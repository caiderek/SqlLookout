using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public class AlertRulesTests
{
    private static readonly AlertThresholds T = new();
    private static readonly HashSet<string> None = [];
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static StoredSample S(string collector, string metric, string? subject, double? value, string? text = null) =>
        new("srv", collector, Now, new Sample(metric, subject, value, text));

    private static List<Finding> Eval(params StoredSample[] samples) => AlertRules.Evaluate("srv", samples, T, None);

    // ---- backup ----
    [Theory]
    [InlineData(26 * 60, null)]                       // exactly at the limit: still fine
    [InlineData(26 * 60 + 1, Severity.Warning)]
    [InlineData(48 * 60, Severity.Warning)]
    [InlineData(48 * 60 + 1, Severity.Critical)]
    public void Full_backup_age_thresholds(int minutes, Severity? expected)
    {
        var f = AlertRules.BackupFinding("srv", "Sales", minutes, T);
        Assert.Equal(expected, f?.Severity);
    }

    [Fact]
    public void Never_backed_up_is_critical_but_tempdb_is_ignored()
    {
        Assert.Equal(Severity.Critical, AlertRules.BackupFinding("srv", "Sales", null, T)!.Severity);
        Assert.Null(AlertRules.BackupFinding("srv", "tempdb", null, T));
    }

    [Fact]
    public void Offline_database_keeps_its_backup_alert_so_it_does_not_clear_and_fire_again()
    {
        var findings = Eval(
            S("database_state", "db_state", "Old", 6, "OFFLINE"),
            S("backup_age", "last_full_backup_age_min", "Old", null));
        Assert.Contains(findings, f => f.Key.Contains("|backup|"));
        Assert.Contains(findings, f => f.Key.Contains("|dbstate|") && f.Severity == Severity.Critical);
    }

    [Theory]
    [InlineData(1)]    // RESTORING: a standby or a restore in progress is not expected to have backups
    [InlineData(2)]    // RECOVERING
    public void Restoring_database_is_not_judged_on_backup_age(int state)
    {
        var findings = Eval(
            S("database_state", "db_state", "Standby", state, "RESTORING"),
            S("backup_age", "last_full_backup_age_min", "Standby", null));
        Assert.Empty(findings);
    }

    [Fact]
    public void Titles_and_details_round_ages_the_same_way()
    {
        var f = AlertRules.BackupFinding("srv", "Sales", 115.5 * 60, T)!;
        Assert.Contains("115.5 h", f.Title);
        Assert.Contains("115.5 h", f.Detail);
    }

    // ---- database state ----
    [Theory]
    [InlineData(0, false)]   // ONLINE
    [InlineData(1, false)]   // RESTORING: normal on a standby
    [InlineData(2, false)]   // RECOVERING
    [InlineData(3, true)]    // RECOVERY_PENDING
    [InlineData(4, true)]    // SUSPECT
    [InlineData(5, true)]    // EMERGENCY
    [InlineData(6, true)]    // OFFLINE
    public void Database_state_codes(int state, bool alerts)
    {
        var findings = Eval(S("database_state", "db_state", "Sales", state, "X"));
        Assert.Equal(alerts, findings.Count == 1);
    }

    [Fact]
    public void Suspect_pages_alert_only_when_positive()
    {
        Assert.Empty(Eval(S("suspect_pages", "suspect_page_count", "Sales", 0)));
        Assert.Equal(Severity.Critical, Eval(S("suspect_pages", "suspect_page_count", "Sales", 2)).Single().Severity);
    }

    // ---- dbcc ----
    [Theory]
    [InlineData(8 * 1440, null)]
    [InlineData(8 * 1440 + 1, Severity.Warning)]
    [InlineData(15 * 1440 + 1, Severity.Critical)]
    public void Dbcc_age_thresholds(int minutes, Severity? expected) =>
        Assert.Equal(expected, AlertRules.DbccFinding("srv", "Sales", new Sample("dbcc_last_good_age_min", "Sales", minutes), T)?.Severity);

    [Fact]
    public void Dbcc_unknown_never_alerts_and_never_checked_only_warns()
    {
        Assert.Null(AlertRules.DbccFinding("srv", "Sales", new Sample("dbcc_last_good_age_min", "Sales", null, "unknown"), T));
        Assert.Equal(Severity.Warning, AlertRules.DbccFinding("srv", "Sales", new Sample("dbcc_last_good_age_min", "Sales", null, "never"), T)!.Severity);
    }

    [Fact]
    public void Disabled_rule_produces_nothing()
    {
        var findings = AlertRules.Evaluate("srv", [S("suspect_pages", "suspect_page_count", "Sales", 5)], T,
            new HashSet<string> { AlertRules.RuleSuspectPages });
        Assert.Empty(findings);
    }

    // ---- disk ----
    private const double Gb = 1024.0 * 1024 * 1024;

    [Theory]
    [InlineData(500, 1000, null)]                  // 50% and 500 GB free
    [InlineData(150, 1000, Severity.Warning)]      // 15% < 20%
    [InlineData(80, 1000, Severity.Critical)]      // 8% < 10%
    [InlineData(9, 100, Severity.Critical)]        // 9% is under the critical 10%
    [InlineData(12, 100, Severity.Warning)]        // 12% is under 20% but over 10%; 12 GB is over 10 GB
    public void Disk_space_thresholds(double freeGb, double totalGb, Severity? expected)
    {
        var f = AlertRules.DiskSpaceFinding("srv", "C:\\", freeGb * Gb, totalGb * Gb, T);
        Assert.Equal(expected, f?.Severity);
    }

    [Fact]
    public void Small_absolute_free_space_alerts_even_when_percent_is_fine()
    {
        // 4 GB free on a 10 GB volume is 40%, but under the 5 GB critical size.
        Assert.Equal(Severity.Critical, AlertRules.DiskSpaceFinding("srv", "E:\\", 4 * Gb, 10 * Gb, T)!.Severity);
    }

    [Fact]
    public void Disk_fall_predicts_time_to_full()
    {
        // 200 GB falling 60 GB/h for an hour leaves 140 GB, which lasts 140/60 = 2.3 h -> warning (<= 4 h, > 1 h).
        var s = Series(200 * Gb, -60 * Gb, points: 7, stepMinutes: 10);
        var f = AlertRules.DiskFallFinding("srv", "D:\\", s, T);
        Assert.NotNull(f);
        Assert.Equal(Severity.Warning, f!.Severity);
    }

    [Fact]
    public void Disk_fall_under_one_hour_is_critical()
    {
        // 100 GB falling 100 GB/h: after 30 minutes 50 GB are left, which lasts 0.5 h.
        var s = Series(100 * Gb, -100 * Gb, points: 4, stepMinutes: 10);
        var last = s[^1].Value;
        Assert.True(last > 0);
        var f = AlertRules.DiskFallFinding("srv", "D:\\", s, T);
        Assert.Equal(Severity.Critical, f?.Severity);
    }

    [Fact]
    public void Disk_fall_ignores_flat_rising_and_too_little_data()
    {
        Assert.Null(AlertRules.DiskFallFinding("srv", "D:\\", Series(100 * Gb, 0, 7, 10), T));
        Assert.Null(AlertRules.DiskFallFinding("srv", "D:\\", Series(100 * Gb, 5 * Gb, 7, 10), T));
        Assert.Null(AlertRules.DiskFallFinding("srv", "D:\\", Series(100 * Gb, -60 * Gb, 3, 10), T));   // only 3 points
        Assert.Null(AlertRules.DiskFallFinding("srv", "D:\\", Series(100 * Gb, -60 * Gb, 4, 2), T));    // only 6 minutes
    }

    [Fact]
    public void Disk_fall_slow_decline_is_quiet()
    {
        // 500 GB falling 1 GB/h lasts 500 h.
        Assert.Null(AlertRules.DiskFallFinding("srv", "D:\\", Series(500 * Gb, -1 * Gb, 7, 10), T));
    }

    private static List<(DateTime Utc, double Value)> Cpu(params double[] perMinute) =>
        perMinute.Select((v, i) => (Now.AddMinutes(i), v)).ToList();

    [Fact]
    public void Cpu_alerts_when_every_sample_in_the_period_is_at_or_above_the_limit()
    {
        var f = AlertRules.CpuFinding("srv", Cpu(Enumerable.Repeat(95.0, 11).ToArray()), T);   // 10 minutes
        Assert.Equal(Severity.Warning, f?.Severity);
        Assert.Equal("srv|cpu|", f!.Key);
        // Exactly the limit counts.
        Assert.NotNull(AlertRules.CpuFinding("srv", Cpu(Enumerable.Repeat(90.0, 11).ToArray()), T));
    }

    [Fact]
    public void Cpu_stays_quiet_for_a_dip_a_short_history_or_too_few_samples()
    {
        var dip = Enumerable.Repeat(95.0, 11).ToArray();
        dip[5] = 89.9;
        Assert.Null(AlertRules.CpuFinding("srv", Cpu(dip), T));                                        // one minute below the limit
        Assert.Null(AlertRules.CpuFinding("srv", Cpu(Enumerable.Repeat(99.0, 5).ToArray()), T));        // only 4 minutes of history
        Assert.Null(AlertRules.CpuFinding("srv", Cpu(99, 99), T));                                       // two samples
        Assert.Null(AlertRules.CpuFinding("srv", [], T));
    }

    /// <summary>Free space starting at <paramref name="start"/> and changing <paramref name="perHour"/> per hour.</summary>
    private static List<(DateTime Utc, double Value)> Series(double start, double perHour, int points, int stepMinutes) =>
        Enumerable.Range(0, points)
            .Select(i => (Now.AddMinutes(i * stepMinutes), start + perHour * (i * stepMinutes / 60.0)))
            .ToList();

    // ---- log ----
    [Theory]
    [InlineData(80, null)]
    [InlineData(81, Severity.Warning)]
    [InlineData(90, Severity.Warning)]
    [InlineData(91, Severity.Critical)]
    public void Log_usage_thresholds(double used, Severity? expected)
    {
        var findings = Eval(
            S("log_usage", "log_size_mb", "Sales", 5000),
            S("log_usage", "log_used_percent", "Sales", used));
        Assert.Equal(expected, findings.SingleOrDefault()?.Severity);
    }

    [Fact]
    public void Many_databases_over_the_log_limit_make_one_finding_worst_sets_severity()
    {
        var samples = new List<StoredSample>();
        for (var i = 0; i < 120; i++)
        {
            samples.Add(S("log_usage", "log_size_mb", "Db" + i, 5000));
            samples.Add(S("log_usage", "log_used_percent", "Db" + i, i == 7 ? 95 : 85));
        }
        var finding = Eval(samples.ToArray()).Single();
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("120 database(s)", finding.Title);
        Assert.StartsWith("Warning above", finding.Detail);
        Assert.Contains("Db7 95%", finding.Detail);
        Assert.Contains("and 100 more", finding.Detail);
    }

    [Fact]
    public void Many_databases_with_old_backups_make_one_finding_and_the_key_does_not_change_with_the_count()
    {
        var samples = new List<StoredSample>();
        for (var i = 0; i < 120; i++) samples.Add(S("backup_age", "last_full_backup_age_min", "Db" + i, i == 3 ? 100 * 60 : 30 * 60));
        var many = Eval(samples.ToArray()).Single();
        Assert.Equal(Severity.Critical, many.Severity);                 // Db3 is over 48 h
        Assert.Contains("120 database(s)", many.Title);
        Assert.StartsWith("Full backup of Db3 is 100 h old", many.Detail);    // critical listed first
        Assert.Contains("and 100 more", many.Detail);
        Assert.Equal(21, many.Detail.Split('\n').Length);                      // 20 databases, one per line, plus the "and 100 more" line

        var one = Eval(S("backup_age", "last_full_backup_age_min", "Db3", 100 * 60)).Single();
        Assert.Equal(many.Key, one.Key);
        Assert.Equal("Full backup of Db3 is 100 h old", one.Title);     // a single database keeps its own wording
    }

    [Fact]
    public void Many_databases_with_old_dbcc_make_one_finding()
    {
        var samples = Enumerable.Range(0, 50).Select(i => S("dbcc_last_good", "dbcc_last_good_age_min", "Db" + i, 10 * 1440)).ToArray();
        var f = Eval(samples).Single();
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("50 database(s)", f.Title);
        Assert.EndsWith("and 30 more", f.Detail);
    }

    [Fact]
    public void Ignored_databases_are_left_out_of_every_per_database_alert_and_the_rest_still_alert()
    {
        var samples = new[]
        {
            S("backup_age", "last_full_backup_age_min", "SqlLookoutAge_1", null),
            S("backup_age", "last_full_backup_age_min", "Prod", null),
            S("dbcc_last_good", "dbcc_last_good_age_min", "SqlLookoutAge_1", 20 * 1440),
            S("log_usage", "log_size_mb", "SqlLookoutAge_1", 5000), S("log_usage", "log_used_percent", "SqlLookoutAge_1", 99),
            S("db_state", "db_state", "SqlLookoutAge_1", 5),
            S("suspect_pages", "suspect_page_count", "SqlLookoutAge_1", 3),
        };
        var ignored = new[] { "sqllookoutage_*" };
        var findings = AlertRules.Evaluate("srv", samples, T, new HashSet<string>(), ignored);

        var only = Assert.Single(findings);                              // just the backup of Prod
        Assert.Contains("|backup|", only.Key);
        Assert.Contains("Prod", only.Title);

        // without the list the ignored database does alert (guards against a filter that hides everything)
        Assert.True(AlertRules.Evaluate("srv", samples, T, new HashSet<string>()).Count > 1);
    }

    [Fact]
    public void Tiny_log_never_alerts_on_percentage()
    {
        Assert.Empty(Eval(S("log_usage", "log_size_mb", "Sales", 2), S("log_usage", "log_used_percent", "Sales", 99)));
    }

    // ---- jobs ----
    [Fact]
    public void Failed_job_is_critical_and_succeeded_job_is_quiet()
    {
        Assert.Equal(Severity.Critical, Eval(S("agent_jobs", "job_last_outcome", "Nightly", 0)).Single().Severity);
        Assert.Empty(Eval(S("agent_jobs", "job_last_outcome", "Nightly", 1)));
    }

    [Theory]
    [InlineData(25, 10, true)]    // 25 min vs average 10: over 2x
    [InlineData(20, 10, false)]   // exactly 2x: not over
    [InlineData(9, 2, false)]     // under the 10 minute floor
    public void Long_running_job(double running, double average, bool alerts)
    {
        var findings = Eval(
            S("agent_jobs", "job_running_min", "Nightly", running),
            S("agent_jobs", "job_avg_duration_min", "Nightly", average));
        Assert.Equal(alerts, findings.Count == 1);
    }

    // ---- unreachable / collector failing ----
    private static StoredRun Run(CollectionStatus status, string? message = "SQL error 53") => new("x", Now, status, message);

    [Fact]
    public void Unreachable_needs_consecutive_failures()
    {
        Assert.Null(AlertRules.UnreachableFinding("srv", [Run(CollectionStatus.Failed)], T));
        Assert.Equal(Severity.Warning, AlertRules.UnreachableFinding("srv", [Run(CollectionStatus.Failed), Run(CollectionStatus.Failed)], T)!.Severity);
        Assert.Equal(Severity.Critical, AlertRules.UnreachableFinding("srv",
            [Run(CollectionStatus.Failed), Run(CollectionStatus.Failed), Run(CollectionStatus.Failed)], T)!.Severity);
        // A success in the middle resets the count (newest first).
        Assert.Null(AlertRules.UnreachableFinding("srv", [Run(CollectionStatus.Failed), Run(CollectionStatus.Ok), Run(CollectionStatus.Failed)], T));
    }

    [Fact]
    public void Collector_failing_needs_three_in_a_row_and_ignores_unsupported()
    {
        var failing = new[] { Run(CollectionStatus.NoPermission), Run(CollectionStatus.Failed), Run(CollectionStatus.NoPermission) };
        Assert.NotNull(AlertRules.CollectorFailingFinding("srv", "disk_space", failing, T));
        Assert.Null(AlertRules.CollectorFailingFinding("srv", "disk_space", failing.Take(2).ToList(), T));
        Assert.Null(AlertRules.CollectorFailingFinding("srv", "disk_space",
            [Run(CollectionStatus.Unsupported), Run(CollectionStatus.Unsupported), Run(CollectionStatus.Unsupported)], T));
        Assert.Null(AlertRules.CollectorFailingFinding("srv", "disk_space",
            [Run(CollectionStatus.Failed), Run(CollectionStatus.Ok), Run(CollectionStatus.Failed)], T));
    }

    // ---- quiet hours ----
    [Theory]
    [InlineData("22:00", "07:00", "23:30", true)]    // wraps midnight
    [InlineData("22:00", "07:00", "03:00", true)]
    [InlineData("22:00", "07:00", "12:00", false)]
    [InlineData("22:00", "07:00", "07:00", false)]   // end is exclusive
    [InlineData("01:00", "05:00", "03:00", true)]    // same-day window
    [InlineData("01:00", "05:00", "06:00", false)]
    public void Quiet_hours_window(string start, string end, string at, bool quiet)
    {
        var q = new QuietHours(TimeSpan.Parse(start), TimeSpan.Parse(end));
        Assert.Equal(quiet, q.Contains(TimeSpan.Parse(at)));
    }
}
