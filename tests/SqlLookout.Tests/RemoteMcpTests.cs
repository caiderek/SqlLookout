using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlLookout.Core;
using SqlLookout.Mcp;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>The remote MCP endpoint: who gets in, what each key may see, the key pages, and the two live tools.</summary>
public sealed class RemoteMcpTests : IAsyncLifetime
{
    private static readonly string SqlServer =
        Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
        ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";

    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private WebFixture _web = null!;

    public async Task InitializeAsync()
    {
        _web = await WebFixture.StartAsync();
        await AddInstanceAsync("prod");
        await AddInstanceAsync("dev");
        await _web.Live.RefreshAsync();
        foreach (var name in new[] { "prod", "dev" })
        {
            await _web.History.AppendAsync(name, CollectorCatalog.Connectivity, Now, TimeSpan.Zero, CollectionResult.Ok([]));
            await _web.History.AppendAsync(name, "cpu", Now, TimeSpan.Zero, CollectionResult.Ok([new Sample("cpu_total_percent", null, 40)]));
            await _web.History.UpsertAlertAsync(new AlertRecord($"{name}|disk|C:\\", name, "Warning", $"Disk C:\\ is low on {name}", "9 GB free", Now, Now, null));
        }
    }

    public async Task DisposeAsync() => await _web.DisposeAsync();

    private Task AddInstanceAsync(string name, string server = "localhost") =>
        _web.Settings.SaveInstanceAsync(new InstanceRecord(name, server, AuthModes.Windows, null, EncryptModes.Trust, true, false), null, true);

    private async Task<string> KeyAsync(params string[]? only) =>
        (await _web.Settings.IssueApiKeyAsync("test", DateTime.UtcNow.AddDays(30), only is { Length: > 0 } ? only : null)).Key;

    private async Task<McpClient> ConnectAsync(string key) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("https://localhost/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        }, new HttpClient(_web.NewHandler()), ownsHttpClient: true));

    private async Task<HttpStatusCode> RawPostAsync(string? authorization, string path = "/mcp")
    {
        using var http = new HttpClient(_web.NewHandler()) { BaseAddress = new Uri("https://localhost") };
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return (await http.SendAsync(request)).StatusCode;
    }

    private static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, params (string Name, object Value)[] args)
    {
        var result = await client.CallToolAsync(tool, args.ToDictionary(a => a.Name, a => (object?)a.Value));
        Assert.True(result.IsError != true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    // ---------------------------------------------------------------- who gets in

    [Fact]
    public async Task Without_any_key_the_path_does_not_exist()
    {
        Assert.Equal(HttpStatusCode.NotFound, await RawPostAsync(null));
        Assert.Equal(HttpStatusCode.NotFound, await RawPostAsync("Bearer slk_whatever"));
    }

    [Fact]
    public async Task Missing_wrong_expired_and_revoked_keys_all_get_the_same_401()
    {
        var good = await KeyAsync();
        var (expiredId, expired) = await _web.Settings.IssueApiKeyAsync("old", DateTime.UtcNow.AddDays(-1), null, nowUtc: DateTime.UtcNow.AddDays(-3));
        var (revokedId, revoked) = await _web.Settings.IssueApiKeyAsync("gone", DateTime.UtcNow.AddDays(10), null);
        await _web.Settings.RevokeApiKeyAsync(revokedId);

        Assert.Equal(HttpStatusCode.OK, await RawPostAsync("Bearer " + good));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Basic " + good));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer"));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer slk_" + new string('a', 43)));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + expired));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + revoked));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + good.ToUpperInvariant()));
        Assert.NotEqual(0, expiredId);
    }

