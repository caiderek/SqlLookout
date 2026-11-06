using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>Rules for availability groups, log shipping and mirroring: numeric codes in, one finding per instance out.</summary>
public sealed class HaAlertTests
{
    private static readonly AlertThresholds T = new();
    private static readonly HashSet<string> None = [];
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static StoredSample S(string collector, string metric, string? subject, double? value, string? text = null, int minutesAgo = 0) =>
        new("srv", collector, Now.AddMinutes(-minutesAgo), new Sample(metric, subject, value, text));

    private static StoredSample Ag(string metric, string? subject, double? value, string? text = null, int minutesAgo = 0) =>
        S(HaAlertRules.AgCollector, metric, subject, value, text, minutesAgo);

    private static List<Finding> Eval(params StoredSample[] latest) => AlertRules.Evaluate("srv", latest, T, None);

    // ---------------------------------------------------------------- availability groups

    [Fact]
    public void Healthy_groups_are_quiet()
    {
        var findings = Eval(
            Ag("ag_replicas", null, 2),
            Ag("ag_replica_connected", "AG1 / A", 1, "PRIMARY"), Ag("ag_replica_health", "AG1 / A", 2, "HEALTHY"),
            Ag("ag_replica_connected", "AG1 / B", 1, "SECONDARY"), Ag("ag_replica_health", "AG1 / B", 2, "HEALTHY"),
            Ag("ag_db_suspended", "AG1 / B / Sales", 0, ""), Ag("ag_db_health", "AG1 / B / Sales", 2, "SYNCHRONIZED"));

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData(1, 2, null)]
    [InlineData(1, 1, Severity.Warning)]
    [InlineData(1, 0, Severity.Critical)]
    [InlineData(0, 0, Severity.Critical)]
    public void Replica_levels_follow_connection_and_health_codes(int connected, int health, Severity? expected)
    {
        var f = HaAlertRules.AvailabilityGroups("srv",
            [Ag("ag_replicas", null, 1), Ag("ag_replica_connected", "AG1 / B", connected, "SECONDARY"), Ag("ag_replica_health", "AG1 / B", health, "X")],
            _ => false);

        Assert.Equal(expected, f?.Severity);
    }

    [Fact]
    public void A_disconnected_replica_is_listed_once_not_twice()
    {
        var f = HaAlertRules.AvailabilityGroups("srv",
            [Ag("ag_replicas", null, 1), Ag("ag_replica_connected", "AG1 / B", 0, "SECONDARY"), Ag("ag_replica_health", "AG1 / B", 0, "NOT_HEALTHY")],
            _ => false)!;

        Assert.Equal("srv|ag|", f.Key);
        Assert.Equal("Availability group problem: 1 item(s)", f.Title);
        Assert.Contains("AG1 / B is disconnected", f.Detail);
    }

    [Fact]
    public void A_suspended_database_is_critical_and_a_partly_healthy_one_is_a_warning()
    {
        var suspended = HaAlertRules.AvailabilityGroups("srv",
            [Ag("ag_replicas", null, 2), Ag("ag_db_suspended", "AG1 / B / Sales", 1, "SUSPEND_FROM_USER"), Ag("ag_db_health", "AG1 / B / Sales", 2, "SYNCHRONIZED")],
            _ => false)!;
        var partly = HaAlertRules.AvailabilityGroups("srv",
            [Ag("ag_replicas", null, 2), Ag("ag_db_suspended", "AG1 / B / Sales", 0, ""), Ag("ag_db_health", "AG1 / B / Sales", 1, "SYNCHRONIZING")],
            _ => false)!;

        Assert.Equal(Severity.Critical, suspended.Severity);
        Assert.Contains("SUSPEND_FROM_USER", suspended.Detail);
        Assert.Equal(Severity.Warning, partly.Severity);
    }

    [Fact]
    public void Many_problems_make_one_finding_with_the_worst_severity_first()
    {
        var f = HaAlertRules.AvailabilityGroups("srv",
        [
            Ag("ag_replicas", null, 2),
            Ag("ag_replica_connected", "AG1 / B", 1, "SECONDARY"), Ag("ag_replica_health", "AG1 / B", 1, "PARTIALLY_HEALTHY"),
            Ag("ag_replica_connected", "AG1 / C", 0, "SECONDARY"),
        ], _ => false)!;

        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Availability group problem: 2 item(s)", f.Title);
        Assert.True(f.Detail.IndexOf("AG1 / C", StringComparison.Ordinal) < f.Detail.IndexOf("AG1 / B", StringComparison.Ordinal));
    }

