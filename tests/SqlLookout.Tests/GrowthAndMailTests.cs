using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Mcp;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public sealed class GrowthAndMailTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-growth-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;
    private readonly FakeSender _sender = new();
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Srv = ["srv"];
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);
    private static readonly AlertThresholds T = new();
    private const double Gb = 1024;

    private sealed class FakeSender : IAlertSender
    {
        public List<(string Subject, string Body)> Sent { get; } = [];
        public Task SendAsync(string subject, string body, CancellationToken ct)
        {
            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        _store = new SqliteHistoryStore(_file);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private Task Sizes(DateTime at, params (string Db, double DataGb, double LogGb)[] dbs) =>
        _store.AppendAsync("srv", "file_size", at, TimeSpan.Zero, CollectionResult.Ok(dbs.SelectMany(d => new[]
        {
            new Sample("db_data_mb", d.Db, d.DataGb * Gb), new Sample("db_log_mb", d.Db, d.LogGb * Gb),
        }).ToList()));

    private async Task<List<DatabaseGrowthRow>> Rows(double hours = 24) =>
        await DatabaseGrowth.ReadAsync(_store, "srv", await _store.GetLatestAsync("srv"), Now.AddHours(-hours));

    private static DatabaseGrowthRow Row(double sizeGb, double grownGb, string db = "Sales") =>
        new(db, sizeGb * Gb, 0, grownGb * Gb, 0, Now.AddHours(-24));

    [Fact]
    public async Task Growth_compares_the_newest_size_with_the_oldest_inside_the_window_data_and_log_apart()
    {
        await Sizes(Now.AddHours(-30), ("Sales", 10, 1));
        await Sizes(Now.AddHours(-23), ("Sales", 150, 5), ("Small", 1, 1));
        await Sizes(Now.AddHours(-10), ("Sales", 200, 60), ("Small", 1, 1), ("Dropped", 5, 1));
        await Sizes(Now, ("Sales", 279, 105), ("Small", 1, 1));

        var rows = await Rows();

        Assert.Equal(["Sales", "Small"], rows.Select(r => r.Database));
        Assert.Equal(129 * Gb, rows[0].DataGrownMb);
        Assert.Equal(100 * Gb, rows[0].LogGrownMb);
        Assert.Equal(384 * Gb, rows[0].SizeMb);
        Assert.Equal(Now.AddHours(-23), rows[0].SinceUtc);
        Assert.Equal(0, rows[1].GrownMb);
    }

    [Fact]
    public async Task A_database_seen_for_the_first_time_has_not_grown()
    {
        await Sizes(Now, ("Restored", 500, 50));
        var row = Assert.Single(await Rows());
        Assert.Equal(0, row.GrownMb);
        Assert.Null(AlertRules.DbGrowthFinding("srv", [row], T));
    }

    [Theory]
    [InlineData(1000, 19.9, null)]
    [InlineData(1000, 20, Severity.Warning)]
    [InlineData(1000, 49.9, Severity.Warning)]
    [InlineData(1000, 50, Severity.Critical)]
    [InlineData(17, 4.9, null)]                  // 40 % but under the smallest size the percentage counts from
    [InlineData(26, 6, Severity.Warning)]        // 30 % and over 5 GB
    [InlineData(100, 19, null)]                  // 23 % and under 20 GB
    [InlineData(10, 10, null)]                   // nothing at the start of the window: no percentage to compare with
    [InlineData(1000, -200, null)]
    public void Growth_limits(double sizeGb, double grownGb, Severity? expected) =>
        Assert.Equal(expected, AlertRules.DbGrowthFinding("srv", [Row(sizeGb, grownGb)], T)?.Severity);

    [Fact]
    public void Several_databases_make_one_problem_largest_first()
    {
        var f = AlertRules.DbGrowthFinding("srv", [Row(900, 25, "B"), Row(900, 60, "A"), Row(900, 1, "C")], T)!;
        Assert.Equal("srv|dbgrowth|", f.Key);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.True(f.Detail.IndexOf("A: +60 GB", StringComparison.Ordinal) < f.Detail.IndexOf("B: +25 GB", StringComparison.Ordinal));
        Assert.DoesNotContain("C:", f.Detail);
    }

    [Fact]
    public async Task The_evaluator_raises_growth_respects_the_switch_and_ignore_list_and_names_it_on_disk_problems()
    {
        await Sizes(Now.AddHours(-20), ("Sales", 150, 5), ("Scratch", 1, 1));
        await Sizes(Now, ("Sales", 279, 105), ("Scratch", 200, 1));
        await _store.AppendAsync("srv", "disk_space", Now, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("disk_total_bytes", "D:\\", 500e9), new Sample("disk_free_bytes", "D:\\", 1e9)]));
        Func<string, IReadOnlyList<string>> ignore = _ => ["Scratch"];

        var all = await new AlertEvaluator(_store, T, new HashSet<string>(), ignore).EvaluateAsync("srv", Now);
        var growth = Assert.Single(all, f => f.Key == "srv|dbgrowth|");
        Assert.Contains("Sales", growth.Detail);
        Assert.DoesNotContain("Scratch", growth.Detail);
        var disk = Assert.Single(all, f => f.Key == "srv|disk|D:\\");
        Assert.Contains("grew most", disk.Detail);
        Assert.Contains("Sales: +229 GB", disk.Detail);
        Assert.DoesNotContain("Scratch", disk.Detail);

        var off = await new AlertEvaluator(_store, T, new HashSet<string> { AlertRules.RuleDbGrowth }, ignore).EvaluateAsync("srv", Now);
        Assert.DoesNotContain(off, f => f.Key == "srv|dbgrowth|");
        Assert.Contains("Sales", Assert.Single(off, f => f.Key == "srv|disk|D:\\").Detail);
    }

    // ---- which kinds are mailed

    private AlertDispatcher Dispatcher(string[] noMail, DigestSchedule? digest = null) =>
        new(_store, _sender, TimeSpan.FromHours(4), null, digest: digest, noMailRules: noMail.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static Finding Disk(Severity s = Severity.Warning) => new("srv|disk|D:\\", "srv", s, "Disk D is low", "detail");
    private static Finding Growth() => new("srv|dbgrowth|", "srv", Severity.Warning, "Fast growth", "detail");

    [Fact]
    public async Task A_kind_that_is_not_mailed_is_recorded_and_the_others_are_still_mailed()
    {
        await Dispatcher(["disk"]).DispatchAsync([Disk(), Growth()], Srv, Now, Noon);

        var mail = Assert.Single(_sender.Sent);
        Assert.Contains("Fast growth", mail.Body);
        Assert.DoesNotContain("Disk D is low", mail.Body);
        Assert.Equal(2, (await _store.GetActiveAlertsAsync()).Count);
    }

    [Fact]
    public async Task A_not_mailed_problem_resolves_without_a_mail()
    {
        var d = Dispatcher(["disk"]);
        await d.DispatchAsync([Disk()], Srv, Now, Noon);
        await d.DispatchAsync([Disk(Severity.Critical)], Srv, Now.AddHours(5), Noon);
        Assert.Equal("Critical", Assert.Single(await _store.GetActiveAlertsAsync()).Severity);
        await d.DispatchAsync([], Srv, Now.AddHours(6), Noon);

        Assert.Empty(_sender.Sent);
        Assert.Empty(await _store.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task Turning_mail_back_on_sends_the_open_problem_as_new()
    {
        await Dispatcher(["disk"]).DispatchAsync([Disk()], Srv, Now, Noon);
        await Dispatcher([]).DispatchAsync([Disk()], Srv, Now.AddMinutes(1), Noon);

        Assert.Contains("(new)", Assert.Single(_sender.Sent).Body);
        Assert.Equal(Now, Assert.Single(await _store.GetActiveAlertsAsync()).FirstUtc);
    }

    [Fact]
    public async Task Switching_mail_off_for_a_mailed_problem_silences_its_repeat_and_its_resolved_mail()
    {
        await Dispatcher([]).DispatchAsync([Disk()], Srv, Now, Noon);
        var off = Dispatcher(["disk"]);
        await off.DispatchAsync([Disk()], Srv, Now.AddHours(5), Noon);
        await off.DispatchAsync([], Srv, Now.AddHours(6), Noon);

        Assert.Single(_sender.Sent);
        Assert.Empty(await _store.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task A_not_mailed_problem_closes_at_once_even_while_a_digest_is_waiting()
    {
        var digest = new DigestSchedule(TimeSpan.Zero, TimeSpan.FromHours(24));
        var d = Dispatcher(["disk"], digest);
        await d.DispatchAsync([Disk()], Srv, Now, Noon);
        await d.DispatchAsync([], Srv, Now.AddMinutes(1), Noon);

        Assert.Empty(_sender.Sent);
        Assert.Empty(await _store.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task The_daily_summary_counts_but_does_not_list_not_mailed_problems()
    {
        var body = await DailySummary.BuildAsync(_store, Srv, Now, [Disk(), Growth()], default, new HashSet<string> { "disk" });
        Assert.Contains("Fast growth", body);
        Assert.DoesNotContain("Disk D is low", body);
        Assert.Contains("1 more problem(s)", body);
    }

    [Fact]
    public void Every_rule_but_the_system_database_switch_has_a_kind_that_follows_it()
    {
        string[] kinds = ["dbstate", "suspect", "backup", "dbcc", "disk", "diskfall", "dbgrowth", "log", "jobfail", "jobslow", "cpu", "blocking", "ple", "grants",
            "deadlocks", "connections", "ioread", "iowrite", "ag", "logship", "mirror", "express", "tempdb", "failed_logins"];
        var rules = typeof(AlertRules).GetFields().Where(f => f.IsLiteral && f.Name.StartsWith("Rule", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).Where(r => r != AlertRules.RuleSystemDbBackup).Order().ToList();

        Assert.Equal(rules, kinds.Select(k => MailChoices.RuleOfKind(k)!).Distinct().Order().ToList());
        Assert.True(MailChoices.IsMailed("srv|unreachable|", rules.ToHashSet()));
    }

    [Fact]
    public async Task The_settings_page_saves_the_mail_column_and_an_old_form_leaves_it_alone()
    {
        await using var web = await WebFixture.StartAsync();
        await web.SignInAsAdminAsync();
        string[] rules = ["backup", "disk", "db_growth"];
        (string, string)[] Form(params (string, string)[] extra)
        {
            var fields = new Dictionary<string, string> { ["renotify"] = "4", ["retention"] = "90", ["language"] = "en" };
            foreach (var rule in rules) fields["rule_" + rule] = "1";
            foreach (var c in CollectorCatalog.All()) fields["col_" + c.Name] = "1";
            foreach (var p in typeof(AlertThresholds).GetProperties())
                fields["th_" + p.Name] = Convert.ToString(p.GetValue(new AlertThresholds()), System.Globalization.CultureInfo.InvariantCulture)!;
            foreach (var (k, v) in extra) fields[k] = v;
            return fields.Select(kv => (kv.Key, kv.Value)).ToArray();
        }

        var page = (await web.GetAsync("/settings")).Body;
        Assert.Contains("name=\"mail_db_growth\" value=\"1\" aria-label=\"Send mail: Database grew too fast\" checked", page);
        Assert.DoesNotContain("name=\"mail_backup_system_db\"", page);

        var saved = await web.PostAsync("/settings", "/settings", Form(("mail_choices", "1"), ("mail_disk", "1"), ("mail_db_growth", "1")));
        Assert.Equal(HttpStatusCode.Redirect, saved.Status);
        var off = web.Live.Current.Alerts.NoMailRules;
        Assert.Contains("backup", off);
        Assert.DoesNotContain("disk", off);
        Assert.DoesNotContain("db_growth", off);
        Assert.DoesNotContain("name=\"mail_backup\" value=\"1\" aria-label=\"Send mail: Backups too old or missing\" checked", (await web.GetAsync("/settings")).Body);

        var old = await web.PostAsync("/settings", "/settings", Form());
        Assert.Equal(HttpStatusCode.Redirect, old.Status);
        Assert.Equal(off, web.Live.Current.Alerts.NoMailRules);
    }

    // ---- MCP periodic check

    private static JsonElement Json(object o) => JsonDocument.Parse(MonitorQueries.ToJson(o)).RootElement;
    private static List<string> Titles(JsonElement e, string property) => e.GetProperty(property).EnumerateArray().Select(a => a.GetProperty("title").GetString()!).ToList();

    [Fact]
    public async Task Check_changes_reports_each_change_once_when_the_next_time_is_passed_back()
    {
        var q = new MonitorQueries(_store);
        await Sizes(Now.AddHours(-20), ("Sales", 150, 5));
        await Sizes(Now, ("Sales", 279, 105));
        await _store.UpsertAlertAsync(new AlertRecord("srv|a|", "srv", "Critical", "Old and open", "x", Now.AddHours(-5), Now, null));
        await _store.UpsertAlertAsync(new AlertRecord("srv|b|", "srv", "Warning", "Just opened", "x", Now.AddMinutes(-2), Now, null));
        await _store.UpsertAlertAsync(new AlertRecord("srv|d|", "srv", "Warning", "Not settled yet", "x", Now.AddMinutes(-1), Now, null));
        await _store.UpsertAlertAsync(new AlertRecord("srv|c|", "srv", "Warning", "Just resolved", "x", Now.AddHours(-6), Now, null));
        await _store.ResolveAlertAsync("srv|c|", Now.AddMinutes(-3));

        var first = Json(await q.CheckChangesAsync(null, 24, Now, default));
        Assert.True(first.GetProperty("changed").GetBoolean());
        Assert.Equal(["Just opened"], Titles(first, "opened"));
        Assert.Equal(["Just resolved"], Titles(first, "resolved"));
        Assert.Equal(1, first.GetProperty("openCritical").GetInt32());
        Assert.Equal(2, first.GetProperty("openWarning").GetInt32());
        Assert.Empty(first.GetProperty("noRecentData").EnumerateArray());
        var top = Assert.Single(first.GetProperty("fastestGrowing").EnumerateArray());
        Assert.Equal(229 * Gb, top.GetProperty("growth").GetProperty("grownMb").GetDouble());

        var next = first.GetProperty("nextSinceUtc").GetString();
        var second = Json(await q.CheckChangesAsync(next, 24, Now.AddMinutes(5), default));
        Assert.Equal(["Not settled yet"], Titles(second, "opened"));
        Assert.Empty(second.GetProperty("resolved").EnumerateArray());

        var third = Json(await q.CheckChangesAsync(second.GetProperty("nextSinceUtc").GetString(), 24, Now.AddMinutes(10), default));
        Assert.Empty(third.GetProperty("opened").EnumerateArray());
        Assert.Empty(third.GetProperty("resolved").EnumerateArray());
    }

    [Fact]
    public async Task Check_changes_flags_old_data_and_shows_only_servers_the_key_may_see()
    {
        var q = new MonitorQueries(_store);
        await Sizes(Now.AddMinutes(-40), ("Sales", 1, 1));
        await _store.AppendAsync("other", "cpu", Now, TimeSpan.Zero, CollectionResult.Ok([new Sample("cpu_total_percent", null, 1)]));
        await _store.UpsertAlertAsync(new AlertRecord("other|a|", "other", "Critical", "Secret", "x", Now.AddMinutes(-1), Now, null));

        var mine = Json(await q.CheckChangesAsync(null, 24, Now, default, new InstanceScope(["srv"])));

        Assert.Empty(mine.GetProperty("opened").EnumerateArray());
        Assert.Equal(0, mine.GetProperty("openCritical").GetInt32());
        Assert.Equal("srv", Assert.Single(mine.GetProperty("noRecentData").EnumerateArray()).GetProperty("instance").GetString());
        Assert.True(mine.GetProperty("changed").GetBoolean());
        await Assert.ThrowsAsync<ArgumentException>(() => q.GetDatabaseGrowthAsync("other", 24, Now, default, new InstanceScope(["srv"])));
    }

    [Fact]
    public async Task Hourly_checks_without_a_cutoff_leave_no_gap()
    {
        var q = new MonitorQueries(_store);
        await _store.UpsertAlertAsync(new AlertRecord("srv|a|", "srv", "Warning", "Between runs", "x", Now.AddMinutes(-1), Now, null));
        await _store.AppendAsync("srv", "cpu", Now, TimeSpan.Zero, CollectionResult.Ok([new Sample("cpu_total_percent", null, 1)]));

        Assert.Empty(Titles(Json(await q.CheckChangesAsync(null, 24, Now, default)), "opened"));
        Assert.Equal(["Between runs"], Titles(Json(await q.CheckChangesAsync(null, 24, Now.AddHours(1), default)), "opened"));
    }

    [Theory]
    [InlineData("yesterday", 24)]
    [InlineData(null, 0)]
    [InlineData(null, 721)]
    public async Task Check_changes_refuses_bad_input(string? since, double hours) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new MonitorQueries(_store).CheckChangesAsync(since, hours, Now, default));
}
