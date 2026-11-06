using System.Net;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Tests;

public class WebTests : IAsyncLifetime
{
    private WebFixture _web = null!;

    public async Task InitializeAsync() => _web = await WebFixture.StartAsync();
    public async Task DisposeAsync() => await _web.DisposeAsync();

    private static string LocalSqlServer =>
        Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_SERVER") ?? "localhost";

    // ---------------------------------------------------------------- setup and sign-in

    [Fact]
    public async Task Before_setup_every_page_leads_to_the_setup_page()
    {
        var home = await _web.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.Status);
        Assert.StartsWith("/login", WebFixture.Location(home.Response));

        var login = await _web.GetAsync("/login");
        Assert.Equal("/setup", WebFixture.Location(login.Response));
    }

    [Fact]
    public async Task Setup_creates_the_admin_signs_in_and_then_closes()
    {
        var setup = await _web.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, setup.Status);
        Assert.Contains("Create the administrator account", setup.Body);

        var created = await _web.PostAsync("/setup", "/setup", ("username", "admin"), ("password", "a-long-enough-password"), ("confirm", "a-long-enough-password"));
        Assert.Equal(HttpStatusCode.Redirect, created.Status);
        Assert.Equal("/", WebFixture.Location(created.Response));

        var home = await _web.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.Status);
        Assert.Contains("No SQL Server is being watched yet", home.Body);

        // The setup page is closed for good once an account exists.
        _web.ForgetCookies();
        var again = await _web.GetAsync("/setup");
        Assert.Equal("/login", WebFixture.Location(again.Response));
        var post = await _web.PostAsync("/login", "/setup", ("username", "intruder"), ("password", "another-long-password"), ("confirm", "another-long-password"));
        Assert.Equal("/login", WebFixture.Location(post.Response));
        Assert.Equal(LoginResult.Invalid, await _web.Settings.VerifyLoginAsync("intruder", "another-long-password", DateTime.UtcNow));
    }

    [Fact]
    public async Task Setup_is_refused_from_another_machine()
    {
        _web.RemoteAddress = IPAddress.Parse("10.1.2.3");

        var page = await _web.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Forbidden, page.Status);
        Assert.Contains("only be created from the server itself", page.Body);

        _web.RemoteAddress = IPAddress.Loopback;
        var form = await _web.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, form.Status);
        _web.RemoteAddress = IPAddress.Parse("10.1.2.3");
        var post = await _web.PostRawAsync("/setup", ("username", "admin"), ("password", "a-long-enough-password"), ("confirm", "a-long-enough-password"));
        Assert.NotEqual(HttpStatusCode.Redirect, post.Status);          // no token / not local: nothing is created
        Assert.False(await _web.Settings.HasAdminAsync());
    }

    [Fact]
    public async Task Setup_rejects_mismatched_and_short_passwords_with_a_message()
    {
        var mismatch = await _web.PostAsync("/setup", "/setup", ("username", "admin"), ("password", "a-long-enough-password"), ("confirm", "different-long-password"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);
        Assert.Contains("not the same", mismatch.Body);

        var shortPw = await _web.PostAsync("/setup", "/setup", ("username", "admin"), ("password", "short"), ("confirm", "short"));
        Assert.Equal(HttpStatusCode.BadRequest, shortPw.Status);
        Assert.Contains("at least 10 characters", shortPw.Body);
        Assert.False(await _web.Settings.HasAdminAsync());
    }

    [Fact]
    public async Task Login_works_and_wrong_credentials_get_one_generic_message()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/", "/logout");
        _web.ForgetCookies();

        var wrongPw = await _web.PostAsync("/login", "/login", ("username", "admin"), ("password", "not-the-password"));
        var wrongUser = await _web.PostAsync("/login", "/login", ("username", "nobody"), ("password", "not-the-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPw.Status);
        Assert.Contains("Wrong user name or password", wrongPw.Body);
        Assert.Contains("Wrong user name or password", wrongUser.Body);    // same text for an unknown user: no account probing

        var ok = await _web.PostAsync("/login", "/login", ("username", "admin"), ("password", "a-long-enough-password"));
        Assert.Equal(HttpStatusCode.Redirect, ok.Status);
        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync("/")).Status);
    }

    [Theory]
    [InlineData("/instances", "/instances")]
    [InlineData("//evil.example/steal", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("", "/")]
    public async Task Return_url_only_follows_local_paths(string returnUrl, string expected)
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/", "/logout");
        _web.ForgetCookies();

        var ok = await _web.PostAsync("/login", "/login", ("username", "admin"), ("password", "a-long-enough-password"), ("returnUrl", returnUrl));
        Assert.Equal(expected, WebFixture.Location(ok.Response));
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/", "/logout");
        _web.ForgetCookies();

        for (var i = 0; i < 5; i++)
            await _web.PostAsync("/login", "/login", ("username", "admin"), ("password", "wrong-password-" + i));

        var locked = await _web.PostAsync("/login", "/login", ("username", "admin"), ("password", "a-long-enough-password"));
        var unknown = await _web.PostAsync("/login", "/login", ("username", "nobody"), ("password", "a-long-enough-password"));

        // A locked account answers exactly like an unknown user, so the page cannot be used to find which user names exist.
        Assert.Equal(HttpStatusCode.Unauthorized, locked.Status);
        Assert.Equal(unknown.Status, locked.Status);
        Assert.Equal(unknown.Body.Length, locked.Body.Length);
        Assert.Contains("locked", locked.Body);
    }

    [Fact]
    public async Task Logout_makes_a_copied_cookie_useless()
    {
        await _web.SignInAsAdminAsync();
        var copy = _web.Cookie("sqllookout.auth")!;
        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync("/instances")).Status);

        await _web.PostAsync("/", "/logout");

        _web.ForgetCookies();
        _web.SetCookie("sqllookout.auth", copy);      // an attacker who saved the cookie before the user signed out
        var replay = await _web.GetAsync("/instances");
        Assert.Equal(HttpStatusCode.Redirect, replay.Status);
        Assert.StartsWith("/login", WebFixture.Location(replay.Response));
    }

    [Fact]
    public async Task Password_change_ends_other_sessions_and_keeps_the_current_one()
    {
        await _web.SignInAsAdminAsync();
        var otherBrowser = _web.Cookie("sqllookout.auth")!;

        _web.ForgetCookies();
        await _web.PostAsync("/login", "/login", ("username", "admin"), ("password", "a-long-enough-password"));
        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync("/instances")).Status);

        var changed = await _web.PostAsync("/account", "/account", ("current", "a-long-enough-password"), ("new", "brand-new-password"), ("confirm", "brand-new-password"));
        Assert.Equal(HttpStatusCode.Redirect, changed.Status);

        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync("/instances")).Status);          // this browser stays signed in

        _web.ForgetCookies();
        _web.SetCookie("sqllookout.auth", otherBrowser);
        var stale = await _web.GetAsync("/instances");
        Assert.Equal(HttpStatusCode.Redirect, stale.Status);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await _web.SignInAsAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await _web.GetAsync("/instances")).Status);

        await _web.PostAsync("/", "/logout");

        var after = await _web.GetAsync("/instances");
        Assert.Equal(HttpStatusCode.Redirect, after.Status);
        Assert.StartsWith("/login", WebFixture.Location(after.Response));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/instances")]
    [InlineData("/instances/new")]
    [InlineData("/settings")]
    [InlineData("/account")]
    public async Task Pages_need_a_signed_in_user(string path)
    {
        await _web.Settings.CreateAdminAsync("admin", "a-long-enough-password");
        var r = await _web.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, r.Status);
        Assert.StartsWith("/login", WebFixture.Location(r.Response));
    }

    // ---------------------------------------------------------------- request forgery and headers

    [Fact]
    public async Task Posting_without_a_form_token_is_refused_even_when_signed_in()
    {
        await _web.SignInAsAdminAsync();

        var r = await _web.PostRawAsync("/instances/new", ("name", "x"), ("server", "y"), ("auth", "windows"), ("encrypt", "trust"), ("enabled", "1"));

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Empty(await _web.Settings.GetInstancesAsync());
    }

    [Fact]
    public async Task A_token_from_another_session_does_not_work()
    {
        await _web.SignInAsAdminAsync();
        var page = await _web.GetAsync("/instances/new");
        var token = System.Text.RegularExpressions.Regex.Match(page.Body, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

        _web.ForgetCookies();         // an attacker's browser has the token text but not the matching cookie
        var r = await _web.PostRawAsync("/login", ("__RequestVerificationToken", token), ("username", "admin"), ("password", "a-long-enough-password"));

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
    }

    [Fact]
    public async Task Pages_carry_a_strict_content_security_policy_and_are_not_cached()
    {
        var r = await _web.GetAsync("/login");
        var csp = string.Join(" ", r.Response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe", csp);
        Assert.DoesNotContain("script-src", csp);       // no scripts are allowed at all, and none are used
        Assert.Contains("no-store", string.Join(",", r.Response.Headers.GetValues("Cache-Control")));
        Assert.Equal("nosniff", r.Response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.DoesNotContain("<script", r.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stylesheet_is_served_without_signing_in()
    {
        var r = await _web.GetAsync("/app.css");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains("text/css", r.Response.Content.Headers.ContentType!.ToString());
    }

    // ---------------------------------------------------------------- servers

    private Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> AddServer(
        string name, string server = "PROD01", string auth = "windows", string user = "", string password = "", string encrypt = "trust") =>
        _web.PostAsync("/instances/new", "/instances/new",
            ("name", name), ("server", server), ("auth", auth), ("user", user), ("password", password), ("encrypt", encrypt), ("enabled", "1"));

    [Fact]
    public async Task Adding_a_server_stores_it_lists_it_and_starts_monitoring_it()
    {
        await _web.SignInAsAdminAsync();

        var added = await AddServer("PROD-01", "PROD01\\SQL2008", encrypt: "off");
        Assert.Equal(HttpStatusCode.Redirect, added.Status);
        Assert.Equal("/instances?msg=added", WebFixture.Location(added.Response));

        var list = await _web.GetAsync("/instances?msg=added");
        Assert.Contains("PROD-01", list.Body);
        Assert.Contains("PROD01\\SQL2008", list.Body);
        Assert.Contains("Server added", list.Body);

        var running = _web.Live.Current.Instances.Single();
        Assert.Equal("PROD-01", running.Name);
        Assert.True(running.Enabled);
        Assert.Contains("PROD01\\SQL2008", running.ResolveConnectionString());
    }

    [Fact]
    public async Task A_sql_password_never_appears_in_any_page_or_in_the_database_file()
    {
        await _web.SignInAsAdminAsync();
        const string secret = "Zx9!secretPW-never-show";
        await AddServer("sqlbox", "SQL01", "sql", "monitor", secret);

        foreach (var path in new[] { "/instances", "/instances/sqlbox/edit", "/", "/settings" })
        {
            var page = await _web.GetAsync(path);
            Assert.DoesNotContain(secret, page.Body);
        }

        // The edit form says a password exists without showing it.
        var edit = await _web.GetAsync("/instances/sqlbox/edit");
        Assert.Contains("Leave blank to keep the saved password", edit.Body);

        Assert.DoesNotContain(secret, await _web.StoredTextAsync());
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Editing_with_a_blank_password_keeps_the_saved_one()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("sqlbox", "SQL01", "sql", "monitor", "first-secret");

        var edit = await _web.PostAsync("/instances/sqlbox/edit", "/instances/sqlbox/edit",
            ("server", "SQL02"), ("auth", "sql"), ("user", "monitor"), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));

        Assert.Equal(HttpStatusCode.Redirect, edit.Status);
        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder((await _web.Settings.GetConnectionStringAsync("sqlbox"))!);
        Assert.Equal("SQL02", parsed.DataSource);
        Assert.Equal("first-secret", parsed.Password);
        Assert.Equal("SQL02", _web.Live.Current.Instances.Single().ResolveConnectionString().Split(';').First(p => p.StartsWith("Data Source", StringComparison.OrdinalIgnoreCase)).Split('=')[1]);
    }

    [Fact]
    public async Task Bad_input_shows_a_readable_message_and_keeps_what_was_typed()
    {
        await _web.SignInAsAdminAsync();

        var noServer = await AddServer("box", server: "");
        Assert.Equal(HttpStatusCode.BadRequest, noServer.Status);
        Assert.Contains("Enter the server name", noServer.Body);

        var noPassword = await AddServer("box", "SQL01", "sql", "monitor", "");
        Assert.Equal(HttpStatusCode.BadRequest, noPassword.Status);
        Assert.Contains("Enter the SQL login password", noPassword.Body);
        Assert.Contains("value=\"SQL01\"", noPassword.Body);          // the form is shown again with the server filled in

        var badName = await AddServer("../../etc", "SQL01");
        Assert.Equal(HttpStatusCode.BadRequest, badName.Status);

        Assert.Empty(await _web.Settings.GetInstancesAsync());
    }

    [Fact]
    public async Task Duplicate_names_are_refused()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("box", "SQL01");
        var again = await AddServer("BOX", "SQL02");
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);
        Assert.Contains("already exists", again.Body);
    }

    [Fact]
    public async Task Removing_a_server_asks_first_and_then_stops_watching_it()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("box", "SQL01");

        var confirm = await _web.GetAsync("/instances/box/delete");
        Assert.Contains("Remove box?", confirm.Body);
        Assert.Single(await _web.Settings.GetInstancesAsync());          // asking changed nothing

        var done = await _web.PostAsync("/instances/box/delete", "/instances/box/delete");
        Assert.Equal(HttpStatusCode.Redirect, done.Status);
        Assert.Empty(await _web.Settings.GetInstancesAsync());
        Assert.Empty(_web.Live.Current.Instances);
    }

    [Fact]
    public async Task Unknown_server_gives_not_found()
    {
        await _web.SignInAsAdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await _web.GetAsync("/instances/ghost/edit")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _web.GetAsync("/instances/ghost/delete")).Status);
    }

    [Fact]
    public async Task Test_button_connects_to_a_real_server_and_reports_each_check()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("local", LocalSqlServer);

        var r = await _web.PostAsync("/instances", "/instances/local/test");

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains("Connected. SQL Server", r.Body);
        Assert.Contains("backup_age", r.Body);
        Assert.Contains("works", r.Body);
    }

    [Fact]
    public async Task Test_button_explains_a_connection_failure_without_echoing_the_connection_string()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("down", "localhost,1", "sql", "monitor", "hidden-pw-123");

        var r = await _web.PostAsync("/instances", "/instances/down/test");

        Assert.Contains("Could not connect (SQL error", r.Body);
        Assert.DoesNotContain("hidden-pw-123", r.Body);
        Assert.DoesNotContain("localhost,1", r.Body);
    }

    // ---------------------------------------------------------------- script injection

    [Fact]
    public async Task User_supplied_text_is_encoded_everywhere_it_is_shown()
    {
        await _web.SignInAsAdminAsync();
        const string evil = "\"><script>alert(1)</script>";
        await AddServer("box", evil);
        var t = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        await _web.History.AppendAsync("box", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("box", "database_state", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("db_state", "<img src=x onerror=alert(1)>", 0, "<b>ONLINE</b>")]));
        await _web.History.AppendAsync("box", "agent_jobs", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("job_last_outcome", "<script>job()</script>", 0)]));

        foreach (var path in new[] { "/", "/instances", "/instances/box/edit", "/instances/box/delete", "/server/box?tab=databases", "/server/box?tab=jobs&all=1" })
        {
            var page = await _web.GetAsync(path);
            Assert.DoesNotContain("<script>alert", page.Body);
            Assert.DoesNotContain("<script>job", page.Body);
            Assert.DoesNotContain("<img src=x", page.Body);
            Assert.DoesNotContain("<b>ONLINE</b>", page.Body);
        }
        var databases = await _web.GetAsync("/server/box?tab=databases");
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", databases.Body);
        var jobs = await _web.GetAsync("/server/box?tab=jobs&all=1");
        Assert.Contains("&lt;script&gt;job()&lt;/script&gt;", jobs.Body);
    }

    // ---------------------------------------------------------------- dashboard

    [Fact]
    public async Task Dashboard_is_an_overview_and_the_detail_lives_on_the_server_page()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("prod", "PROD01");
        var t = DateTime.UtcNow;
        await _web.History.AppendAsync("prod", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("prod", "database_state", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("db_state", "Sales", 0, "ONLINE"), new Sample("db_state", "Broken", 4, "SUSPECT")]));
        await _web.History.AppendAsync("prod", "backup_age", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("last_full_backup_age_min", "Sales", 60 * 60), new Sample("last_full_backup_age_min", "Broken", null)]));
        await _web.History.AppendAsync("prod", "disk_space", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("disk_total_bytes", "D:\\", 1000L * 1024 * 1024 * 1024), new Sample("disk_free_bytes", "D:\\", 80L * 1024 * 1024 * 1024)]));

        var page = await _web.GetAsync("/");

        Assert.Contains("href=\"/server/prod?tab=problems\"", page.Body);   // the server name leads to its own page
        Assert.Contains("reachable", page.Body);
        var list = (await _web.GetAsync("/problems")).Body;   // the problem list has its own page
        Assert.DoesNotContain("Database Broken is SUSPECT", page.Body);
        Assert.Contains("Database Broken is SUSPECT", list);          // a critical finding
        Assert.Contains("Full backup of Sales is 60 h old", list);     // a warning finding
        Assert.Contains("8%", page.Body);                                   // lowest disk: 80 of 1000 GB free
        Assert.Contains("http-equiv=\"refresh\"", page.Body);
        // The long per-server tables are not on the home page any more.
        Assert.DoesNotContain("Data size", page.Body);
        Assert.DoesNotContain("Volume", page.Body);

        var problems = await _web.GetAsync("/server/prod");
        Assert.Equal(HttpStatusCode.OK, problems.Status);
        Assert.Contains("Database Broken is SUSPECT", problems.Body);
        Assert.DoesNotContain("Data size", problems.Body);

        var databases = await _web.GetAsync("/server/prod?tab=databases");
        Assert.Contains("Data size", databases.Body);
        Assert.True(databases.Body.IndexOf("Broken", StringComparison.Ordinal) < databases.Body.IndexOf("Sales", StringComparison.Ordinal),
            "the database that needs a look comes first");

        var disks = await _web.GetAsync("/server/prod?tab=disks");
        Assert.Contains("D:\\", disks.Body);
        Assert.Contains("80 GB", disks.Body);
        Assert.Contains("8%", disks.Body);

        // An unknown tab or range falls back to the defaults instead of failing.
        var odd = await _web.GetAsync("/server/prod?tab=nonsense&range=forever");
        Assert.Equal(HttpStatusCode.OK, odd.Status);
        Assert.Contains("Database Broken is SUSPECT", odd.Body);
    }

    [Fact]
    public async Task Dashboard_lists_the_worst_servers_first()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("aaa-healthy", "A01");
        await AddServer("bbb-down", "B01");
        await AddServer("ccc-warn", "C01");
        var t = DateTime.UtcNow;
        await _web.History.AppendAsync("aaa-healthy", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        for (var i = 0; i < 3; i++)
            await _web.History.AppendAsync("bbb-down", CollectorCatalog.Connectivity, t.AddSeconds(i), TimeSpan.Zero, new CollectionResult(CollectionStatus.Failed, [], "SQL error 53"));
        await _web.History.AppendAsync("ccc-warn", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("ccc-warn", "backup_age", t, TimeSpan.Zero, CollectionResult.Ok([new Sample("last_full_backup_age_min", "Sales", 60 * 60)]));

        var body = (await _web.GetAsync("/")).Body;

        var down = body.IndexOf("<strong>bbb-down", StringComparison.Ordinal);
        var warn = body.IndexOf("<strong>ccc-warn", StringComparison.Ordinal);
        var ok = body.IndexOf("<strong>aaa-healthy", StringComparison.Ordinal);
        Assert.True(down >= 0 && warn >= 0 && ok >= 0);
        Assert.True(down < warn && warn < ok, "unreachable first, then warning, then healthy (although healthy sorts first by name)");
    }

    [Fact]
    public async Task Status_is_never_shown_by_colour_alone()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("aaa-ok", "A01");
        await AddServer("bbb-crit", "B01");
        await AddServer("ccc-warn", "C01");
        await AddServer("ddd-new", "D01");                                  // nothing collected yet
        var t = DateTime.UtcNow;
        await _web.History.AppendAsync("aaa-ok", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("bbb-crit", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("bbb-crit", "database_state", t, TimeSpan.Zero, CollectionResult.Ok([new Sample("db_state", "Broken", 4, "SUSPECT")]));
        await _web.History.AppendAsync("ccc-warn", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("ccc-warn", "backup_age", t, TimeSpan.Zero, CollectionResult.Ok([new Sample("last_full_backup_age_min", "Sales", 30 * 60)]));   // 30 h: above the 26 h warning, below the 48 h critical

        var body = (await _web.GetAsync("/")).Body;

        // Each overview row says what it is (the marker colour comes from the class), a waiting server is not drawn as healthy.
        Assert.Matches(@"<tr class=""rowok"">\s*<td><a[^>]*><strong>aaa-ok", body);
        Assert.Matches(@"<tr class=""rowbad"">\s*<td><a[^>]*><strong>bbb-crit", body);
        Assert.Matches(@"<tr class=""rowwarn"">\s*<td><a[^>]*><strong>ccc-warn", body);
        Assert.Matches(@"<tr class=""rowoff"">\s*<td><a[^>]*><strong>ddd-new", body);
        // Every problem starts with its severity as a word.
        Assert.Contains("<div class=\"problem bad\"><span class=\"sev\">Critical</span>", (await _web.GetAsync("/problems")).Body);
        Assert.Contains("<div class=\"problem warn\"><span class=\"sev\">Warning</span>", (await _web.GetAsync("/problems")).Body);
        Assert.Contains("<div class=\"problem bad\"><span class=\"sev\">Critical</span>", (await _web.GetAsync("/server/bbb-crit")).Body);
    }

    [Fact]
    public async Task Unknown_server_gives_404()
    {
        await _web.SignInAsAdminAsync();
        var r = await _web.GetAsync("/server/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.Status);
        Assert.Contains("Server not found", r.Body);
    }

    [Fact]
    public async Task Jobs_tab_shows_only_jobs_that_need_attention_until_asked_for_all()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("jobs", "J01");
        var t = DateTime.UtcNow;
        var samples = new List<Sample>();
        for (var i = 0; i < 50; i++) samples.Add(new Sample("job_last_outcome", $"nightly-{i:00}", 1));
        samples.Add(new Sample("job_last_outcome", "broken-etl", 0));
        samples.Add(new Sample("job_last_outcome", "running-now", 1));
        samples.Add(new Sample("job_running_min", "running-now", 5));
        await _web.History.AppendAsync("jobs", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("jobs", "agent_jobs", t, TimeSpan.Zero, CollectionResult.Ok(samples));

        var brief = (await _web.GetAsync("/server/jobs?tab=jobs")).Body;
        Assert.Contains("broken-etl", brief);
        Assert.Contains("running-now", brief);
        Assert.DoesNotContain("nightly-07", brief);
        Assert.Contains("50 more jobs succeeded on their last run.", brief);
        Assert.Contains("Show all 52 jobs", brief);

        var all = (await _web.GetAsync("/server/jobs?tab=jobs&all=1")).Body;
        Assert.Contains("nightly-07", all);
        Assert.True(all.IndexOf("broken-etl", StringComparison.Ordinal) < all.IndexOf("nightly-00", StringComparison.Ordinal), "problem jobs stay on top");

        // The overview never lists the healthy jobs; the failure shows up there only as a problem and a count.
        var home = (await _web.GetAsync("/")).Body;
        Assert.DoesNotContain("nightly-07", home);
        Assert.DoesNotContain("running-now", home);
    }

    [Fact]
    public async Task A_finished_job_is_no_longer_shown_as_running_and_a_running_job_is_not_a_red_badge()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("agent", "AG01");
        var t = DateTime.UtcNow;
        await _web.History.AppendAsync("agent", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        // Earlier reading: the job was running. Newer reading: it succeeded and is not running (empty reading).
        await _web.History.AppendAsync("agent", "agent_jobs", t.AddMinutes(-5), TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("job_last_outcome", "etl-done", 1), new Sample("job_running_min", "etl-done", 3),
             new Sample("job_last_outcome", "etl-busy", 1), new Sample("job_running_min", "etl-busy", 3)]));
        await _web.History.AppendAsync("agent", "agent_jobs", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("job_last_outcome", "etl-done", 1), new Sample("job_running_min", "etl-done", null),
             new Sample("job_last_outcome", "etl-busy", 1), new Sample("job_running_min", "etl-busy", 8)]));

        var jobs = (await _web.GetAsync("/server/agent?tab=jobs")).Body;

        Assert.Contains("etl-busy", jobs);
        Assert.DoesNotContain("etl-done", jobs);                           // finished: back among the quiet jobs
        Assert.Contains("1 more jobs succeeded on their last run.", jobs);
        Assert.DoesNotContain("badge bad\">1<", jobs);                     // a merely running job is not counted as a problem
    }

    [Fact]
    public async Task A_paused_server_shows_no_problem_counts_or_badge()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("off", "OFF01");
        await _web.PostAsync("/instances/off/edit", "/instances/off/edit", ("server", "OFF01"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"));   // unticked = paused
        var t = DateTime.UtcNow;
        await _web.History.AppendAsync("off", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("off", "database_state", t, TimeSpan.Zero, CollectionResult.Ok([new Sample("db_state", "Broken", 4, "SUSPECT")]));

        var page = (await _web.GetAsync("/server/off")).Body;
        var home = (await _web.GetAsync("/")).Body;

        Assert.DoesNotContain("badge bad\">1<", page);                     // no red badge on the Problems tab
        Assert.DoesNotContain("badge bad\">1<", home);                     // and no critical count in the overview row
        Assert.DoesNotContain("Database Broken is SUSPECT", home);
    }

    [Fact]
    public async Task Performance_tab_draws_charts_and_no_page_has_scripts_or_inline_styles()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("perf", "P01");
        var now = DateTime.UtcNow;
        await _web.History.AppendAsync("perf", CollectorCatalog.Connectivity, now, TimeSpan.Zero, CollectionResult.Ok([]));
        for (var i = 0; i < 30; i++)
            await _web.History.AppendAsync("perf", "cpu", now.AddMinutes(-(30 - i)), TimeSpan.Zero, CollectionResult.Ok(
                [new Sample("cpu_total_percent", null, 10 + i), new Sample("cpu_sql_percent", null, 5 + i)]));

        var body = (await _web.GetAsync("/server/perf?tab=performance")).Body;
        Assert.Contains("<svg class=\"chartsvg\"", body);
        Assert.Contains("class=\"threshold\"", body);                       // the 90% CPU warning line
        Assert.Contains("No history for this yet.", body);                  // memory and the rest have no data in this test
        Assert.Contains("range=7d", body);

        Assert.Contains("class=\"spark\"", (await _web.GetAsync("/")).Body);

        foreach (var path in new[] { "/", "/server/perf?tab=performance", "/server/perf?tab=disks", "/server/perf?tab=jobs", "/server/perf?tab=databases" })
        {
            var page = (await _web.GetAsync(path)).Body;
            Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" style=", page, StringComparison.OrdinalIgnoreCase);   // the CSP allows no inline styles
        }
    }

    [Fact]
    public async Task Dashboard_flags_an_unreachable_server_and_a_paused_one()
    {
        await _web.SignInAsAdminAsync();
        await AddServer("down", "DOWN01");
        await _web.PostAsync("/instances/down/edit", "/instances/down/edit", ("server", "DOWN01"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));
        await AddServer("paused", "OFF01");
        await _web.PostAsync("/instances/paused/edit", "/instances/paused/edit", ("server", "OFF01"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"));    // no "enabled" field = unticked
        var t = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
            await _web.History.AppendAsync("down", CollectorCatalog.Connectivity, t.AddSeconds(i), TimeSpan.Zero,
                new CollectionResult(CollectionStatus.Failed, [], "SQL error 53"));

        var page = await _web.GetAsync("/");

        Assert.Contains("UNREACHABLE", page.Body);
        Assert.Contains("<strong>Cannot connect</strong>", (await _web.GetAsync("/problems")).Body);          // the problem row shows the server name once, in front of the title
        Assert.Contains("paused", page.Body);
        Assert.False(_web.Live.Current.Instances.Single(i => i.Name == "paused").Enabled);
    }

    // ---------------------------------------------------------------- settings

    private static (string, string)[] SettingsForm(params (string, string)[] overrides)
    {
        var fields = new Dictionary<string, string>
        {
            ["email_on"] = "1", ["email_host"] = "smtp.local", ["email_port"] = "587", ["email_ssl"] = "1",
            ["email_from"] = "lookout@corp.example", ["email_to"] = "dba@corp.example, ops@corp.example",
            ["email_user"] = "lookout", ["email_password"] = "smtp-secret-pw",
            ["renotify"] = "6", ["summary"] = "07:30", ["quiet_start"] = "22:00", ["quiet_end"] = "06:00",
            ["retention"] = "45",
        };
        foreach (var rule in new[] { "backup", "dbcc", "disk", "log", "database_state", "suspect_pages", "jobs" }) fields["rule_" + rule] = "1";
        foreach (var c in CollectorCatalog.All()) fields["col_" + c.Name] = "1";
        foreach (var p in typeof(SqlLookout.Alerts.AlertThresholds).GetProperties())
            fields["th_" + p.Name] = Convert.ToString(p.GetValue(new SqlLookout.Alerts.AlertThresholds()), System.Globalization.CultureInfo.InvariantCulture)!;
        foreach (var (k, v) in overrides) fields[k] = v;
        return fields.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    [Fact]
    public async Task Settings_page_shows_defaults_before_anything_is_saved()
    {
        await _web.SignInAsAdminAsync();
        var page = await _web.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("Email alerts", page.Body);
        Assert.Contains("Backup age: warning after (hours)", page.Body);
        Assert.Contains("value=\"26\"", page.Body);
        Assert.Contains("value=\"90\"", page.Body);      // retention
    }

    [Fact]
    public async Task Diagnostic_log_is_off_until_switched_on_in_settings_and_then_records_problems_page_views()
    {
        await _web.SignInAsAdminAsync();
        Assert.False(_web.Diagnostic.Enabled);
        Assert.Contains("The diagnostic log is off", (await _web.GetAsync("/settings/diagnostics")).Body);

        var saved = await _web.PostAsync("/settings", "/settings", SettingsForm(("diag_log", "1"), ("claude_links", "1")));
        Assert.Equal(HttpStatusCode.Redirect, saved.Status);
        Assert.True(_web.Diagnostic.Enabled);

        await _web.GetAsync("/problems");
        var page = await _web.GetAsync("/settings/diagnostics");
        Assert.Contains("The diagnostic log is on", page.Body);
        Assert.Contains("[problems] Page served. Claude Code links on", page.Body);

        var download = await _web.GetAsync("/settings/diagnostics/download");
        Assert.Contains("[settings]", download.Body);

        await _web.PostAsync("/settings/diagnostics", "/settings/diagnostics/clear");
        Assert.Contains("Nothing logged yet", (await _web.GetAsync("/settings/diagnostics")).Body);

        await _web.PostAsync("/settings", "/settings", SettingsForm(("claude_links", "1")));
        Assert.False(_web.Diagnostic.Enabled);
    }

    [Fact]
    public async Task Saving_settings_applies_them_and_encrypts_the_smtp_password()
    {
        await _web.SignInAsAdminAsync();

        var saved = await _web.PostAsync("/settings", "/settings", SettingsForm(
            ("th_DiskWarnFreeGb", "42.5"), ("rule_dbcc", ""), ("col_agent_jobs", "")));

        Assert.Equal(HttpStatusCode.Redirect, saved.Status);
        var live = _web.Live.Current;
        Assert.Equal("smtp.local", live.Alerts.Email!.Host);
        Assert.Equal(587, live.Alerts.Email.Port);
        Assert.Equal(new[] { "dba@corp.example", "ops@corp.example" }, live.Alerts.Email.To);
        Assert.Equal("smtp-secret-pw", live.Alerts.Email.ResolvePassword());
        Assert.Equal(42.5, live.Alerts.Thresholds.DiskWarnFreeGb);
        Assert.Equal(45, live.RetentionDays);
        Assert.Equal(6, live.Alerts.RenotifyHours);
        Assert.Contains("dbcc", live.Alerts.DisabledRules);
        Assert.DoesNotContain("backup", live.Alerts.DisabledRules);
        Assert.Contains("agent_jobs", live.DisabledCollectors);

        Assert.DoesNotContain("smtp-secret-pw", await _web.StoredTextAsync());
        var page = await _web.GetAsync("/settings");
        Assert.DoesNotContain("smtp-secret-pw", page.Body);
        Assert.Contains("A password is saved", page.Body);
        Assert.Contains("value=\"42.5\"", page.Body);
    }

    [Fact]
    public async Task Saving_again_with_a_blank_password_keeps_it_and_the_checkbox_removes_it()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/settings", "/settings", SettingsForm());

        await _web.PostAsync("/settings", "/settings", SettingsForm(("email_password", "")));
        Assert.Equal("smtp-secret-pw", _web.Live.Current.Alerts.Email!.ResolvePassword());

        await _web.PostAsync("/settings", "/settings", SettingsForm(("email_password", ""), ("email_clear_password", "1")));
        Assert.Null(_web.Live.Current.Alerts.Email!.ResolvePassword());
    }

    [Fact]
    public async Task Unticking_email_turns_it_off()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/settings", "/settings", SettingsForm());
        Assert.NotNull(_web.Live.Current.Alerts.Email);

        await _web.PostAsync("/settings", "/settings", SettingsForm(("email_on", "")));
        Assert.Null(_web.Live.Current.Alerts.Email);
    }

    [Theory]
    [InlineData("th_DiskWarnFreeGb", "ten", "must be a number")]
    [InlineData("th_DiskWarnFreeGb", "-5", "must be a number")]
    [InlineData("renotify", "0", "more than zero")]
    [InlineData("email_port", "99999", "SMTP port")]
    [InlineData("email_to", "not-an-address", "not a valid email address")]
    [InlineData("quiet_end", "", "Quiet hours need both a start and an end")]
    [InlineData("summary", "25:99", "is not a time")]
    [InlineData("retention", "0", "1 to 3650")]
    [InlineData("th_FullBackupWarnHours", "50", "backup age")]            // warning above the critical 48
    [InlineData("th_LogWarnPercent", "95", "log usage")]                  // warning above the critical 90
    [InlineData("th_LogCriticalPercent", "150", "log usage")]
    [InlineData("th_DiskWarnFreePercent", "5", "disk free space")]        // warning below the critical 10
    [InlineData("th_UnreachableCriticalAfter", "1", "unreachable servers")]
    [InlineData("renotify", "1e99", "0.1 to 720")]
    public async Task Invalid_settings_are_refused_with_a_message_and_nothing_is_saved(string field, string value, string expectedMessage)
    {
        await _web.SignInAsAdminAsync();

        var r = await _web.PostAsync("/settings", "/settings", SettingsForm((field, value)));

        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Contains(expectedMessage, r.Body);
        Assert.Null(_web.Live.Current.Alerts.Email);
        Assert.False(await _web.Settings.HasSmtpPasswordAsync());
        // Nothing is half-saved: the other fields on the page (valid in this form) were not written either.
        Assert.Null((await _web.Settings.GetAlertSettingsAsync()).Email);
        Assert.Equal(90, (await _web.Settings.GetGlobalAsync()).RetentionDays);
    }

    [Fact]
    public async Task Test_email_without_saved_settings_explains_what_to_do()
    {
        await _web.SignInAsAdminAsync();
        var r = await _web.PostAsync("/settings", "/settings/test-email");
        Assert.Contains("No email is saved yet", r.Body);
    }

    [Fact]
    public async Task Test_email_reports_a_send_failure_in_words()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/settings", "/settings", SettingsForm(("email_host", "127.0.0.1"), ("email_port", "1"), ("email_ssl", ""), ("email_user", ""), ("email_password", "")));

        var r = await _web.PostAsync("/settings", "/settings/test-email");

        Assert.Contains("Could not send", r.Body);
    }

    // ---------------------------------------------------------------- account

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_takes_effect()
    {
        await _web.SignInAsAdminAsync();

        var wrong = await _web.PostAsync("/account", "/account", ("current", "not-it-at-all"), ("new", "brand-new-password"), ("confirm", "brand-new-password"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains("current password is not correct", wrong.Body);

        var mismatch = await _web.PostAsync("/account", "/account", ("current", "a-long-enough-password"), ("new", "brand-new-password"), ("confirm", "something-else-entirely"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);

        var ok = await _web.PostAsync("/account", "/account", ("current", "a-long-enough-password"), ("new", "brand-new-password"), ("confirm", "brand-new-password"));
        Assert.Equal(HttpStatusCode.Redirect, ok.Status);
        Assert.Equal(LoginResult.Ok, await _web.Settings.VerifyLoginAsync("admin", "brand-new-password", DateTime.UtcNow));
        Assert.Equal(LoginResult.Invalid, await _web.Settings.VerifyLoginAsync("admin", "a-long-enough-password", DateTime.UtcNow));
    }
}