    [Fact]
    public void A_removed_group_stops_alerting_because_only_the_newest_run_counts()
    {
        // The old run saw a disconnected replica; the newest run saw no group at all and wrote only the empty count.
        var latest = new[]
        {
            Ag("ag_replica_connected", "AG1 / B", 0, "SECONDARY", minutesAgo: 5),
            Ag("ag_replicas", null, 0),
        };

        Assert.Empty(Eval(latest));
    }

    [Fact]
    public void Ignored_databases_are_left_out_of_the_group_findings_but_replica_problems_stay()
    {
        var latest = new[]
        {
            Ag("ag_replicas", null, 2), Ag("ag_db_suspended", "AG1 / B / Scratch", 1, "SUSPEND_FROM_USER"), Ag("ag_db_health", "AG1 / B / Scratch", 0, "X"),
            Ag("ag_replica_connected", "AG1 / B", 0, "SECONDARY"),
        };

        var f = AlertRules.Evaluate("srv", latest, T, None, ["Scratch"]).Single();

        Assert.Contains("AG1 / B is disconnected", f.Detail);
        Assert.DoesNotContain("Scratch", f.Detail);
    }

    [Fact]
    public void Group_rule_can_be_switched_off()
    {
        var latest = new[] { Ag("ag_replicas", null, 1), Ag("ag_replica_connected", "AG1 / B", 0, "SECONDARY") };

        Assert.Empty(AlertRules.Evaluate("srv", latest, T, new HashSet<string> { AlertRules.RuleAvailabilityGroups }));
    }

    // ---------------------------------------------------------------- log shipping

    private static StoredSample Ls(string metric, string db, double? value, int minutesAgo = 0) =>
        S(HaAlertRules.LogShippingCollector, metric, db, value, null, minutesAgo);

    [Theory]
    [InlineData(59, null)]
    [InlineData(60, Severity.Warning)]
    [InlineData(119, Severity.Warning)]
    [InlineData(120, Severity.Critical)]
    public void Backup_age_is_late_at_the_limit_and_critical_at_twice_the_limit(double age, Severity? expected)
    {
        var f = HaAlertRules.LogShipping("srv", [Ls("ls_backup_age_min", "Sales", age), Ls("ls_backup_threshold_min", "Sales", 60)], T, _ => false);

        Assert.Equal(expected, f?.Severity);
    }

    [Fact]
    public void A_database_name_containing_the_separator_is_still_matched_by_the_ignore_list()
    {
        var latest = new[] { Ag("ag_db_suspended", "AG1 / B / odd / name", 1, "USER") };

        Assert.NotNull(HaAlertRules.AvailabilityGroups("srv", latest, _ => false));
        Assert.Null(HaAlertRules.AvailabilityGroups("srv", latest, db => db == "odd / name"));
    }

    [Fact]
    public void A_database_repeated_with_a_server_suffix_pairs_its_own_limit_and_is_ignored_by_its_plain_name()
    {
        var latest = new[]
        {
            Ls("ls_backup_age_min", "Sales (A)", 100), Ls("ls_backup_threshold_min", "Sales (A)", 60),
            Ls("ls_backup_age_min", "Sales (B)", 100), Ls("ls_backup_threshold_min", "Sales (B)", 300),
        };

        var f = HaAlertRules.LogShipping("srv", latest, T, _ => false)!;
        Assert.Contains("Sales (A)", f.Detail);
        Assert.DoesNotContain("Sales (B)", f.Detail);
        Assert.Null(HaAlertRules.LogShipping("srv", latest, T, db => db == "Sales"));
    }

    [Fact]
    public void Restore_age_uses_its_own_limit_and_the_multiplier_is_a_setting()
    {
        var latest = new[] { Ls("ls_restore_age_min", "Sales", 100), Ls("ls_restore_threshold_min", "Sales", 45) };

        Assert.Equal(Severity.Critical, HaAlertRules.LogShipping("srv", latest, T, _ => false)!.Severity);
        Assert.Equal(Severity.Warning, HaAlertRules.LogShipping("srv", latest, new AlertThresholds { LogShippingCriticalMultiplier = 3 }, _ => false)!.Severity);
    }