    [Fact]
    public async Task A_key_works_without_the_web_sign_in_or_a_form_token_but_other_posts_still_need_a_token()
    {
        var key = await KeyAsync();
        Assert.Equal(HttpStatusCode.OK, await RawPostAsync("Bearer " + key));

        // The same missing token on any page is still refused, and /mcp does not open the page POSTs.
        await _web.SignInAsAdminAsync();
        // A complete, valid form that only lacks the token: refused, and no key is made.
        var page = await _web.PostRawAsync("/settings/mcp/new", ("name", "x"), ("days", "30"), ("all", "1"));
        Assert.Equal(HttpStatusCode.BadRequest, page.Status);
        Assert.Empty(await _web.Settings.ListApiKeysAsync().ContinueWith(t => t.Result.Where(k => k.Name == "x")));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync(null, "/mcp/anything"));
    }

    [Fact]
    public async Task Ten_wrong_keys_lock_that_address_out_even_for_a_right_key()
    {
        var key = await KeyAsync();
        for (var i = 0; i < McpKeyThrottle.MaxFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer slk_wrong" + i));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RawPostAsync("Bearer " + key));
    }

    [Fact]
    public void The_throttle_counts_per_address_and_forgets_after_the_window()
    {
        var t = new McpKeyThrottle();
        for (var i = 0; i < McpKeyThrottle.MaxFailures - 1; i++) t.Failed("a", Now);
        Assert.False(t.IsBlocked("a", Now));
        t.Failed("a", Now);
        Assert.True(t.IsBlocked("a", Now));
        Assert.False(t.IsBlocked("b", Now));
        Assert.True(t.IsBlocked("a", Now + McpKeyThrottle.Window - TimeSpan.FromSeconds(1)));
        Assert.False(t.IsBlocked("a", Now + McpKeyThrottle.Window + TimeSpan.FromSeconds(1)));
    }

    // ---------------------------------------------------------------- key storage

    [Fact]
    public async Task Only_a_hash_of_the_key_is_kept_and_expiry_and_revoking_are_honoured()
    {
        var (id, key) = await _web.Settings.IssueApiKeyAsync("laptop", Now.AddDays(10), ["prod"], nowUtc: Now);
        Assert.StartsWith("slk_", key);

        var stored = await _web.StoredApiKeysAsync();
        Assert.DoesNotContain(key, stored);
        Assert.Contains(SettingsStore.HashApiKey(key), stored);

        Assert.NotNull(await _web.Settings.ValidateApiKeyAsync(key, Now.AddDays(9)));
        Assert.Null(await _web.Settings.ValidateApiKeyAsync(key, Now.AddDays(10)));      // the expiry moment itself is already too late
        Assert.Null(await _web.Settings.ValidateApiKeyAsync(key + "x", Now));
        Assert.Null(await _web.Settings.ValidateApiKeyAsync("", Now));

        var identity = await _web.Settings.ValidateApiKeyAsync(key, Now.AddDays(1));
        Assert.Equal(["prod"], identity!.Instances);
        await _web.Settings.RevokeApiKeyAsync(id, Now.AddDays(2));
        Assert.Null(await _web.Settings.ValidateApiKeyAsync(key, Now.AddDays(3)));
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        var (id, key) = await _web.Settings.IssueApiKeyAsync("x", Now.AddDays(5), null, nowUtc: Now);
        Assert.Null((await _web.Settings.ListApiKeysAsync()).Single(k => k.Id == id).LastUsedUtc);
        await _web.Settings.ValidateApiKeyAsync(key, Now.AddSeconds(1));
        var first = (await _web.Settings.ListApiKeysAsync()).Single(k => k.Id == id).LastUsedUtc;
        await _web.Settings.ValidateApiKeyAsync(key, Now.AddSeconds(30));
        Assert.Equal(first, (await _web.Settings.ListApiKeysAsync()).Single(k => k.Id == id).LastUsedUtc);
        await _web.Settings.ValidateApiKeyAsync(key, Now.AddSeconds(75));
        Assert.NotEqual(first, (await _web.Settings.ListApiKeysAsync()).Single(k => k.Id == id).LastUsedUtc);
    }

    [Fact]
    public async Task A_key_needs_a_name_a_near_enough_expiry_and_some_servers()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _web.Settings.IssueApiKeyAsync("  ", Now.AddDays(5), null, nowUtc: Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => _web.Settings.IssueApiKeyAsync("a", Now.AddDays(-1), null, nowUtc: Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => _web.Settings.IssueApiKeyAsync("a", Now, null, nowUtc: Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => _web.Settings.IssueApiKeyAsync("a", Now.AddDays(SettingsStore.MaxApiKeyDays + 1), null, nowUtc: Now));
        await Assert.ThrowsAsync<InvalidDataException>(() => _web.Settings.IssueApiKeyAsync("a", Now.AddDays(5), [], nowUtc: Now));
        await _web.Settings.IssueApiKeyAsync("a", Now.AddDays(SettingsStore.MaxApiKeyDays), null, nowUtc: Now);
    }

    // ---------------------------------------------------------------- what a key may see

    [Fact]
    public async Task A_key_for_all_servers_sees_only_servers_that_still_exist()
    {
        await using var client = await ConnectAsync(await KeyAsync());
        var names = (await CallAsync(client, "list_instances")).GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("instance").GetString()).Order().ToList();
        Assert.Equal(["dev", "prod"], names);

        await _web.Settings.DeleteInstanceAsync("dev");
        await _web.Live.RefreshAsync();
        var after = (await CallAsync(client, "list_instances")).GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("instance").GetString()).ToList();
        Assert.Equal(["prod"], after);   // the history still holds dev; the key must not
        var problems = await CallAsync(client, "get_problems");
        Assert.All(problems.GetProperty("problems").EnumerateArray(), p => Assert.Equal("prod", p.GetProperty("instance").GetString()));
    }

    [Fact]
    public async Task A_limited_key_sees_nothing_else_and_cannot_learn_other_names()
    {
        await using var client = await ConnectAsync(await KeyAsync("prod"));

        var list = await CallAsync(client, "list_instances");
        Assert.Equal(["prod"], list.GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("instance").GetString()).ToList());
        var problems = await CallAsync(client, "get_problems");
        Assert.Equal(1, problems.GetProperty("count").GetInt32());
        Assert.DoesNotContain("dev", problems.ToString());

        // A name outside the key and a name that does not exist get the very same answer, and it names nothing.
        var outside = await client.CallToolAsync("get_overview", new Dictionary<string, object?> { ["instance"] = "dev" });
        var missing = await client.CallToolAsync("get_overview", new Dictionary<string, object?> { ["instance"] = "nope" });
        Assert.True(outside.IsError);
        Assert.Equal(Text(missing), Text(outside));
        Assert.Contains("Unknown instance or not allowed", Text(outside));
        Assert.DoesNotContain("prod", Text(outside));
        Assert.DoesNotContain("dev", Text(outside).Replace("Unknown instance or not allowed for this key. Use list_instances", ""));

        var history = await client.CallToolAsync("get_metric_history", new Dictionary<string, object?> { ["instance"] = "dev", ["metric"] = "cpu_total_percent" });
        Assert.True(history.IsError);
        var scoped = await client.CallToolAsync("get_problems", new Dictionary<string, object?> { ["instance"] = "dev" });
        Assert.True(scoped.IsError);

        // Case does not matter, and the answer uses the stored spelling.
        var overview = await CallAsync(client, "get_overview", ("instance", "PROD"));
        Assert.Equal("prod", overview.GetProperty("instance").GetString());

        foreach (var tool in new[] { "get_running_queries", "get_blocking" })
        {
            var live = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["instance"] = "dev" });
            Assert.True(live.IsError);
            Assert.Contains("Unknown instance or not allowed", Text(live));
        }
    }

    [Fact]
    public async Task The_remote_server_lists_the_six_read_only_tools()
    {
        await using var client = await ConnectAsync(await KeyAsync());
        var tools = await client.ListToolsAsync();
        Assert.Equal(
            ["get_blocking", "get_metric_history", "get_overview", "get_problems", "get_running_queries", "list_instances"],
            tools.Select(t => t.Name).Order().ToList());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint == true));
    }

    // ---------------------------------------------------------------- the key pages

    [Fact]
    public async Task The_key_page_needs_a_sign_in_shows_a_new_key_once_and_revoking_stops_it_at_once()
    {
        // Not signed in: sent to the sign-in page.
        var anonymous = await _web.GetAsync("/settings/mcp");
        Assert.True(anonymous.Status is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Found, anonymous.Status.ToString());

        await _web.SignInAsAdminAsync();
        var made = await _web.PostAsync("/settings/mcp", "/settings/mcp/new", ("name", "Anna's laptop"), ("days", "30"), ("all", "0"), ("inst", "prod"));
        Assert.Equal(HttpStatusCode.OK, made.Status);
        var key = System.Text.RegularExpressions.Regex.Match(made.Body, "<code>(slk_[A-Za-z0-9_-]+)</code>").Groups[1].Value;
        Assert.NotEmpty(key);
        Assert.Contains("claude mcp add --transport http sqllookout", made.Body);
        Assert.Contains("Anna&#39;s laptop", made.Body);

        var again = await _web.GetAsync("/settings/mcp");
        Assert.DoesNotContain(key, again.Body);
        Assert.Contains("Anna&#39;s laptop", again.Body);

        await using (var client = await ConnectAsync(key))
            Assert.Equal(["prod"], (await CallAsync(client, "list_instances")).GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("instance").GetString()).ToList());

        var id = (await _web.Settings.ListApiKeysAsync()).Single(k => k.Name == "Anna's laptop").Id;
        var revoked = await _web.PostAsync("/settings/mcp", "/settings/mcp/revoke", ("id", id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, revoked.Status);
        Assert.Equal(HttpStatusCode.NotFound, await RawPostAsync("Bearer " + key));   // it was the only key
    }

    [Fact]
    public async Task The_key_form_refuses_an_empty_name_and_ignores_servers_that_do_not_exist()
    {
        await _web.SignInAsAdminAsync();
        var noName = await _web.PostAsync("/settings/mcp", "/settings/mcp/new", ("name", " "), ("days", "30"), ("all", "1"));
        Assert.Equal(HttpStatusCode.BadRequest, noName.Status);
        var noServers = await _web.PostAsync("/settings/mcp", "/settings/mcp/new", ("name", "x"), ("days", "30"), ("all", "0"), ("inst", "ghost"));
        Assert.Equal(HttpStatusCode.BadRequest, noServers.Status);
        Assert.Empty(await _web.Settings.ListApiKeysAsync());
    }

    // ---------------------------------------------------------------- live tools against a real SQL Server

    [Fact]
    public void Chains_are_built_from_the_blocked_list_with_everything_waiting_behind_each_head()
    {
        BlockedSession B(int id, int by, long wait) => new(id, by, wait, "LCK_M_S", "KEY: 5:1", "db", "u", "h", $"q{id}");
        var chains = LiveBlocking.BuildChains(
            [B(11, 10, 500), B(12, 11, 300), B(13, 10, 900), B(21, 20, 100)],
            new Dictionary<int, BlockerSession> { [10] = new(10, "sa", "pc", "app", "sleeping", 1, "db", "UPDATE t") });

        Assert.Equal([10, 20], chains.Select(c => c.HeadSessionId).ToList());          // longest wait first
        Assert.Equal([13, 11, 12], chains[0].Blocked.Select(b => b.SessionId).ToList());
        Assert.Equal(900, chains[0].LongestWaitMs);
        Assert.Equal("UPDATE t", chains[0].Head!.LastText);
        Assert.Null(chains[1].Head);
        Assert.Empty(LiveBlocking.BuildChains([], new Dictionary<int, BlockerSession>()));
    }

    [Fact]
    public async Task Live_tools_report_a_real_lock_chain_with_the_idle_head_blocker_and_the_running_statement()
    {
        await AddInstanceAsync("local");
        await _web.Live.RefreshAsync();
        var table = "SqlLookoutMcpBlk_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new SqlConnection(SqlServer);
        await using var holder = new SqlConnection(SqlServer);
        await using var waiter = new SqlConnection(SqlServer);
        await admin.OpenAsync(); await holder.OpenAsync(); await waiter.OpenAsync();
        async Task Run(SqlConnection c, string sql) { await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        await Run(admin, $"CREATE TABLE tempdb.dbo.[{table}] (Id int PRIMARY KEY);");
        try
        {
            int holderSession;
            await using (var id = holder.CreateCommand()) { id.CommandText = "SELECT @@SPID;"; holderSession = Convert.ToInt32(await id.ExecuteScalarAsync()); }
            await Run(holder, $"BEGIN TRAN; INSERT tempdb.dbo.[{table}] VALUES (1);");   // then it sits idle with the transaction open; the INSERT is its last statement
            await using var wait = waiter.CreateCommand();
            wait.CommandText = $"SELECT * FROM tempdb.dbo.[{table}];";
            var blocked = wait.ExecuteNonQueryAsync();

            await using var client = await ConnectAsync(await KeyAsync());
            JsonElement chain = default;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(300);
                var result = await CallAsync(client, "get_blocking", ("instance", "local"));
                var mine = result.GetProperty("chains").EnumerateArray().Where(c => c.GetProperty("headSession").GetInt32() == holderSession).ToList();
                if (mine.Count > 0) { chain = mine[0]; break; }
            }
            Assert.Equal(JsonValueKind.Object, chain.ValueKind);
            Assert.Equal("sleeping", chain.GetProperty("head").GetProperty("status").GetString());
            Assert.Equal(1, chain.GetProperty("head").GetProperty("openTransactions").GetInt32());
            Assert.Contains("INSERT", chain.GetProperty("head").GetProperty("lastStatement").GetString());
            Assert.Equal(1, chain.GetProperty("waitingSessions").GetInt32());
            Assert.Contains(table, chain.GetProperty("waiting")[0].GetProperty("text").GetString());

            var running = await CallAsync(client, "get_running_queries", ("instance", "local"));
            var waiting = running.GetProperty("running").EnumerateArray().Single(q => q.GetProperty("text").GetString()!.Contains(table));
            Assert.Equal(holderSession, waiting.GetProperty("blockedBySession").GetInt32());
            Assert.True(running.GetProperty("running").GetArrayLength() <= LiveQueries.RunningLimit);

            await Run(holder, "ROLLBACK;");
            await blocked;
        }
        finally
        {
            // If an assertion failed above, the open transaction would block the DROP and hide the real failure.
            try { await Run(holder, "IF @@TRANCOUNT > 0 ROLLBACK;"); } catch (SqlException) { }
            await Run(admin, $"DROP TABLE IF EXISTS tempdb.dbo.[{table}];");
        }
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_gives_a_short_error_that_repeats_nothing_from_the_exception()
    {
        await AddInstanceAsync("unreachable", server: "tcp:127.0.0.1,1");
        await _web.Live.RefreshAsync();
        await using var client = await ConnectAsync(await KeyAsync());
        var result = await client.CallToolAsync("get_running_queries", new Dictionary<string, object?> { ["instance"] = "unreachable" });
        Assert.True(result.IsError);
        Assert.DoesNotContain("127.0.0.1", Text(result));
        Assert.DoesNotContain("tcp:", Text(result));
    }

    // ---------------------------------------------------------------- methods, paths, keys and the throttle

    private static async Task<HttpStatusCode> SendAsync(HttpClient http, HttpMethod method, string path, string? authorization)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
            request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return (await http.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Every_method_and_spelling_of_the_path_needs_a_key()
    {
        await KeyAsync();
        using var http = new HttpClient(_web.NewHandler()) { BaseAddress = new Uri("https://localhost") };
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete, HttpMethod.Put, HttpMethod.Options })
            foreach (var path in new[] { "/mcp", "/MCP", "/Mcp/", "/mcp/sse", "/mcp/message" })
                Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(http, method, path, null));
    }

    [Fact]
    public async Task A_valid_key_gets_plain_answers_for_other_methods_and_paths_and_never_the_web_sign_in()
    {
        var auth = "Bearer " + await KeyAsync();
        using var http = new HttpClient(_web.NewHandler()) { BaseAddress = new Uri("https://localhost") };
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await SendAsync(http, HttpMethod.Get, "/mcp", auth));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await SendAsync(http, HttpMethod.Delete, "/MCP/", auth));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(http, HttpMethod.Post, "/mcp/anything", auth));
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(http, HttpMethod.Get, "/mcp/sse", auth));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(http, HttpMethod.Post, "/MCP/", auth));
    }

    [Fact]
    public async Task A_signed_in_browser_without_a_key_is_not_let_in()
    {
        await KeyAsync();
        await _web.SignInAsAdminAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(_web.Client, HttpMethod.Post, "/mcp", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(_web.Client, HttpMethod.Get, "/mcp", null));
    }

    [Fact]
    public async Task A_valid_key_does_not_open_the_pages_or_skip_their_form_token()
    {
        var key = await KeyAsync();
        await _web.SignInAsAdminAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/settings/mcp/new")
        {
            Content = new FormUrlEncodedContent([new("name", "sneaky"), new("days", "30"), new("all", "1")]),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        var response = await _web.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(await _web.Settings.ListApiKeysAsync(), k => k.Name == "sneaky");

        // Without a sign-in a key does not stand in for one on a page either.
        using var anonymous = new HttpClient(_web.NewHandler()) { BaseAddress = new Uri("https://localhost") };
        using var page = new HttpRequestMessage(HttpMethod.Get, "/settings/mcp");
        page.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.SendAsync(page)).StatusCode);
    }

    [Fact]
    public async Task Key_matching_is_case_sensitive()
    {
        var key = await KeyAsync();
        var chars = key.ToCharArray();
        var i = Enumerable.Range(4, chars.Length - 4).First(n => char.IsLetter(chars[n]));
        chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + new string(chars)));
        Assert.Equal(HttpStatusCode.OK, await RawPostAsync("Bearer " + key));
    }

    [Fact]
    public async Task Only_expired_or_revoked_keys_means_the_path_is_gone_again()
    {
        var (id, _) = await _web.Settings.IssueApiKeyAsync("a", DateTime.UtcNow.AddDays(1), null);
        var (_, old) = await _web.Settings.IssueApiKeyAsync("b", DateTime.UtcNow.AddDays(-1), null, nowUtc: DateTime.UtcNow.AddDays(-5));
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + old));
        await _web.Settings.RevokeApiKeyAsync(id);
        Assert.Equal(HttpStatusCode.NotFound, await RawPostAsync("Bearer " + old));
    }

    [Fact]
    public async Task Clients_with_old_keys_or_no_header_are_not_counted_as_guessers()
    {
        var good = await KeyAsync();
        var (_, expired) = await _web.Settings.IssueApiKeyAsync("old", DateTime.UtcNow.AddDays(-1), null, nowUtc: DateTime.UtcNow.AddDays(-5));
        var (revokedId, revoked) = await _web.Settings.IssueApiKeyAsync("gone", DateTime.UtcNow.AddDays(5), null);
        await _web.Settings.RevokeApiKeyAsync(revokedId);
        for (var i = 0; i < McpKeyThrottle.MaxFailures + 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + expired));
            Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + revoked));
            Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync(null));
        }
        Assert.Equal(HttpStatusCode.OK, await RawPostAsync("Bearer " + good));
    }

    [Fact]
    public void Throttle_buckets_ipv6_by_network_and_maps_ipv4_inside_ipv6()
    {
        static string K(string a) => McpKeyThrottle.ClientKey(IPAddress.Parse(a));
        Assert.Equal("", McpKeyThrottle.ClientKey(null));
        Assert.Equal("1.2.3.4", K("1.2.3.4"));
        Assert.Equal("1.2.3.4", K("::ffff:1.2.3.4"));
        Assert.Equal(K("2001:db8:1:2::1"), K("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        Assert.NotEqual(K("2001:db8:1:2::1"), K("2001:db8:1:3::1"));
    }

    [Fact]
    public void A_flood_of_addresses_cannot_wipe_a_lockout_that_is_in_force()
    {
        var t = new McpKeyThrottle();
        for (var i = 0; i < McpKeyThrottle.MaxFailures; i++) t.Failed("attacker", Now);
        for (var i = 0; i < 12_000; i++) t.Failed("noise" + i, Now.AddSeconds(1 + i % 5));
        Assert.True(t.IsBlocked("attacker", Now.AddSeconds(10)));
    }

    [Fact]
    public async Task Two_clients_with_different_keys_never_see_each_others_servers_even_when_they_run_together()
    {
        await using var prod = await ConnectAsync(await KeyAsync("prod"));
        await using var dev = await ConnectAsync(await KeyAsync("dev"));
        static async Task Check(McpClient client, string expected)
        {
            for (var i = 0; i < 25; i++)
            {
                var names = (await CallAsync(client, "list_instances")).GetProperty("instances").EnumerateArray().Select(n => n.GetProperty("instance").GetString()).ToList();
                Assert.Equal([expected], names);
                var problems = await CallAsync(client, "get_problems");
                Assert.All(problems.GetProperty("problems").EnumerateArray(), p => Assert.Equal(expected, p.GetProperty("instance").GetString()));
            }
        }
        await Task.WhenAll(Check(prod, "prod"), Check(dev, "dev"), Check(prod, "prod"), Check(dev, "dev"));
    }

    [Fact]
    public async Task A_live_read_is_shared_for_a_few_seconds_and_a_second_caller_is_told_when_the_server_is_busy()
    {
        var clock = Now;
        var gate = new LiveToolGate(() => clock);
        var reads = 0;
        Task<string> Read() { reads++; return Task.FromResult("answer" + reads); }

        Assert.Equal("answer1", await gate.RunAsync("prod", "running", Read, CancellationToken.None));
        Assert.Equal("answer1", await gate.RunAsync("PROD", "running", Read, CancellationToken.None));   // cached
        Assert.Equal("answer2", await gate.RunAsync("prod", "blocking", Read, CancellationToken.None));  // another tool
        Assert.Equal("answer3", await gate.RunAsync("dev", "running", Read, CancellationToken.None));    // another server
        clock += LiveToolGate.CacheFor + TimeSpan.FromSeconds(1);
        Assert.Equal("answer4", await gate.RunAsync("prod", "running", Read, CancellationToken.None));

        var release = new TaskCompletionSource<string>();
        var slow = gate.RunAsync("slow", "running", () => release.Task, CancellationToken.None);
        var busy = await Assert.ThrowsAsync<McpException>(() => gate.RunAsync("slow", "blocking", Read, CancellationToken.None));
        Assert.Equal(LiveToolGate.Busy, busy.Message);
        release.SetResult("done");
        Assert.Equal("done", await slow);
        Assert.Equal("answer5", await gate.RunAsync("slow", "blocking", Read, CancellationToken.None));   // free again
    }

    [Fact]
    public async Task A_damaged_key_row_refuses_the_key_instead_of_failing_the_request()
    {
        var (_, key) = await _web.Settings.IssueApiKeyAsync("x", DateTime.UtcNow.AddDays(5), null);
        await _web.ExecuteRawAsync("UPDATE ApiKeys SET Instances = 'not json';");
        Assert.Equal(HttpStatusCode.Unauthorized, await RawPostAsync("Bearer " + key));
    }
}
