using System.Net;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public class WebLanguageTests : IAsyncLifetime
{
    private WebFixture _web = null!;

    public async Task InitializeAsync() => _web = await WebFixture.StartAsync();
    public async Task DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Setup_page_can_be_previewed_in_chinese_and_the_choice_becomes_the_tool_language()
    {
        var english = await _web.GetAsync("/setup");
        Assert.Contains("Welcome to SqlLookout", english.Body);
        Assert.Contains("<html lang=\"en\">", english.Body);

        var chinese = await _web.GetAsync("/setup?lang=zh-TW");
        Assert.Contains("歡迎使用 SqlLookout", chinese.Body);
        Assert.Contains("<html lang=\"zh-Hant\">", chinese.Body);
        Assert.Contains("English", chinese.Body);                                   // the way back is offered
        Assert.Contains("action=\"/setup?lang=zh-TW\"", chinese.Body);

        // Errors on that page are in the same language.
        var mismatch = await _web.PostAsync("/setup?lang=zh-TW", "/setup?lang=zh-TW", ("username", "admin"), ("password", "a-long-enough-password"), ("confirm", "different-long-password"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
        Assert.Contains("兩次輸入的密碼不一致", mismatch.Body);

        var done = await _web.PostAsync("/setup?lang=zh-TW", "/setup?lang=zh-TW", ("username", "admin"), ("password", "a-long-enough-password"), ("confirm", "a-long-enough-password"));
        Assert.Equal(HttpStatusCode.Redirect, done.Status);
        Assert.Equal("zh-TW", _web.Live.Current.Language);

        var home = await _web.GetAsync("/");
        Assert.Contains("儀表板", home.Body);
        Assert.Contains("目前還沒有監控任何 SQL Server", home.Body);
    }

    [Fact]
    public async Task Setup_without_a_choice_stays_english()
    {
        await _web.SignInAsAdminAsync();
        Assert.Equal("en", _web.Live.Current.Language);
        Assert.Contains("No SQL Server is being watched yet", (await _web.GetAsync("/")).Body);
    }

    [Fact]
    public async Task The_header_button_switches_language_for_every_page_and_returns_to_the_same_page()
    {
        await _web.SignInAsAdminAsync();
        var english = await _web.GetAsync("/instances");
        Assert.Contains("繁體中文", english.Body);                                   // the button shows the other language's own name

        var switched = await _web.PostAsync("/instances", "/language", ("language", "zh-TW"), ("returnUrl", "/instances"));
        Assert.Equal(HttpStatusCode.Redirect, switched.Status);
        Assert.Equal("/instances", WebFixture.Location(switched.Response));
        Assert.Equal("zh-TW", _web.Live.Current.Language);

        foreach (var (path, expected) in new[]
                 {
                     ("/", "儀表板"), ("/instances", "SQL Server 伺服器"), ("/instances/new", "新增伺服器"),
                     ("/settings", "Email 告警"), ("/account", "更改密碼"),
                 })
        {
            var page = await _web.GetAsync(path);
            Assert.Contains(expected, page.Body);
            Assert.Contains("<html lang=\"zh-Hant\">", page.Body);
        }
        Assert.Contains(">English<", (await _web.GetAsync("/")).Body);

        await _web.PostAsync("/", "/language", ("language", "en"), ("returnUrl", "/"));
        Assert.Equal("en", _web.Live.Current.Language);
        Assert.Contains("Dashboard", (await _web.GetAsync("/")).Body);
    }

    [Fact]
    public async Task The_language_survives_a_restart_because_it_is_stored()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/", "/language", ("language", "zh-TW"), ("returnUrl", "/"));

        var reloaded = await _web.Settings.BuildConfigAsync();
        Assert.Equal("zh-TW", reloaded.Language);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    [InlineData("zh-CN")]
    public async Task An_unsupported_language_is_ignored(string language)
    {
        await _web.SignInAsAdminAsync();
        var r = await _web.PostAsync("/", "/language", ("language", language), ("returnUrl", "/"));
        Assert.Equal(HttpStatusCode.Redirect, r.Status);
        Assert.Equal("en", _web.Live.Current.Language);
    }

    [Theory]
    [InlineData("//evil.example/", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("/settings", "/settings")]
    public async Task The_language_button_never_redirects_off_site(string returnUrl, string expected)
    {
        await _web.SignInAsAdminAsync();
        var r = await _web.PostAsync("/", "/language", ("language", "zh-TW"), ("returnUrl", returnUrl));
        Assert.Equal(expected, WebFixture.Location(r.Response));
    }

    [Fact]
    public async Task The_language_button_needs_a_signed_in_user_and_a_form_token()
    {
        await _web.SignInAsAdminAsync();
        var noToken = await _web.PostRawAsync("/language", ("language", "zh-TW"), ("returnUrl", "/"));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.Status);

        await _web.PostAsync("/", "/logout");
        _web.ForgetCookies();
        var anonymous = await _web.PostAsync("/login", "/language", ("language", "zh-TW"), ("returnUrl", "/"));
        Assert.Equal("en", _web.Live.Current.Language);
        Assert.NotEqual("zh-TW", (await _web.Settings.GetGlobalAsync()).Language);
        Assert.StartsWith("/login", WebFixture.Location(anonymous.Response));
    }

    [Fact]
    public async Task Validation_messages_and_hints_come_back_in_chinese()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/", "/language", ("language", "zh-TW"), ("returnUrl", "/"));

        var noServer = await _web.PostAsync("/instances/new", "/instances/new",
            ("name", "box"), ("server", ""), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));
        Assert.Equal(HttpStatusCode.BadRequest, noServer.Status);
        Assert.Contains("請輸入伺服器名稱", noServer.Body);

        var badLimit = await _web.PostAsync("/settings", "/settings",
            Enumerable.Concat(AllSettingsFields(), [("th_FullBackupWarnHours", "50")]).GroupBy(f => f.Item1).Select(g => g.Last()).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.Status);
        Assert.Contains("備份時間的警告門檻必須大於 0,且低於嚴重門檻", badLimit.Body);

        var down = await _web.PostAsync("/instances/new", "/instances/new",
            ("name", "down"), ("server", "localhost,1"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));
        Assert.Equal(HttpStatusCode.Redirect, down.Status);
        var test = await _web.PostAsync("/instances", "/instances/down/test");
        Assert.Contains("無法連線", test.Body);
    }

    private static (string, string)[] AllSettingsFields()
    {
        var fields = new Dictionary<string, string> { ["renotify"] = "4", ["retention"] = "90", ["language"] = "zh-TW" };
        foreach (var rule in new[] { "backup", "dbcc", "disk", "log", "database_state", "suspect_pages", "jobs" }) fields["rule_" + rule] = "1";
        foreach (var c in CollectorCatalog.All()) fields["col_" + c.Name] = "1";
        foreach (var p in typeof(AlertThresholds).GetProperties())
            fields["th_" + p.Name] = Convert.ToString(p.GetValue(new AlertThresholds()), System.Globalization.CultureInfo.InvariantCulture)!;
        return fields.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    [Fact]
    public async Task Saving_settings_can_change_the_language_and_the_page_afterwards_is_in_it()
    {
        await _web.SignInAsAdminAsync();
        var saved = await _web.PostAsync("/settings", "/settings", AllSettingsFields());      // the form says language = zh-TW

        Assert.Equal(HttpStatusCode.Redirect, saved.Status);
        Assert.Equal("zh-TW", _web.Live.Current.Language);
        var page = await _web.GetAsync("/settings?msg=saved");
        Assert.Contains("設定已儲存,立即生效", page.Body);
        Assert.Contains("<option value=\"zh-TW\" selected>繁體中文</option>", page.Body);
    }

    [Fact]
    public async Task Dashboard_problems_and_tables_are_translated_and_still_encoded()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/", "/language", ("language", "zh-TW"), ("returnUrl", "/"));
        await _web.PostAsync("/instances/new", "/instances/new",
            ("name", "prod"), ("server", "PROD01"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));
        var t = DateTime.UtcNow;
        using (Loc.Use(Loc.English))
        {
            await _web.History.AppendAsync("prod", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
            await _web.History.AppendAsync("prod", "database_state", t, TimeSpan.Zero, CollectionResult.Ok(
                [new Sample("db_state", "Sales", 0, "ONLINE"), new Sample("db_state", "<b>Bad</b>", 4, "SUSPECT")]));
            await _web.History.AppendAsync("prod", "backup_age", t, TimeSpan.Zero, CollectionResult.Ok(
                [new Sample("last_full_backup_age_min", "Sales", 60 * 60)]));
            await _web.History.AppendAsync("prod", "disk_space", t, TimeSpan.Zero, new CollectionResult(CollectionStatus.Unsupported, [], "sys.dm_os_volume_stats needs SQL Server 2008 R2 SP1 or later"));
        }

        var page = await _web.GetAsync("/");

        Assert.Contains("可連線", page.Body);
        Assert.Contains("Sales 的完整備份已經 60 小時", (await _web.GetAsync("/problems")).Body);                  // computed in Chinese for this request
        Assert.Contains("最舊完整備份", page.Body);                                   // overview header
        Assert.Contains("最低磁碟剩餘", page.Body);

        var problems = await _web.GetAsync("/server/prod");
        Assert.Contains("這台伺服器不支援(sys.dm_os_volume_stats 需要 SQL Server 2008 R2 SP1 或更新版本)", problems.Body);   // a stored English reason, translated when shown
        Assert.Contains("資料庫", problems.Body);                                    // tab names
        Assert.Contains("效能", problems.Body);

        var databases = await _web.GetAsync("/server/prod?tab=databases");
        Assert.Contains("最近完整備份", databases.Body);                               // table header
        Assert.DoesNotContain("<b>Bad</b>", databases.Body);
        Assert.Contains("&lt;b&gt;Bad&lt;/b&gt;", databases.Body);

        var performance = await _web.GetAsync("/server/prod?tab=performance");
        Assert.Contains("這項目前還沒有歷史資料。", performance.Body);
        Assert.Contains("時間範圍", performance.Body);
    }
}

public sealed class AlertLanguageTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-lang-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Recorder : IAlertSender
    {
        public List<(string Subject, string Body)> Mails { get; } = [];
        public Task SendAsync(string subject, string body, CancellationToken ct)
        {
            Mails.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        _store = new SqliteHistoryStore(_file);
        await _store.InitializeAsync();
        await _store.AppendAsync("prod", CollectorCatalog.Connectivity, Now, TimeSpan.Zero, CollectionResult.Ok([]));
        await _store.AppendAsync("prod", "database_state", Now, TimeSpan.Zero, CollectionResult.Ok([new Sample("db_state", "Sales", 4, "SUSPECT")]));
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private static SqlLookoutConfig Config(string language) => new()
    {
        Language = language,
        Instances = [new InstanceConfig { Name = "prod", ConnectionString = "Server=x;Integrated Security=true" }],
        Alerts = new AlertSettings { SummaryTime = null },
    };

    [Fact]
    public async Task Alert_mail_follows_the_configured_language()
    {
        var zh = new Recorder();
        await new MonitorLoop(Config(Loc.TraditionalChinese), _store, zh, _ => { }).RunAlertPassAsync(Now, CancellationToken.None);

        var mail = Assert.Single(zh.Mails);
        Assert.Equal("[SqlLookout] 嚴重:1 則告警", mail.Subject);
        Assert.Contains("[嚴重] (新發生) prod: 資料庫 Sales 狀態為 SUSPECT", mail.Body);
        Assert.Contains("prod 上的 Sales 目前狀態為 SUSPECT", mail.Body);
        Assert.Equal("en", Loc.Language);                           // the pass did not change the caller's language
    }

    [Fact]
    public async Task Alert_mail_is_english_by_default_and_the_resolved_mail_follows_the_language_too()
    {
        var en = new Recorder();
        var english = new MonitorLoop(Config(Loc.English), _store, en, _ => { });
        await english.RunAlertPassAsync(Now, CancellationToken.None);
        Assert.Equal("[SqlLookout] Critical: 1 alert(s)", en.Mails[0].Subject);

        // The problem clears; the resolved mail is written in the language now configured.
        await _store.AppendAsync("prod", "database_state", Now.AddMinutes(1), TimeSpan.Zero, CollectionResult.Ok([new Sample("db_state", "Sales", 0, "ONLINE")]));
        var zh = new Recorder();
        var chinese = new MonitorLoop(Config(Loc.TraditionalChinese), _store, zh, _ => { });
        await chinese.RunAlertPassAsync(Now.AddMinutes(2), CancellationToken.None);
        Assert.Empty(zh.Mails);                                      // not resolved yet: it must stay away for the grace time
        await chinese.RunAlertPassAsync(Now.AddMinutes(40), CancellationToken.None);

        Assert.Equal("[SqlLookout] 已解除:1 則告警", zh.Mails.Single().Subject);
    }

    [Fact]
    public async Task Daily_summary_is_translated()
    {
        using var _ = Loc.Use(Loc.TraditionalChinese);
        var text = await DailySummary.BuildAsync(_store, ["prod"], Now);

        Assert.Contains("SqlLookout 每日摘要", text);
        Assert.Contains("目前沒有未解決的告警", text);
        Assert.Contains("prod:可連線,1 個資料庫", text);
    }
}
