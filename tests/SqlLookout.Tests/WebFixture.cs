using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SqlLookout.Service;
using SqlLookout.Storage;
using SqlLookout.Web;

namespace SqlLookout.Tests;

/// <summary>Keeps cookies between requests, like a browser would. Ignores Secure/path rules on purpose (the test server is plain HTTP).</summary>
public sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly Dictionary<string, string> _cookies = [];

    public void Clear() => _cookies.Clear();
    public bool Has(string name) => _cookies.ContainsKey(name);
    public string? Get(string name) => _cookies.GetValueOrDefault(name);
    public void Set(string name, string value) => _cookies[name] = value;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_cookies.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        var response = await base.SendAsync(request, ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in setCookies)
            {
                var first = header.Split(';')[0];
                var eq = first.IndexOf('=');
                var name = first[..eq];
                var value = first[(eq + 1)..];
                if (value.Length == 0 || header.Contains("1970", StringComparison.Ordinal)) _cookies.Remove(name);
                else _cookies[name] = value;
            }
        }
        return response;
    }
}

public sealed partial class WebFixture : IAsyncDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-web-{Guid.NewGuid():N}.db");
    public SqlLookout.Service.DiagnosticLog Diagnostic { get; }
    private CookieHandler _cookies = null!;
    private WebApplication _app = null!;

    public HttpClient Client { get; private set; } = null!;
    public SettingsStore Settings { get; private set; } = null!;
    public IHistoryStore History { get; private set; } = null!;
    public LiveConfig Live { get; private set; } = null!;
    public string DatabaseFile => _file;

    /// <summary>True when SQLLOOKOUT_TEST_STORE=sqlserver put this fixture on a SQL Server database instead of the SQLite file.</summary>
    public bool IsSqlServer => _db is not null;

    private SqlServerTestDb? _db;

    /// <summary>Everything the store holds as one text, to prove a secret is not in it: the SQLite file with its log, or every table as JSON.</summary>
    public async Task<string> StoredTextAsync()
    {
        if (_db is not null)
        {
            var tables = new[] { "Users", "Instances", "Settings", "ApiKeys", "State", "Alerts", "AlertLog" };
            var query = "SELECT " + string.Join(" + ", tables.Select(t => $"COALESCE((SELECT * FROM {t} FOR JSON PATH), '')"));
            return (string?)await _db.ScalarAsync(query) ?? "";
        }
        var text = System.Text.Encoding.UTF8.GetString(await ReadSharedAsync(_file));
        var wal = _file + "-wal";
        return File.Exists(wal) ? text + System.Text.Encoding.UTF8.GetString(await ReadSharedAsync(wal)) : text;
    }

    /// <summary>The stored key rows as "hash|name|servers" lines.</summary>
    public async Task<string> StoredApiKeysAsync()
    {
        if (_db is not null)
            return (string?)await _db.ScalarAsync("SELECT COALESCE(STRING_AGG(KeyHash + '|' + Name + '|' + COALESCE(Instances, ''), CHAR(10)), '') FROM ApiKeys;") ?? "";
        await using var raw = new SqliteConnection($"Data Source={_file}");
        await raw.OpenAsync();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT group_concat(KeyHash || '|' || Name || '|' || ifnull(Instances, ''), char(10)) FROM ApiKeys;";
        return (string?)await cmd.ExecuteScalarAsync() ?? "";
    }

    /// <summary>Changes the store behind the program's back, to test how it copes with damaged rows. The statement must work on both backends.</summary>
    public async Task ExecuteRawAsync(string sql)
    {
        if (_db is not null)
        {
            await _db.ExecuteAsync(sql);
            return;
        }
        await using var raw = new SqliteConnection($"Data Source={_file}");
        await raw.OpenAsync();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    /// <summary>Set to make the next requests look like they come from this address (for the setup page's local-only rule).</summary>
    public IPAddress? RemoteAddress { get; set; }

    private WebFixture() { Diagnostic = new(Path.Combine(Path.GetTempPath(), $"sqllookout-diag-{Guid.NewGuid():N}.log")); }

    public static async Task<WebFixture> StartAsync() => await new WebFixture().BuildAsync();

    private async Task<WebFixture> BuildAsync()
    {
        if (Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_STORE") == "sqlserver")
        {
            _db = await SqlServerTestDb.CreateAsync();
            Settings = SettingsStore.ForSqlServer(_db.ConnectionString, new FakeSecretProtector());
            await Settings.InitializeAsync();
            History = new SqlServerHistoryStore(_db.ConnectionString);
        }
        else
        {
            Settings = new SettingsStore(_file, new FakeSecretProtector());
            await Settings.InitializeAsync();
            History = new SqliteHistoryStore(_file);
        }
        await History.InitializeAsync();
        Live = new LiveConfig(Settings);
        await Live.RefreshAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(Settings);
        builder.Services.AddSingleton<IHistoryStore>(History);
        builder.Services.AddSingleton(Live);
        builder.Services.AddSingleton(Diagnostic);
        SqlLookoutWeb.AddServices(builder.Services);
        _app = builder.Build();
        _app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = RemoteAddress;
            return next(ctx);
        });
        SqlLookoutWeb.Map(_app);
        await _app.StartAsync();

        _cookies = new CookieHandler(_app.GetTestServer().CreateHandler());
        Client = new HttpClient(_cookies) { BaseAddress = new Uri("https://localhost") };
        return this;
    }

    /// <summary>A handler into the running app with no cookies, for clients that sign in some other way (the MCP endpoint).</summary>
    public HttpMessageHandler NewHandler() => _app.GetTestServer().CreateHandler();

    public void ForgetCookies() => _cookies.Clear();
    public string? Cookie(string name) => _cookies.Get(name);
    public void SetCookie(string name, string value) => _cookies.Set(name, value);

    public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> GetAsync(string path)
    {
        var response = await Client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    /// <summary>Loads <paramref name="tokenPage"/> to get a fresh form token (as a browser would), then posts the form.</summary>
    public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> PostAsync(
        string tokenPage, string path, params (string Key, string Value)[] fields)
    {
        var page = await Client.GetAsync(tokenPage);
        var html = await page.Content.ReadAsStringAsync();
        var match = TokenPattern().Match(html);
        if (!match.Success) throw new InvalidOperationException($"No form token on {tokenPage} (status {(int)page.StatusCode}).");
        return await PostRawAsync(path, [("__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups[1].Value)), .. fields]);
    }

    public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> PostRawAsync(string path, params (string Key, string Value)[] fields)
    {
        var response = await Client.PostAsync(path, new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))));
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    /// <summary>Creates the administrator through the real setup page and signs in.</summary>
    public async Task SignInAsAdminAsync(string password = "a-long-enough-password")
    {
        var r = await PostAsync("/setup", "/setup", ("username", "admin"), ("password", password), ("confirm", password));
        if (r.Status != HttpStatusCode.Redirect) throw new InvalidOperationException("Setup failed: " + r.Body);
    }

    /// <summary>Where a redirect points, as path and query (the framework may send it as an absolute address).</summary>
    public static string? Location(HttpResponseMessage r)
    {
        var location = r.Headers.Location;
        if (location is null) return null;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Settings.DisposeAsync();
        await History.DisposeAsync();
        if (_db is not null) await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }
}
