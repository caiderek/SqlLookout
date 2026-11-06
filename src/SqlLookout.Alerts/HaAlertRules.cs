using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>
/// Rules for availability groups, log shipping and database mirroring. Each of the three produces ONE finding per instance
/// (a list of what is wrong, worst severity wins), so a farm of groups or a hundred shipped databases sends one problem.
/// Decisions use numeric codes and minutes, never the description text.
/// </summary>
public static class HaAlertRules
{
    public const string AgCollector = "availability_groups";
    public const string LogShippingCollector = "log_shipping";
    public const string MirroringCollector = "mirroring";
    public const string ExpressCollector = "express_limit";

    /// <summary>
    /// The readings of the collector's newest run only. Every run of these collectors writes at least one reading, so the
    /// newest timestamp is the last run, and a group, pair or mirrored database that has since been removed no longer counts.
    /// </summary>
    public static List<StoredSample> Current(IReadOnlyList<StoredSample> latest, string collector)
    {
        var mine = latest.Where(s => s.Collector == collector).ToList();
        if (mine.Count == 0) return mine;
        var newest = mine.Max(s => s.CollectedUtc);
        return mine.Where(s => s.CollectedUtc == newest).ToList();
    }

    private static Finding? Build(string instance, string kind, List<(Severity Severity, string Line)> problems, Func<int, string> title)
    {
        if (problems.Count == 0) return null;
        var ordered = problems.OrderByDescending(p => p.Severity).ThenBy(p => p.Line, StringComparer.OrdinalIgnoreCase).ToList();
        var list = string.Join("\n", ordered.Take(20).Select(p => p.Line));
        if (ordered.Count > 20) list += "\n" + Loc.T("and {0} more", ordered.Count - 20);
        return new(AlertRules.Key(instance, kind, ""), instance, ordered[0].Severity, title(ordered.Count), list);
    }

    /// <summary>A subject is "group / replica" or "group / replica / database"; the database is everything after the second separator, so a database name containing " / " stays whole.</summary>
    private static string? DatabaseOf(string subject)
    {
        var parts = subject.Split(" / ");
        return parts.Length >= 3 ? string.Join(" / ", parts.Skip(2)) : null;
    }

    public static Finding? AvailabilityGroups(string instance, IReadOnlyList<StoredSample> latest, Func<string, bool> ignored)
    {
        var current = Current(latest, AgCollector);
        var problems = new List<(Severity, string)>();

        foreach (var s in current.Where(s => s.Sample.Metric == "ag_replica_connected" && s.Sample.Subject is not null))
        {
            var subject = s.Sample.Subject!;
            var health = current.FirstOrDefault(x => x.Sample.Metric == "ag_replica_health" && x.Sample.Subject == subject)?.Sample;
            if (s.Sample.Value is 0)
                problems.Add((Severity.Critical, Loc.T("Replica {0} is disconnected", subject)));
            else if (health?.Value is 0)
                problems.Add((Severity.Critical, Loc.T("Replica {0} is not healthy ({1})", subject, health.Text ?? "")));
            else if (health?.Value is 1)
                problems.Add((Severity.Warning, Loc.T("Replica {0} is only partly healthy ({1})", subject, health.Text ?? "")));
        }

        foreach (var s in current.Where(s => s.Sample.Metric == "ag_db_suspended" && s.Sample.Subject is not null))
        {
            var subject = s.Sample.Subject!;
            var db = DatabaseOf(subject);
            if (db is not null && ignored(db)) continue;
            var health = current.FirstOrDefault(x => x.Sample.Metric == "ag_db_health" && x.Sample.Subject == subject)?.Sample;
            if (s.Sample.Value is 1)
                problems.Add((Severity.Critical, Loc.T("Data movement is suspended for {0} ({1})", subject, s.Sample.Text ?? "")));
            else if (health?.Value is 0)
                problems.Add((Severity.Critical, Loc.T("Database {0} is not healthy ({1})", subject, health.Text ?? "")));
            else if (health?.Value is 1)
                problems.Add((Severity.Warning, Loc.T("Database {0} is only partly healthy ({1})", subject, health.Text ?? "")));
        }

        return Build(instance, "ag", problems, n => Loc.T("Availability group problem: {0} item(s)", n));
    }

