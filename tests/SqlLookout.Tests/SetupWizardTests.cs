using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SqlLookout.Service;
using SqlLookout.Web;

namespace SqlLookout.Tests;

public sealed class SetupWizardTests : IAsyncLifetime
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sqllookout-wizard-{Guid.NewGuid():N}");
    private readonly ServiceSetupTests.FakeHost _host;
    private readonly FakeRunner _runner = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _stopped;

    public SetupWizardTests()
    {
        Directory.CreateDirectory(_root);
        _host = new ServiceSetupTests.FakeHost(_root);
    }

    /// <summary>Records what it was asked to run, and copies the temp files it was pointed at before the wizard deletes them.</summary>
    private sealed class FakeRunner : ISetupRunner
    {
        public List<string> Args { get; private set; } = [];
        public Dictionary<string, string> FileContents { get; } = [];
        public List<string> FilePaths { get; } = [];
        public int Calls { get; private set; }
        public (int ExitCode, string Output) Result { get; set; } = (0, "all done");

        public Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls++;
            Args = [.. args];
            for (var i = 0; i + 1 < args.Count; i++)
            {
                if (args[i] is "--password-file" or "--store-connection-file")
                {
                    FilePaths.Add(args[i + 1]);
                    FileContents[args[i]] = File.ReadAllText(args[i + 1]);
                }
            }
            return Task.FromResult(Result);
        }
    }

    public async Task InitializeAsync()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        SetupWizard.Map(_app, new SetupWizard(_host, _runner, source, Token, () => Interlocked.Increment(ref _stopped)));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object body, bool header = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{Token}{path}") { Content = JsonContent.Create(body) };
        if (header) request.Headers.Add("X-Wizard", "1");
        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private void Installed(string? storeFile = null, Version? installedVersion = null, Version? sourceVersion = null)
    {
        var dir = Path.Combine(_root, "installed");
        Directory.CreateDirectory(dir);
        _host.Exists = true;
        _host.ImagePath = ServiceCommandLine.Build(Path.Combine(dir, "sqllookout.exe"), new SetupOptions(), Path.Combine(_root, "dat"), storeFile);
        _host.Versions = f => f.EndsWith("installed", StringComparison.OrdinalIgnoreCase) ? installedVersion ?? new Version(0, 9, 18) : sourceVersion ?? new Version(0, 9, 19);
    }

    // ---- pages ----

    [Fact]
    public async Task Not_installed_shows_the_install_form_with_the_default_folders()
    {
        var html = await _client.GetStringAsync($"/{Token}?lang=en");

        Assert.Contains("id=\"install\"", html);
        Assert.DoesNotContain("id=\"upgrade\"", html);
        Assert.Contains(WebUtility.HtmlEncode(Path.Combine(_host.ProgramFilesDir, "SqlLookout")), html);
        Assert.Contains(WebUtility.HtmlEncode(Path.Combine(_host.ProgramDataDir, "SqlLookout")), html);
        Assert.Contains("value=\"8443\"", html);
        Assert.Contains("Windows will ask for permission when you press Install.", html);
    }

    [Fact]
    public async Task The_page_comes_in_traditional_chinese_too()
    {
        var html = await _client.GetStringAsync($"/{Token}?lang=zh-TW");

        Assert.Contains("lang=\"zh-Hant\"", html);
        Assert.Contains("SqlLookout 安裝精靈", html);
        Assert.Contains("按下「安裝」時,Windows 會要求確認權限。", html);
        Assert.DoesNotContain("Windows will ask for permission", html);
    }

    [Fact]
    public async Task Installed_shows_upgrade_and_uninstall_instead_of_the_install_form()
    {
        Installed();

        var html = await _client.GetStringAsync($"/{Token}?lang=en");

        Assert.Contains("id=\"upgrade\"", html);
        Assert.Contains("id=\"uninstall\"", html);
        Assert.DoesNotContain("id=\"install\"", html);
        Assert.DoesNotContain("id=\"upgrade\" type=\"button\" disabled", html);
        Assert.Contains("0.9.19", html);
        Assert.Contains("0.9.18", html);
        Assert.Contains("a file in the data folder (SQLite)", html);
    }

    [Fact]
    public async Task An_older_folder_cannot_be_used_to_upgrade()
    {
        Installed(installedVersion: new Version(0, 9, 19), sourceVersion: new Version(0, 9, 18));

        var html = await _client.GetStringAsync($"/{Token}?lang=en");

        Assert.Contains("id=\"upgrade\" type=\"button\" disabled", html);
        Assert.Contains("cannot be used to upgrade", html);
    }

    [Fact]
    public async Task An_installed_service_that_keeps_its_data_in_sql_server_says_so()
    {
        Installed(storeFile: Path.Combine(_root, "dat", "store.connection"));

        Assert.Contains("a SQL Server database", await _client.GetStringAsync($"/{Token}?lang=en"));
    }

    [Fact]
    public async Task Nothing_in_the_page_is_taken_from_the_request_unescaped()
    {
        _host.ProgramFilesDir.ToString();
        Installed();
        _host.ImagePath = ServiceCommandLine.Build(Path.Combine(_root, "<script>alert(1)</script>", "sqllookout.exe"), new SetupOptions(), Path.Combine(_root, "dat"));

        var html = await _client.GetStringAsync($"/{Token}?lang=\"><script>x</script>");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<script>x</script>", html);
    }

    // ---- who may talk to it ----

    [Fact]
    public async Task A_wrong_path_gets_nothing()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/wrong-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/{Token}x")).StatusCode);
    }

    [Fact]
    public async Task Another_host_name_is_refused_so_a_rebinding_page_cannot_reach_it()
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/{Token}");
        get.Headers.Host = "evil.example.com";
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(get)).StatusCode);

        using var post = new HttpRequestMessage(HttpMethod.Post, $"/{Token}/run") { Content = JsonContent.Create(new { action = "uninstall" }) };
        post.Headers.Host = "evil.example.com";
        post.Headers.Add("X-Wizard", "1");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(post)).StatusCode);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task A_change_needs_the_custom_header_and_a_json_body()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/run", new { action = "uninstall" }, header: false)).Status);

        using var form = new HttpRequestMessage(HttpMethod.Post, $"/{Token}/run")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "uninstall" }),
        };
        form.Headers.Add("X-Wizard", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(form)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/exit", new { }, header: false)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/check-store", new { }, header: false)).Status);
        Assert.Equal(0, _runner.Calls);
        Assert.Equal(0, _stopped);
    }

    // ---- running the commands ----

    [Fact]
    public async Task Install_runs_the_command_line_the_options_describe_and_hands_secrets_over_in_files_only()
    {
        var (status, body) = await PostAsync("/run", new
        {
            action = "install", lang = "en", dir = @"C:\Apps\SqlLookout", data = @"D:\Data\SqlLookout", port = "9443",
            allComputers = false, firewall = true, account = @"DOM\svc", password = "svc-secret-pw",
            store = new { server = "db01", database = "SqlLookoutStore", auth = "sql", user = "slk", password = "store-secret-pw", trust = true },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("https://localhost:9443/", body.GetProperty("url").GetString());
        Assert.Equal("all done", body.GetProperty("output").GetString());

        Assert.Equal("install", _runner.Args[0]);
        Assert.Equal(@"C:\Apps\SqlLookout", _runner.Args[_runner.Args.IndexOf("--dir") + 1]);
        Assert.Equal(@"D:\Data\SqlLookout", _runner.Args[_runner.Args.IndexOf("--data") + 1]);
        Assert.Equal("9443", _runner.Args[_runner.Args.IndexOf("--port") + 1]);
        Assert.Equal("127.0.0.1", _runner.Args[_runner.Args.IndexOf("--listen") + 1]);
        Assert.Equal(@"DOM\svc", _runner.Args[_runner.Args.IndexOf("--account") + 1]);
        Assert.DoesNotContain("--no-firewall", _runner.Args);

        Assert.DoesNotContain(_runner.Args, a => a.Contains("svc-secret-pw") || a.Contains("store-secret-pw"));
        Assert.Equal("svc-secret-pw", _runner.FileContents["--password-file"]);
        Assert.Contains("store-secret-pw", _runner.FileContents["--store-connection-file"]);
        Assert.All(_runner.FilePaths, f => Assert.False(File.Exists(f), "a temp file with a secret was left behind"));
    }

    [Fact]
    public async Task Install_with_the_defaults_listens_on_all_computers_and_asks_for_no_account_or_store()
    {
        var (_, body) = await PostAsync("/run", new { action = "install", lang = "en", port = "8443", allComputers = true, firewall = false });

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("0.0.0.0", _runner.Args[_runner.Args.IndexOf("--listen") + 1]);
        Assert.Contains("--no-firewall", _runner.Args);
        Assert.DoesNotContain("--account", _runner.Args);
        Assert.DoesNotContain("--store-connection-file", _runner.Args);
        Assert.DoesNotContain("--dir", _runner.Args);
        Assert.Empty(_runner.FilePaths);
    }

    [Theory]
    [InlineData("abc", "must be a number")]
    [InlineData("0", "port")]
    [InlineData("70000", "port")]
    public async Task A_bad_port_is_refused_before_windows_is_asked_for_anything(string port, string expected)
    {
        var (_, body) = await PostAsync("/run", new { action = "install", lang = "en", port, allComputers = true });

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains(expected, body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _runner.Calls);
    }

    [Theory]
    [InlineData("db01", "master", "windows", "", "", "system database")]
    [InlineData("db01", "TempDB", "sql", "slk", "pw", "system database")]
    [InlineData("", "SqlLookoutStore", "windows", "", "", "SQL Server name")]
    [InlineData("  ", "SqlLookoutStore", "sql", "slk", "pw", "SQL Server name")]
    [InlineData("db01", "", "windows", "", "", "database name")]
    [InlineData("db01", "SqlLookoutStore", "sql", "", "pw", "login name")]
    [InlineData("db01", "SqlLookoutStore", "sql", "slk", "", "password")]
    public async Task A_store_that_is_incomplete_or_would_not_be_a_database_of_its_own_is_refused_before_windows_is_asked(
        string server, string database, string auth, string user, string password, string expected)
    {
        var store = new { server, database, auth, user, password, trust = true };
        var (_, body) = await PostAsync("/run", new { action = "install", lang = "en", port = "8443", allComputers = true, store });

        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains(expected, body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task Windows_authentication_builds_a_connection_without_any_login_or_password()
    {
        await PostAsync("/run", new { action = "install", lang = "en", port = "8443", allComputers = true, store = new { server = @"DB01\SQL2019", database = "Store1", auth = "windows", user = "ignored", password = "ignored-pw", trust = false } });

        var text = _runner.FileContents["--store-connection-file"];
        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(text);
        Assert.Equal(@"DB01\SQL2019", parsed.DataSource);
        Assert.Equal("Store1", parsed.InitialCatalog);
        Assert.True(parsed.IntegratedSecurity);
        Assert.False(parsed.TrustServerCertificate);
        Assert.Equal("", parsed.UserID);
        Assert.DoesNotContain("ignored", text);
    }

    [Theory]
    [InlineData("p;a=ss")]
    [InlineData("it's \"quoted\"")]
    [InlineData("end;Database=master")]
    [InlineData("  spaces  ")]
    public async Task A_password_with_odd_characters_arrives_as_typed_and_cannot_change_the_other_settings(string password)
    {
        await PostAsync("/run", new { action = "install", lang = "en", port = "8443", allComputers = true, store = new { server = "db01", database = "Store1", auth = "sql", user = "slk", password, trust = true } });

        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_runner.FileContents["--store-connection-file"]);
        Assert.Equal(password, parsed.Password);
        Assert.Equal("Store1", parsed.InitialCatalog);
        Assert.Equal("slk", parsed.UserID);
        Assert.Equal("db01", parsed.DataSource);
    }

    [Fact]
    public async Task The_install_form_asks_for_the_server_the_database_and_how_to_sign_in()
    {
        var html = await _client.GetStringAsync($"/{Token}?lang=en");

        foreach (var id in new[] { "sqlServer", "sqlDb", "sqlUser", "sqlPass", "sqlTrust", "test" })
            Assert.Contains($"id=\"{id}\"", html);
        Assert.Contains("name=\"sqlauth\" value=\"sql\" checked", html);
        Assert.Contains("name=\"sqlauth\" value=\"windows\"", html);
        Assert.DoesNotContain("id=\"conn\"", html);
        Assert.Contains("value=\"SqlLookoutStore\"", html);

        var zh = await _client.GetStringAsync($"/{Token}?lang=zh-TW");
        Assert.Contains("Windows 驗證", zh);
        Assert.Contains("SQL Server 登入(帳號與密碼)", zh);
    }

    [Fact]
    public async Task An_account_without_a_password_is_refused_unless_it_is_a_managed_service_account()
    {
        var (_, refused) = await PostAsync("/run", new { action = "install", lang = "en", port = "8443", allComputers = true, account = @"DOM\svc" });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(0, _runner.Calls);

        var (_, accepted) = await PostAsync("/run", new { action = "install", lang = "en", port = "8443", allComputers = true, account = @"DOM\svc$" });
        Assert.True(accepted.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Upgrade_and_uninstall_run_their_commands_and_nothing_else_is_accepted()
    {
        await PostAsync("/run", new { action = "upgrade", lang = "en" });
        Assert.Equal(["upgrade"], _runner.Args);

        await PostAsync("/run", new { action = "uninstall", lang = "en", deleteData = true });
        Assert.Equal(["uninstall", "--delete-data"], _runner.Args);

        await PostAsync("/run", new { action = "uninstall", lang = "en", deleteData = false });
        Assert.Equal(["uninstall"], _runner.Args);

        var calls = _runner.Calls;
        foreach (var action in new[] { "format-c", "", "INSTALL", "install --dir x" })
        {
            var (_, body) = await PostAsync("/run", new { action, lang = "en" });
            Assert.False(body.GetProperty("ok").GetBoolean());
        }
        Assert.Equal(calls, _runner.Calls);
    }

    [Fact]
    public async Task Declined_permission_and_failures_are_reported_in_plain_words()
    {
        _runner.Result = (ElevatedSetupRunner.Declined, "ignored");
        var (_, declined) = await PostAsync("/run", new { action = "upgrade", lang = "en" });
        Assert.False(declined.GetProperty("ok").GetBoolean());
        Assert.Contains("declined", declined.GetProperty("message").GetString());

        _runner.Result = (1, "Could not create the service (sc.exe exit code 1073)");
        var (_, failed) = await PostAsync("/run", new { action = "install", lang = "en", port = "8443", allComputers = true });
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Contains("did not finish", failed.GetProperty("message").GetString());
        Assert.Contains("1073", failed.GetProperty("output").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("url").ValueKind);
    }

    [Fact]
    public async Task Messages_come_back_in_the_page_language()
    {
        var (_, body) = await PostAsync("/run", new { action = "install", lang = "zh-TW", port = "abc", allComputers = true });
        Assert.Contains("埠必須是", body.GetProperty("message").GetString());
    }

    // ---- the connection test and closing ----

    [Fact]
    public async Task The_connection_test_reports_what_the_server_said()
    {
        var (_, empty) = await PostAsync("/check-store", new { lang = "en", store = new { server = "  ", database = "SqlLookoutStore", auth = "windows" } });
        Assert.False(empty.GetProperty("ok").GetBoolean());
        var (_, nothing) = await PostAsync("/check-store", new { lang = "en" });
        Assert.False(nothing.GetProperty("ok").GetBoolean());

        var (_, system) = await PostAsync("/check-store", new { lang = "en", store = new { server = "x", database = "tempdb", auth = "windows" } });
        Assert.False(system.GetProperty("ok").GetBoolean());
        Assert.Empty(_host.StoreChecked);

        var good = new { lang = "en", store = new { server = "db01", database = "SqlLookoutStore", auth = "windows", trust = true } };
        var (_, ok) = await PostAsync("/check-store", good);
        Assert.True(ok.GetProperty("ok").GetBoolean());
        var checkedText = Assert.Single(_host.StoreChecked);
        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(checkedText);
        Assert.Equal("db01", parsed.DataSource);
        Assert.True(parsed.IntegratedSecurity);
        Assert.Equal([false], _host.StoreCreate);

        _host.StoreMissing = true;
        var (_, willCreate) = await PostAsync("/check-store", good);
        Assert.True(willCreate.GetProperty("ok").GetBoolean());
        Assert.Contains("created", willCreate.GetProperty("message").GetString());
        _host.StoreMissing = false;

        _host.StoreProblem = "Cannot use the store database: login failed";
        var (_, bad) = await PostAsync("/check-store", good);
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Contains("login failed", bad.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Close_stops_the_wizard()
    {
        var (status, _) = await PostAsync("/exit", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, _stopped);
    }

    // ---- pieces the wizard relies on ----

    [Theory]
    [InlineData("install", "install")]
    [InlineData(@"C:\Program Files\x", "\"C:\\Program Files\\x\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_the_way_windows_reads_them(string arg, string expected)
    {
        Assert.Equal(expected, ElevatedSetupRunner.Quote(arg));
        Assert.Equal([arg], ServiceCommandLine.Split(ElevatedSetupRunner.Quote(arg)).Count == 0 ? [""] : ServiceCommandLine.Split(ElevatedSetupRunner.Quote(arg)));
    }

    [Fact]
    public void Every_setup_command_accepts_an_output_file_and_the_tee_writes_to_both()
    {
        foreach (var command in new[] { "install", "upgrade", "uninstall" })
        {
            var (o, e) = SetupOptionsParser.Parse(command, [command, "--output-file", "out.txt"]);
            Assert.Null(e);
            Assert.Equal(Path.GetFullPath("out.txt"), o!.OutputFile);
        }

        var a = new StringWriter();
        var b = new StringWriter();
        var tee = new TeeTextWriter(a, b);
        tee.Write("x");
        tee.WriteLine("line");
        tee.Write('!');
        Assert.Equal("xline" + Environment.NewLine + "!", a.ToString());
        Assert.Equal(a.ToString(), b.ToString());
    }
}
