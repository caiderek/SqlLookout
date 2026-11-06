using SqlLookout.Alerts;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public sealed class AlertDispatcherTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-disp-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;
    private readonly FakeSender _sender = new();
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Srv = ["srv"];

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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private AlertDispatcher Dispatcher(QuietHours? quiet = null, TimeSpan resolveAfter = default) => new(_store, _sender, TimeSpan.FromHours(4), quiet, resolveAfter);
    private static Finding F(string key, Severity s) => new(key, "srv", s, "Title " + key, "detail");
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);

    [Fact]
    public async Task New_alert_is_mailed_once_and_not_repeated_within_the_interval()
    {
        var d = Dispatcher();
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddMinutes(30), Noon);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(3), Noon);

        Assert.Single(_sender.Sent);
    }

    private AlertDispatcher Digest(bool merge = true) => new(_store, _sender, TimeSpan.FromHours(4), null, default,
        digest: new DigestSchedule(TimeSpan.Zero, TimeSpan.FromHours(24)), urgentKinds: new HashSet<string> { "unreachable" }, mergeHosts: merge);

    [Fact]
    public async Task With_a_digest_only_urgent_problems_go_out_at_once_and_the_rest_waits_for_the_next_slot()
    {
        var d = Digest();
        await d.DispatchAsync([], Srv, T0, Noon);                                    // first slot is taken, nothing to say
        var urgent = new Finding("srv|unreachable|", "srv", Severity.Critical, "Cannot connect", "x");
        await d.DispatchAsync([F("srv|backup|", Severity.Warning), F("srv|disk|C", Severity.Critical), urgent], Srv, T0.AddMinutes(5), Noon);

        var first = Assert.Single(_sender.Sent);
        Assert.Contains("Cannot connect", first.Body);
        Assert.DoesNotContain("Title srv|backup|", first.Body);
        Assert.DoesNotContain("Title srv|disk|C", first.Body);

        await d.DispatchAsync([F("srv|backup|", Severity.Warning), F("srv|disk|C", Severity.Critical), urgent], Srv, T0.AddHours(2), Noon);
        Assert.Single(_sender.Sent);                                                  // still held

        await d.DispatchAsync([F("srv|backup|", Severity.Warning), F("srv|disk|C", Severity.Critical), urgent], Srv, T0.AddHours(30), Noon);
        Assert.Equal(2, _sender.Sent.Count);                                          // next day's slot: both in one mail
        Assert.Contains("Title srv|backup|", _sender.Sent[1].Body);
        Assert.Contains("Title srv|disk|C", _sender.Sent[1].Body);

        await d.DispatchAsync([F("srv|backup|", Severity.Warning), F("srv|disk|C", Severity.Critical), urgent], Srv, T0.AddHours(31), Noon);
        Assert.Equal(2, _sender.Sent.Count);                                          // same slot: not again
    }

    [Fact]
    public async Task The_resolved_mail_of_a_non_urgent_problem_waits_for_the_digest_but_an_urgent_one_goes_at_once()
    {
        await Dispatcher().DispatchAsync([F("srv|disk|C", Severity.Warning), new("srv|unreachable|", "srv", Severity.Critical, "Cannot connect", "x")], Srv, T0, Noon);
        Assert.Single(_sender.Sent);

        var d = Digest();
        await d.DispatchAsync([F("srv|disk|C", Severity.Warning), new("srv|unreachable|", "srv", Severity.Critical, "Cannot connect", "x")], Srv, T0.AddMinutes(1), Noon);   // takes the slot
        await d.DispatchAsync([F("srv|disk|C", Severity.Warning)], Srv, T0.AddMinutes(10), Noon);                                  // urgent one cleared
        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("Cannot connect", _sender.Sent[1].Body);
        Assert.DoesNotContain("Title srv|disk|C", _sender.Sent[1].Body);

        await d.DispatchAsync([], Srv, T0.AddMinutes(20), Noon);                                                                     // disk cleared: held
        Assert.Equal(2, _sender.Sent.Count);
        await d.DispatchAsync([], Srv, T0.AddHours(30), Noon);                                                                       // next slot
        Assert.Equal(3, _sender.Sent.Count);
        Assert.Contains("Title srv|disk|C", _sender.Sent[2].Body);
    }

    [Fact]
    public async Task A_problem_that_comes_and_goes_between_digests_is_never_mailed()
    {
        var d = Digest();
        await d.DispatchAsync([], Srv, T0, Noon);
        await d.DispatchAsync([F("srv|disk|C", Severity.Warning)], Srv, T0.AddMinutes(10), Noon);
        await d.DispatchAsync([], Srv, T0.AddMinutes(20), Noon);
        await d.DispatchAsync([], Srv, T0.AddHours(30), Noon);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task The_same_problem_on_several_servers_is_one_entry_with_the_server_names_together()
    {
        var d = Dispatcher();
        string[] all = ["a", "b", "c"];
        await d.DispatchAsync([new("a|disk|C", "a", Severity.Warning, "Disk low", "d"), new("b|disk|C", "b", Severity.Warning, "Disk low", "d"), new("c|disk|C", "c", Severity.Warning, "Other", "d")], all, T0, Noon);
        var body = Assert.Single(_sender.Sent).Body;
        Assert.Contains("a, b: Disk low", body);
        Assert.Contains("c: Other", body);
        Assert.Equal(1, body.Split("Disk low").Length - 1);

        var off = new AlertDispatcher(_store, new FakeSender(), TimeSpan.FromHours(4), null, default, mergeHosts: false);
        Assert.NotNull(off);
    }

    [Fact]
    public void Backup_problems_of_a_system_database_are_skipped_when_that_rule_is_unchecked_but_user_databases_still_alert()
    {
        var none = new StoredSample("srv", "x", DateTime.UtcNow, new SqlLookout.Core.Sample("last_full_backup_age_min", "msdb", null));
        var t = new AlertThresholds();
        HashSet<string> off = [AlertRules.RuleSystemDbBackup];
        Assert.Empty(AlertRules.Evaluate("srv", [none], t, off));
        Assert.Single(AlertRules.Evaluate("srv", [none], t, new HashSet<string>()));
        var old = none with { Sample = none.Sample with { Value = 99999 } };
        Assert.Empty(AlertRules.Evaluate("srv", [old], t, off));
        Assert.Single(AlertRules.Evaluate("srv", [old], t, new HashSet<string>()));
        var user = none with { Sample = none.Sample with { Subject = "Sales" } };
        Assert.Single(AlertRules.Evaluate("srv", [user], t, off));
    }

    [Fact]
    public async Task A_multi_line_detail_is_indented_line_by_line_in_the_mail()
    {
        var d = Dispatcher();
        await d.DispatchAsync([new Finding("a|backup|", "srv", Severity.Warning, "2 database(s) overdue", "Sales 30 h\nHR 40 h")], Srv, T0, Noon);
        Assert.Contains("srv: 2 database(s) overdue" + Environment.NewLine + "    Sales 30 h" + Environment.NewLine + "    HR 40 h", _sender.Sent[0].Body);
    }

    [Fact]
    public async Task Warnings_only_in_the_summary_are_not_mailed_critical_still_is_and_no_resolved_mail_follows_for_a_warning()
    {
        var d = new AlertDispatcher(_store, _sender, TimeSpan.FromHours(4), null, default, warningsInSummaryOnly: true);
        await d.DispatchAsync([F("a", Severity.Warning), F("b", Severity.Critical)], Srv, T0, Noon);

        var mail = Assert.Single(_sender.Sent);
        Assert.Contains("Title b", mail.Body);
        Assert.DoesNotContain("Title a", mail.Body);

        await d.DispatchAsync([], Srv, T0.AddMinutes(1), Noon);                      // both clear
        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("Title b", _sender.Sent[1].Body);                           // only the critical one was ever mailed
        Assert.DoesNotContain("Title a", _sender.Sent[1].Body);
    }

    [Fact]
    public async Task A_warning_that_gets_worse_is_mailed_as_a_new_critical_problem_in_summary_only_mode()
    {
        var d = new AlertDispatcher(_store, _sender, TimeSpan.FromHours(4), null, default, warningsInSummaryOnly: true);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        Assert.Empty(_sender.Sent);
        await d.DispatchAsync([F("a", Severity.Critical)], Srv, T0.AddMinutes(1), Noon);
        Assert.Contains("Title a", Assert.Single(_sender.Sent).Body);
    }

    [Fact]
    public async Task The_daily_summary_lists_current_warnings_with_their_details_even_when_they_were_never_mailed()
    {
        var current = new[]
        {
            new Finding("srv|backup|", "srv", Severity.Warning, "3 database(s) overdue", "Sales 30 h\nHR 40 h\nOld 50 h"),
            new Finding("srv|jobfail|X", "srv", Severity.Critical, "Agent job X failed", "last run failed"),
        };
        var text = await DailySummary.BuildAsync(_store, Srv, T0, current);

        Assert.Contains("2 active alert(s):", text);
        Assert.True(text.IndexOf("[CRITICAL] srv: Agent job X failed", StringComparison.Ordinal) < text.IndexOf("[WARNING] srv: 3 database(s) overdue", StringComparison.Ordinal));
        Assert.Contains("      Sales 30 h", text.Replace(Environment.NewLine, "\n"));
        Assert.Contains("      HR 40 h", text.Replace(Environment.NewLine, "\n"));
        Assert.Contains("No active alerts.", await DailySummary.BuildAsync(_store, Srv, T0, []));
    }

    [Fact]
    public void Warnings_in_the_summary_need_the_summary_to_be_on()
    {
        Assert.Throws<InvalidDataException>(() => new SqlLookout.Service.AlertSettings { WarningsInSummaryOnly = true, SummaryTime = null }.Validate());
        new SqlLookout.Service.AlertSettings { WarningsInSummaryOnly = true, SummaryTime = "08:00" }.Validate();
        new SqlLookout.Service.AlertSettings { WarningsInSummaryOnly = false, SummaryTime = null }.Validate();
    }

    [Fact]
    public async Task A_problem_that_flickers_within_the_grace_time_sends_one_mail_not_a_pair_every_time()
    {
        var d = Dispatcher(resolveAfter: TimeSpan.FromMinutes(30));
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        for (var i = 1; i <= 6; i++)
        {
            await d.DispatchAsync([], Srv, T0.AddMinutes(i * 10 - 5), Noon);                       // gone for a moment
            await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddMinutes(i * 10), Noon);   // and back
        }
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task A_problem_gone_for_the_whole_grace_time_is_resolved_once()
    {
        var d = Dispatcher(resolveAfter: TimeSpan.FromMinutes(30));
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await d.DispatchAsync([], Srv, T0.AddMinutes(1), Noon);
        await d.DispatchAsync([], Srv, T0.AddMinutes(20), Noon);
        Assert.Single(_sender.Sent);
        await d.DispatchAsync([], Srv, T0.AddMinutes(32), Noon);
        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("Resolved", _sender.Sent[1].Subject);
        await d.DispatchAsync([], Srv, T0.AddMinutes(40), Noon);
        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task Mails_name_the_instance_so_same_named_databases_can_be_told_apart()
    {
        var d = Dispatcher();
        await d.DispatchAsync([new Finding("a|backup|Sales", "prod-01", Severity.Critical, "No full backup of Sales", "x"),
                               new Finding("b|backup|Sales", "prod-02", Severity.Critical, "No full backup of Sales", "x")], ["prod-01", "prod-02"], T0, Noon);
        Assert.Contains("prod-01, prod-02: No full backup of Sales", _sender.Sent[0].Body);

        await d.DispatchAsync([], ["prod-01", "prod-02"], T0.AddMinutes(5), Noon);
        Assert.Contains("prod-01, prod-02: No full backup of Sales", _sender.Sent[1].Body);        // the Resolved mail too
    }

    [Fact]
    public async Task Still_active_alert_is_mailed_again_after_the_renotify_interval()
    {
        var d = Dispatcher();
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(4).AddMinutes(1), Noon);

        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("still active", _sender.Sent[1].Body);
    }

    [Fact]
    public async Task Escalation_to_critical_is_mailed_immediately()
    {
        var d = Dispatcher();
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await d.DispatchAsync([F("a", Severity.Critical)], Srv, T0.AddMinutes(1), Noon);

        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("escalated", _sender.Sent[1].Body);
    }

    [Fact]
    public async Task Cleared_condition_sends_one_resolved_mail_then_can_alert_again()
    {
        var d = Dispatcher();
        await d.DispatchAsync([F("a", Severity.Critical)], Srv, T0, Noon);
        await d.DispatchAsync([], Srv, T0.AddMinutes(5), Noon);
        await d.DispatchAsync([], Srv, T0.AddMinutes(10), Noon);

        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("Resolved", _sender.Sent[1].Subject);
        Assert.Empty(await _store.GetActiveAlertsAsync());

        await d.DispatchAsync([F("a", Severity.Critical)], Srv, T0.AddMinutes(15), Noon);
        Assert.Equal(3, _sender.Sent.Count);
    }

    [Fact]
    public async Task Alerts_of_instances_that_were_not_checked_are_left_alone()
    {
        var d = Dispatcher();
        await d.DispatchAsync([F("a", Severity.Critical)], Srv, T0, Noon);
        await d.DispatchAsync([], ["other"], T0.AddMinutes(5), Noon);

        Assert.Single(_sender.Sent);
        Assert.Single(await _store.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task Quiet_hours_hold_warnings_but_not_criticals_and_warnings_go_out_afterwards()
    {
        var quiet = new QuietHours(TimeSpan.FromHours(22), TimeSpan.FromHours(7));
        var d = Dispatcher(quiet);
        var night = TimeSpan.FromHours(23);

        await d.DispatchAsync([F("w", Severity.Warning), F("c", Severity.Critical)], Srv, T0, night);
        Assert.Single(_sender.Sent);
        Assert.Contains("Title c", _sender.Sent[0].Body);
        Assert.DoesNotContain("Title w", _sender.Sent[0].Body);

        await d.DispatchAsync([F("w", Severity.Warning), F("c", Severity.Critical)], Srv, T0.AddHours(9), TimeSpan.FromHours(8));
        Assert.Equal(2, _sender.Sent.Count);
        Assert.Contains("Title w", _sender.Sent[1].Body);
    }

    // ---- acknowledge / snooze

    [Fact]
    public async Task An_acknowledged_problem_is_never_mailed_while_it_lasts()
    {
        var d = Dispatcher();
        await AlertMutes.SetAsync(_store, F("a", Severity.Warning), MuteKind.Acknowledged, null, T0);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddDays(3), Noon);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_snoozed_problem_is_quiet_until_the_time_and_mailed_after()
    {
        var d = Dispatcher();
        await AlertMutes.SetAsync(_store, F("a", Severity.Warning), MuteKind.Snoozed, T0.AddHours(2), T0);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(1), Noon);
        Assert.Empty(_sender.Sent);

        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(3), Noon);
        Assert.Single(_sender.Sent);
        Assert.Null(await AlertMutes.GetAsync(_store, "a"));
    }

    [Fact]
    public async Task A_problem_that_gets_worse_than_what_was_acknowledged_is_mailed()
    {
        var d = Dispatcher();
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await AlertMutes.SetAsync(_store, F("a", Severity.Warning), MuteKind.Acknowledged, null, T0);
        _sender.Sent.Clear();

        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(9), Noon);
        Assert.Empty(_sender.Sent);
        await d.DispatchAsync([F("a", Severity.Critical)], Srv, T0.AddHours(9), Noon);
        Assert.Single(_sender.Sent);
        Assert.Contains("escalated", _sender.Sent[0].Body);
        Assert.Null(await AlertMutes.GetAsync(_store, "a"));
    }

    [Fact]
    public async Task An_acknowledged_critical_stays_quiet_when_it_drops_to_a_warning()
    {
        var d = Dispatcher();
        await AlertMutes.SetAsync(_store, F("a", Severity.Critical), MuteKind.Acknowledged, null, T0);
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Resolving_an_acknowledged_problem_still_mails_and_forgets_the_mute()
    {
        var d = Dispatcher();
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await AlertMutes.SetAsync(_store, F("a", Severity.Warning), MuteKind.Acknowledged, null, T0);
        _sender.Sent.Clear();

        await d.DispatchAsync([], Srv, T0.AddHours(1), Noon);
        Assert.Single(_sender.Sent);
        Assert.Contains("Resolved", _sender.Sent[0].Subject);
        Assert.Empty(await AlertMutes.ListAsync(_store));

        // The same problem coming back later is a new problem and is mailed again.
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(2), Noon);
        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task An_acknowledgement_of_a_never_mailed_problem_goes_when_the_problem_goes()
    {
        var d = Dispatcher();
        await AlertMutes.SetAsync(_store, F("a", Severity.Warning), MuteKind.Acknowledged, null, T0);
        await d.DispatchAsync([], Srv, T0.AddHours(1), Noon);
        Assert.Empty(await AlertMutes.ListAsync(_store));
        Assert.Empty(_sender.Sent);

        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(2), Noon);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task A_mute_survives_a_short_gap_while_the_resolve_grace_time_runs()
    {
        var d = Dispatcher(resolveAfter: TimeSpan.FromMinutes(30));
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        await AlertMutes.SetAsync(_store, F("a", Severity.Warning), MuteKind.Acknowledged, null, T0);
        _sender.Sent.Clear();

        await d.DispatchAsync([], Srv, T0.AddMinutes(5), Noon);
        Assert.Single(await AlertMutes.ListAsync(_store));
        await d.DispatchAsync([F("a", Severity.Warning)], Srv, T0.AddHours(9), Noon);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_mute_for_a_server_that_was_not_checked_is_left_alone()
    {
        var d = Dispatcher();
        await AlertMutes.SetAsync(_store, F("a", Severity.Warning), MuteKind.Acknowledged, null, T0);
        await d.DispatchAsync([], ["other"], T0.AddHours(1), Noon);
        Assert.Single(await AlertMutes.ListAsync(_store));
    }

    [Fact]
    public async Task A_damaged_mute_value_is_ignored_not_trusted()
    {
        await _store.SetStateAsync("alert.mute.a", "Acknowledged	nonsense", default);
        await Dispatcher().DispatchAsync([F("a", Severity.Warning)], Srv, T0, Noon);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task The_mail_never_carries_a_Claude_Code_link()
    {
        await Dispatcher().DispatchAsync([new Finding("srv|disk|C:\\", "srv", Severity.Critical, "Disk low", "2% free")], Srv, T0, Noon);
        Assert.DoesNotContain("claude-cli://", _sender.Sent[0].Body);
        Assert.DoesNotContain("Claude Code", _sender.Sent[0].Body);
    }
}