    public static Finding? LogShipping(string instance, IReadOnlyList<StoredSample> latest, AlertThresholds t, Func<string, bool> ignored)
    {
        var current = Current(latest, LogShippingCollector);
        var problems = new List<(Severity, string)>();

        foreach (var (ageMetric, thresholdMetric, backup) in new[]
                 { ("ls_backup_age_min", "ls_backup_threshold_min", true), ("ls_restore_age_min", "ls_restore_threshold_min", false) })
        {
            foreach (var s in current.Where(s => s.Sample.Metric == ageMetric && s.Sample.Subject is not null))
            {
                var db = s.Sample.Subject!;
                if (ignored(db) || ignored(SqlLookout.Core.Collectors.LogShippingCollector.DatabaseOf(db))) continue;
                var threshold = current.FirstOrDefault(x => x.Sample.Metric == thresholdMetric && x.Sample.Subject == db)?.Sample.Value;
                if (threshold is not > 0) continue;
                if (s.Sample.Value is not { } age)
                {
                    problems.Add((Severity.Warning, backup
                        ? Loc.T("Log shipping of {0}: no log backup has been recorded yet", db)
                        : Loc.T("Log shipping of {0}: no restore has been recorded yet", db)));
                    continue;
                }
                if (age < threshold) continue;
                var critical = age >= threshold * t.LogShippingCriticalMultiplier;
                problems.Add((critical ? Severity.Critical : Severity.Warning, backup
                    ? Loc.T("Log shipping of {0}: last log backup {1:0} min ago (limit {2:0} min)", db, age, threshold)
                    : Loc.T("Log shipping of {0}: last restore {1:0} min ago (limit {2:0} min)", db, age, threshold)));
            }
        }
        return Build(instance, "logship", problems, n => Loc.T("Log shipping is late: {0} database(s)", n));
    }

    /// <summary>Express databases near the edition's size cap (data files only). ONE finding per instance, worst database sets the severity.</summary>
    public static Finding? ExpressLimit(string instance, IReadOnlyList<StoredSample> latest, AlertThresholds t, Func<string, bool> ignored)
    {
        var current = Current(latest, ExpressCollector);
        var limit = current.FirstOrDefault(s => s.Sample.Metric == "express_limit_mb")?.Sample.Value;
        if (limit is not > 0) return null;
        var problems = new List<(Severity, string)>();
        foreach (var s in current.Where(s => s.Sample.Metric == "express_data_mb" && s.Sample.Subject is not null && s.Sample.Value is not null))
        {
            var db = s.Sample.Subject!;
            if (ignored(db)) continue;
            var percent = s.Sample.Value!.Value / limit.Value * 100;
            if (percent < t.ExpressLimitWarnPercent) continue;
            problems.Add((percent >= t.ExpressLimitCriticalPercent ? Severity.Critical : Severity.Warning,
                Loc.T("{0}: {1:0} MB of {2:0} MB ({3:0}%)", db, s.Sample.Value, limit, percent)));
        }
        return Build(instance, "express", problems, n => Loc.T("Express size cap nearly reached: {0} database(s)", n));
    }

    public static Finding? Mirroring(string instance, IReadOnlyList<StoredSample> latest, Func<string, bool> ignored)
    {
        var current = Current(latest, MirroringCollector);
        var problems = new List<(Severity, string)>();

        foreach (var s in current.Where(s => s.Sample.Metric == "mirroring_state" && s.Sample.Subject is not null))
        {
            var db = s.Sample.Subject!;
            if (ignored(db)) continue;
            if (s.Sample.Value is 0 or 1)
                problems.Add((Severity.Critical, Loc.T("Mirroring of {0} is {1}", db, s.Sample.Text ?? "")));
            var witness = current.FirstOrDefault(x => x.Sample.Metric == "mirroring_witness_state" && x.Sample.Subject == db)?.Sample.Value;
            if (witness is 2)
                problems.Add((Severity.Warning, Loc.T("The mirroring witness of {0} is disconnected", db)));
        }
        return Build(instance, "mirror", problems, n => Loc.T("Database mirroring problem: {0} item(s)", n));
    }
}