    [Fact]
    public void A_job_that_never_ran_is_a_warning_and_a_missing_limit_is_skipped()
    {
        var never = HaAlertRules.LogShipping("srv", [Ls("ls_backup_age_min", "Sales", null), Ls("ls_backup_threshold_min", "Sales", 60)], T, _ => false)!;
        var noLimit = HaAlertRules.LogShipping("srv", [Ls("ls_backup_age_min", "Sales", 999), Ls("ls_backup_threshold_min", "Sales", 0)], T, _ => false);

        Assert.Equal(Severity.Warning, never.Severity);
        Assert.Contains("no log backup has been recorded yet", never.Detail);
        Assert.Null(noLimit);
    }

    [Fact]
    public void Log_shipping_groups_databases_into_one_finding_and_honours_ignore_and_switch()
    {
        var latest = new[]
        {
            Ls("ls_backup_age_min", "A", 70), Ls("ls_backup_threshold_min", "A", 60),
            Ls("ls_backup_age_min", "B", 500), Ls("ls_backup_threshold_min", "B", 60),
            Ls("ls_backup_age_min", "Scratch", 500), Ls("ls_backup_threshold_min", "Scratch", 60),
        };

        var f = AlertRules.Evaluate("srv", latest, T, None, ["Scratch"]).Single();

        Assert.Equal("srv|logship|", f.Key);
        Assert.Equal("Log shipping is late: 2 database(s)", f.Title);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.DoesNotContain("Scratch", f.Detail);
        Assert.Empty(AlertRules.Evaluate("srv", latest, T, new HashSet<string> { AlertRules.RuleLogShipping }));
    }

    [Fact]
    public void A_log_shipping_pair_that_was_removed_no_longer_alerts()
    {
        var latest = new[]
        {
            Ls("ls_backup_age_min", "Old", 500, minutesAgo: 10), Ls("ls_backup_threshold_min", "Old", 60, minutesAgo: 10),
            S(HaAlertRules.LogShippingCollector, "logship_items", null, 0),
        };

        Assert.Empty(Eval(latest));
    }

    [Fact]
    public void The_multiplier_must_be_above_one()
    {
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { LogShippingCriticalMultiplier = 1 }.Validate());
        new AlertThresholds().Validate();
    }

    // ---------------------------------------------------------------- mirroring

    private static StoredSample Mi(string metric, string db, double? value, string? text = null, int minutesAgo = 0) =>
        S(HaAlertRules.MirroringCollector, metric, db, value, text, minutesAgo);

    [Theory]
    [InlineData(0, Severity.Critical)]
    [InlineData(1, Severity.Critical)]
    [InlineData(2, null)]
    [InlineData(3, null)]
    [InlineData(4, null)]
    public void Mirroring_is_a_problem_only_when_suspended_or_disconnected(int state, Severity? expected)
    {
        var f = HaAlertRules.Mirroring("srv", [Mi("mirroring_state", "Sales", state, "PRINCIPAL X")], _ => false);

        Assert.Equal(expected, f?.Severity);
    }

    [Fact]
    public void A_disconnected_witness_is_a_warning_and_an_unmirrored_instance_is_quiet()
    {
        var witness = HaAlertRules.Mirroring("srv", [Mi("mirroring_state", "Sales", 4, "PRINCIPAL SYNCHRONIZED"), Mi("mirroring_witness_state", "Sales", 2)], _ => false)!;

        Assert.Equal(Severity.Warning, witness.Severity);
        Assert.Equal("srv|mirror|", witness.Key);
        Assert.Empty(Eval(S(HaAlertRules.MirroringCollector, "mirroring_databases", null, 0)));
    }

    [Fact]
    public void Mirroring_findings_honour_ignore_and_switch_and_the_newest_run()
    {
        var latest = new[] { Mi("mirroring_state", "Scratch", 1, "PRINCIPAL DISCONNECTED"), Mi("mirroring_state", "Gone", 0, "PRINCIPAL SUSPENDED", minutesAgo: 9), S(HaAlertRules.MirroringCollector, "mirroring_databases", null, 1) };

        Assert.Empty(AlertRules.Evaluate("srv", latest, T, None, ["Scratch"]));
        Assert.Empty(AlertRules.Evaluate("srv", latest, T, new HashSet<string> { AlertRules.RuleMirroring }));
    }

    [Fact]
    public void The_three_collectors_are_registered_and_named_like_their_samples_expect()
    {
        var names = CollectorCatalog.All().Select(c => c.Name).ToList();

        Assert.Contains(HaAlertRules.AgCollector, names);
        Assert.Contains(HaAlertRules.LogShippingCollector, names);
        Assert.Contains(HaAlertRules.MirroringCollector, names);
    }
}
