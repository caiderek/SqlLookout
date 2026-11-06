using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>
/// Pure rules: latest samples in, findings out. No clock, no database, so every threshold edge is unit-testable.
/// Decisions use numeric values (state codes, minutes, bytes), never message text. The wording of titles and details
/// follows the active language (<see cref="Loc"/>); the key that identifies a problem never does.
/// </summary>
public static class AlertRules
{
    // sys.databases.state values that need a person now. RESTORING (1) and RECOVERING (2) are normal on standbys and during restores.
    private static readonly HashSet<int> BrokenStates = [3, 4, 5, 6];

    public const string RuleBackup = "backup";
    /// <summary>Unchecked = no "no backup recorded" problem for master, model and msdb.</summary>
    public const string RuleSystemDbBackup = "backup_system_db";
    public const string RuleDbcc = "dbcc";
    public const string RuleDisk = "disk";
    public const string RuleLog = "log";
    public const string RuleDatabaseState = "database_state";
    public const string RuleSuspectPages = "suspect_pages";
    public const string RuleJobs = "jobs";
    public const string RuleCpu = "cpu";
    public const string RuleBlocking = "blocking";
    public const string RuleMemory = "memory";
    public const string RuleDeadlocks = "deadlocks";
    public const string RuleConnections = "connections";
    public const string RuleIoLatency = "io_latency";
    public const string RuleAvailabilityGroups = "availability_groups";
    public const string RuleLogShipping = "log_shipping";
    public const string RuleMirroring = "mirroring";
    public const string RuleExpressLimit = "express_limit";
    public const string RuleTempDb = "tempdb";
    public const string RuleFailedLogins = "failed_logins";

    public static List<Finding> Evaluate(string instance, IReadOnlyList<StoredSample> latest, AlertThresholds t, ISet<string> disabledRules,
        IReadOnlyList<string>? ignoredDatabases = null)
    {
        bool Ignored(string db) => DatabaseFilter.IsIgnored(ignoredDatabases, db);
        var findings = new List<Finding>();
        var logHits = new List<(string Db, double Used, double SizeMb)>();
        var backupHits = new List<Finding>();
        var dbccHits = new List<Finding>();

        var states = latest.Where(s => s.Sample.Metric == "db_state" && s.Sample.Subject is not null)
            .ToDictionary(s => s.Sample.Subject!, s => (int?)s.Sample.Value, StringComparer.OrdinalIgnoreCase);
        // A database that is restoring or recovering (a standby, a restore in progress) is not expected to have backups or
        // checks. One that is offline or damaged still is: judging it keeps its alerts from clearing and re-firing every
        // time it goes offline and back.
        bool NeedsChecks(string db) => !states.TryGetValue(db, out var st) || st is not (1 or 2);

        if (!disabledRules.Contains(RuleBlocking) && BlockingFinding(instance, latest, t) is { } blocking) findings.Add(blocking);

        foreach (var s in latest)
        {
            var m = s.Sample;
            var subject = m.Subject ?? "";
            switch (m.Metric)
            {
                case "db_state" when !disabledRules.Contains(RuleDatabaseState) && !Ignored(subject) && m.Value is { } st && BrokenStates.Contains((int)st):
                    findings.Add(new(Key(instance, "dbstate", subject), instance, Severity.Critical,
                        Loc.T("Database {0} is {1}", subject, m.Text),
                        Loc.T("{0} on {1} is in state {2} (code {3}).", subject, instance, m.Text, (int)st)));
                    break;

                case "suspect_page_count" when !disabledRules.Contains(RuleSuspectPages) && !Ignored(subject) && m.Value is > 0:
                    findings.Add(new(Key(instance, "suspect", subject), instance, Severity.Critical,
                        Loc.T("Suspect pages in {0}", subject),
                        Loc.T("{0} unrepaired suspect page(s) recorded for {1}. Run DBCC CHECKDB and check the storage.", (int)m.Value, subject)));
                    break;

                case "last_full_backup_age_min" when !disabledRules.Contains(RuleBackup) && !Ignored(subject) && NeedsChecks(subject):
                    if (IsSystemDb(subject) && disabledRules.Contains(RuleSystemDbBackup)) break;
                    var backup = BackupFinding(instance, subject, m.Value, t);
                    if (backup is not null) backupHits.Add(backup);
                    break;

                case "dbcc_last_good_age_min" when !disabledRules.Contains(RuleDbcc) && !Ignored(subject) && NeedsChecks(subject):
                    var dbcc = DbccFinding(instance, subject, m, t);
                    if (dbcc is not null) dbccHits.Add(dbcc);
                    break;

                case "log_used_percent" when !disabledRules.Contains(RuleLog) && !Ignored(subject) && NeedsChecks(subject) && m.Value is { } used:
                    var sizeMb = latest.FirstOrDefault(x => x.Sample.Metric == "log_size_mb" && x.Sample.Subject == m.Subject)?.Sample.Value ?? 0;
                    if (sizeMb < t.LogMinSizeMb) break;
                    if (used > t.LogWarnPercent) logHits.Add((subject, used, sizeMb));
                    break;

                case "disk_free_bytes" when !disabledRules.Contains(RuleDisk) && m.Value is { } free:
                    var total = latest.FirstOrDefault(x => x.Sample.Metric == "disk_total_bytes" && x.Sample.Subject == m.Subject)?.Sample.Value;
                    var disk = DiskSpaceFinding(instance, subject, free, total, t);
                    if (disk is not null) findings.Add(disk);
                    break;

                case "job_last_outcome" when !disabledRules.Contains(RuleJobs) && m.Value is 0:
                    findings.Add(new(Key(instance, "jobfail", subject), instance, Severity.Critical,
                        Loc.T("Agent job {0} failed", subject),
                        Loc.T("The last run of job \"{0}\" on {1} failed.", subject, instance)));
                    break;

                case "job_running_min" when !disabledRules.Contains(RuleJobs) && m.Value is { } running:
                    var avg = latest.FirstOrDefault(x => x.Sample.Metric == "job_avg_duration_min" && x.Sample.Subject == m.Subject)?.Sample.Value;
                    if (avg is > 0 && running >= t.JobRunningMinMinutes && running > avg * t.JobRunningMultiplier)
                        findings.Add(new(Key(instance, "jobslow", subject), instance, Severity.Warning,
                            Loc.T("Agent job {0} is running long", subject),
                            Loc.T("Running {0:0} min; its successful runs average {1:0.#} min.", running, avg)));
                    break;
            }
        }
        if (LogFinding(instance, logHits, t) is { } log) findings.Add(log);
        if (!disabledRules.Contains(RuleAvailabilityGroups) && HaAlertRules.AvailabilityGroups(instance, latest, Ignored) is { } ag) findings.Add(ag);
        if (!disabledRules.Contains(RuleLogShipping) && HaAlertRules.LogShipping(instance, latest, t, Ignored) is { } logShipping) findings.Add(logShipping);
        if (!disabledRules.Contains(RuleMirroring) && HaAlertRules.Mirroring(instance, latest, Ignored) is { } mirroring) findings.Add(mirroring);
        if (!disabledRules.Contains(RuleTempDb) && TempDbAlertRules.Evaluate(instance, latest, t) is { } tempdb) findings.Add(tempdb);
        if (!disabledRules.Contains(RuleFailedLogins) && FailedLoginAlertRules.Evaluate(instance, latest, t) is { } failedLogins) findings.Add(failedLogins);
        if (!disabledRules.Contains(RuleExpressLimit) && HaAlertRules.ExpressLimit(instance, latest, t, Ignored) is { } express) findings.Add(express);
        if (GroupPerDatabase(instance, "backup", backupHits, n => Loc.T("Full backups are overdue: {0} database(s)", n)) is { } backups) findings.Add(backups);
        if (GroupPerDatabase(instance, "dbcc", dbccHits, n => Loc.T("DBCC CHECKDB is overdue: {0} database(s)", n)) is { } checks) findings.Add(checks);
        return findings;
    }

    /// <summary>
    /// All databases of one instance whose log is over the limit become ONE finding, so an instance with a hundred databases
    /// sends one problem, not a hundred. The worst database sets the severity; the list is cut at 20 names.
    /// </summary>
    public static Finding? LogFinding(string instance, IReadOnlyList<(string Db, double Used, double SizeMb)> hits, AlertThresholds t)
    {
        if (hits.Count == 0) return null;
        var ordered = hits.OrderByDescending(h => h.Used).ToList();
        var critical = ordered[0].Used > t.LogCriticalPercent;
        var list = string.Join("\n", ordered.Take(20).Select(h => $"{h.Db} {h.Used:0}% ({h.SizeMb:0} MB)"));
        if (ordered.Count > 20) list += "\n" + Loc.T("and {0} more", ordered.Count - 20);
        return new(Key(instance, "log", ""), instance, critical ? Severity.Critical : Severity.Warning,
            Loc.T("Log usage is high: {0} database(s)", ordered.Count),
            Loc.T("Warning above {0:0}%, critical above {1:0}%. Highest first:", t.LogWarnPercent, t.LogCriticalPercent) + "\n" + list);
    }

    /// <summary>
    /// Per-database findings of one kind become ONE finding per instance, so 100 databases send one problem, not 100. The key
    /// is the same whether one or many databases are affected (no clear-and-fire when the count changes); a single database
    /// keeps its own wording. Worst severity wins; the list shows critical first and stops at 20 names.
    /// </summary>
    public static Finding? GroupPerDatabase(string instance, string kind, IReadOnlyList<Finding> hits, Func<int, string> title)
    {
        if (hits.Count == 0) return null;
        var key = Key(instance, kind, "");
        if (hits.Count == 1) return hits[0] with { Key = key };
        var ordered = hits.OrderByDescending(h => h.Severity).ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var list = string.Join("\n", ordered.Take(20).Select(h => h.Title));
        if (ordered.Count > 20) list += "\n" + Loc.T("and {0} more", ordered.Count - 20);
        return new(key, instance, ordered[0].Severity, title(ordered.Count), list);
    }

    public static bool IsSystemDb(string db) =>
        db.Equals("master", StringComparison.OrdinalIgnoreCase) || db.Equals("model", StringComparison.OrdinalIgnoreCase) || db.Equals("msdb", StringComparison.OrdinalIgnoreCase);

    public static Finding? BackupFinding(string instance, string db, double? ageMinutes, AlertThresholds t)
    {
        // Databases that cannot be backed up like normal ones.
        if (db.Equals("tempdb", StringComparison.OrdinalIgnoreCase)) return null;
        if (ageMinutes is null)
            return new(Key(instance, "backup", db), instance, Severity.Critical,
                Loc.T("No full backup of {0}", db), Loc.T("No full backup of {0} is recorded in msdb.", db));
        var hours = ageMinutes.Value / 60.0;
        if (hours > t.FullBackupCriticalHours)
            return new(Key(instance, "backup", db), instance, Severity.Critical,
                Loc.T("Full backup of {0} is {1:0.#} h old", db, hours),
                Loc.T("Last full backup {0:0.#} h ago (critical above {1:0} h).", hours, t.FullBackupCriticalHours));
        if (hours > t.FullBackupWarnHours)
            return new(Key(instance, "backup", db), instance, Severity.Warning,
                Loc.T("Full backup of {0} is {1:0.#} h old", db, hours),
                Loc.T("Last full backup {0:0.#} h ago (warning above {1:0} h).", hours, t.FullBackupWarnHours));
        return null;
    }

    public static Finding? DbccFinding(string instance, string db, Core.Sample sample, AlertThresholds t)
    {
        if (sample.Value is null)
        {
            // "unknown" means this version/login cannot tell: no alert. "never" is worth a nudge, not a page.
            return sample.Text == "never"
                ? new(Key(instance, "dbcc", db), instance, Severity.Warning,
                    Loc.T("{0} has never had a good DBCC CHECKDB", db), Loc.T("The engine records no successful CHECKDB for {0}.", db))
                : null;
        }
        var days = sample.Value.Value / 1440.0;
        if (days > t.DbccCriticalDays)
            return new(Key(instance, "dbcc", db), instance, Severity.Critical,
                Loc.T("DBCC CHECKDB of {0} is {1:0.#} days old", db, days),
                Loc.T("Last good CHECKDB {0:0.#} days ago (critical above {1:0}).", days, t.DbccCriticalDays));
        if (days > t.DbccWarnDays)
            return new(Key(instance, "dbcc", db), instance, Severity.Warning,
                Loc.T("DBCC CHECKDB of {0} is {1:0.#} days old", db, days),
                Loc.T("Last good CHECKDB {0:0.#} days ago (warning above {1:0}).", days, t.DbccWarnDays));
        return null;
    }

    public static Finding? DiskSpaceFinding(string instance, string volume, double freeBytes, double? totalBytes, AlertThresholds t)
    {
        const double Gb = 1024.0 * 1024 * 1024;
        var freeGb = freeBytes / Gb;
        var pct = totalBytes is > 0 ? freeBytes / totalBytes.Value * 100 : (double?)null;
        var detail = pct is null
            ? Loc.T("{0:0.#} GB free on {1}.", freeGb, volume)
            : Loc.T("{0:0.#} GB free ({1:0.#}%) on {2}.", freeGb, pct, volume);

        if (freeGb < t.DiskCriticalFreeGb || pct < t.DiskCriticalFreePercent)
            return new(Key(instance, "disk", volume), instance, Severity.Critical, Loc.T("Disk {0} is almost full", volume), detail);
        if (freeGb < t.DiskWarnFreeGb || pct < t.DiskWarnFreePercent)
            return new(Key(instance, "disk", volume), instance, Severity.Warning, Loc.T("Disk {0} is running low", volume), detail);
        return null;
    }

    /// <summary>
    /// Predicts when a volume fills up from a least-squares line through recent free-space samples.
    /// Needs at least 4 points over 20 minutes so one noisy reading cannot trigger it.
    /// </summary>
    public static Finding? DiskFallFinding(string instance, string volume, IReadOnlyList<(DateTime Utc, double Value)> series, AlertThresholds t)
    {
        if (series.Count < 4 || (series[^1].Utc - series[0].Utc) < TimeSpan.FromMinutes(20)) return null;

        var t0 = series[0].Utc;
        var xs = series.Select(p => (p.Utc - t0).TotalHours).ToArray();
        var ys = series.Select(p => p.Value).ToArray();
        var xm = xs.Average();
        var ym = ys.Average();
        var denom = xs.Sum(x => (x - xm) * (x - xm));
        if (denom <= 0) return null;
        var slope = xs.Zip(ys, (x, y) => (x - xm) * (y - ym)).Sum() / denom; // bytes per hour
        if (slope >= 0) return null;

        var freeNow = ys[^1];
        var hoursLeft = freeNow / -slope;
        const double Gb = 1024.0 * 1024 * 1024;
        var detail = Loc.T("Free space on {0} is falling {1:0.##} GB/h; at this rate {2:0.#} GB lasts {3:0.#} h.", volume, -slope / Gb, freeNow / Gb, hoursLeft);

        if (hoursLeft <= t.DiskFallCriticalHours)
            return new(Key(instance, "diskfall", volume), instance, Severity.Critical, Loc.T("Disk {0} will be full in about {1:0.#} h", volume, hoursLeft), detail);
        if (hoursLeft <= t.DiskFallWarnHours)
            return new(Key(instance, "diskfall", volume), instance, Severity.Warning, Loc.T("Disk {0} will be full in about {1:0.#} h", volume, hoursLeft), detail);
        return null;
    }

    /// <summary>
    /// Machine CPU at or above the limit for the whole sustained period. The samples must cover most of the period
    /// (so a monitor that just started cannot fire on one reading) and every one of them must be at or above the limit.
    /// </summary>
    public static Finding? CpuFinding(string instance, IReadOnlyList<(DateTime Utc, double Value)> series, AlertThresholds t)
    {
        if (series.Count < 3) return null;
        if ((series[^1].Utc - series[0].Utc).TotalMinutes < t.CpuSustainedMinutes * 0.8) return null;
        if (series.Any(p => p.Value < t.CpuWarnPercent)) return null;
        var avg = series.Average(p => p.Value);
        return new(Key(instance, "cpu", ""), instance, Severity.Warning,
            Loc.T("CPU is {0:0}%", avg),
            Loc.T("CPU stayed at {0:0}% or more for {1:0} minutes (average {2:0.#}%).", t.CpuWarnPercent, t.CpuSustainedMinutes, avg));
    }

    /// <summary>
    /// A session that has been blocked for a long time, judged on the newest reading: the longest current wait against the
    /// two limits. The head blocker text comes from the monitored server, so it is shown but never decided on.
    /// </summary>
    public static Finding? BlockingFinding(string instance, IReadOnlyList<StoredSample> latest, AlertThresholds t)
    {
        double? Value(string metric) => latest.FirstOrDefault(x => x.Sample.Metric == metric)?.Sample.Value;
        if (Value("blocking_longest_wait_sec") is not { } wait || wait < t.BlockingWarnSeconds) return null;
        var blocked = (int)(Value("blocked_sessions") ?? 0);
        var heads = latest.FirstOrDefault(x => x.Sample.Metric == "blocking_head_blockers")?.Sample.Text;
        var detail = Loc.T("{0} session(s) are blocked; the longest has waited {1:0} s.", blocked, wait);
        if (!string.IsNullOrWhiteSpace(heads)) detail += " " + Loc.T("Head blocker(s): {0}.", heads);
        return new(Key(instance, "blocking", ""), instance, wait >= t.BlockingCriticalSeconds ? Severity.Critical : Severity.Warning,
            Loc.T("Blocking detected"), detail);
    }

    /// <summary>
    /// Page life expectancy below the limit for the whole sustained period. The readings must cover most of the period
    /// (a monitor that just started, or an instance that just restarted and is still filling its cache, cannot fire on
    /// a few readings) and every one of them must be below the limit.
    /// </summary>
    public static Finding? PageLifeExpectancyFinding(string instance, IReadOnlyList<(DateTime Utc, double Value)> series, AlertThresholds t)
    {
        if (series.Count < 3) return null;
        if ((series[^1].Utc - series[0].Utc).TotalMinutes < t.MemoryPleSustainedMinutes * 0.8) return null;
        if (series.Any(p => p.Value >= t.MemoryPleWarnSeconds)) return null;
        return new(Key(instance, "ple", ""), instance, Severity.Warning,
            Loc.T("Page life expectancy is low ({0:0} s)", series[^1].Value),
            Loc.T("Page life expectancy stayed below {0:0} seconds for {1:0} minutes (lowest {2:0}, latest {3:0}).",
                t.MemoryPleWarnSeconds, t.MemoryPleSustainedMinutes, series.Min(p => p.Value), series[^1].Value));
    }

    /// <summary>At least one query waiting for a memory grant at every reading of the sustained period.</summary>
    public static Finding? MemoryGrantsFinding(string instance, IReadOnlyList<(DateTime Utc, double Value)> series, AlertThresholds t)
    {
        if (series.Count < 3) return null;
        if ((series[^1].Utc - series[0].Utc).TotalMinutes < t.MemoryGrantsPendingMinutes * 0.8) return null;
        if (series.Any(p => p.Value < 1)) return null;
        return new(Key(instance, "grants", ""), instance, Severity.Warning,
            Loc.T("Queries are waiting for memory"),
            Loc.T("At least one query has been waiting for a memory grant at every check for {0:0} minutes (now {1:0}).",
                t.MemoryGrantsPendingMinutes, series[^1].Value));
    }

    /// <summary>
    /// Deadlocks within the window, from a running total that restarts at zero when the instance restarts: a drop in the
    /// total counts the new total as the deadlocks since that restart.
    /// </summary>
    public static Finding? DeadlockFinding(string instance, IReadOnlyList<(DateTime Utc, double Value)> series, AlertThresholds t)
    {
        double count = 0;
        for (var i = 1; i < series.Count; i++)
            count += series[i].Value >= series[i - 1].Value ? series[i].Value - series[i - 1].Value : series[i].Value;
        if (count < t.DeadlockWarnCount) return null;
        return new(Key(instance, "deadlocks", ""), instance, count >= t.DeadlockCriticalCount ? Severity.Critical : Severity.Warning,
            Loc.T("Deadlocks detected"),
            Loc.T("{0:0} deadlock(s) in the last {1:0.#} hours (warning from {2}, critical from {3}).",
                count, t.DeadlockWindowHours, t.DeadlockWarnCount, t.DeadlockCriticalCount));
    }

    /// <summary>
    /// User connections at or above the limit at every reading of the sustained period (warning), or above the higher
    /// limit at every reading (critical). The readings must cover most of the period, so a monitor that just started
    /// cannot fire on one reading.
    /// </summary>
    public static Finding? ConnectionsFinding(string instance, IReadOnlyList<(DateTime Utc, double Value)> series, AlertThresholds t)
    {
        if (series.Count < 3) return null;
        if ((series[^1].Utc - series[0].Utc).TotalMinutes < t.ConnectionsSustainedMinutes * 0.8) return null;
        var lowest = series.Min(p => p.Value);
        if (lowest < t.ConnectionsWarnCount) return null;
        var critical = lowest >= t.ConnectionsCriticalCount;
        return new(Key(instance, "connections", ""), instance, critical ? Severity.Critical : Severity.Warning,
            Loc.T("{0:0} user connections", series[^1].Value),
            Loc.T("User connections stayed at {0} or more for {1:0} minutes (lowest {2:0}, latest {3:0}; critical from {4}).",
                t.ConnectionsWarnCount, t.ConnectionsSustainedMinutes, lowest, series[^1].Value, t.ConnectionsCriticalCount));
    }

    /// <summary>
    /// Average read or write latency (whole instance) at or above the limit at every reading of the sustained period.
    /// <paramref name="kind"/> is "read" or "write"; each has its own finding so one can clear while the other stays.
    /// </summary>
    public static Finding? IoLatencyFinding(string instance, string kind, IReadOnlyList<(DateTime Utc, double Value)> series, AlertThresholds t)
    {
        if (series.Count < 3) return null;
        if ((series[^1].Utc - series[0].Utc).TotalMinutes < t.IoLatencySustainedMinutes * 0.8) return null;
        var lowest = series.Min(p => p.Value);
        if (lowest < t.IoLatencyWarnMs) return null;
        var critical = lowest >= t.IoLatencyCriticalMs;
        var title = kind == "read"
            ? Loc.T("Disk reads are slow ({0:0} ms)", series[^1].Value)
            : Loc.T("Disk writes are slow ({0:0} ms)", series[^1].Value);
        var detail = kind == "read"
            ? Loc.T("Average read latency stayed at {0:0} ms or more for {1:0} minutes (lowest {2:0}, latest {3:0}; critical from {4:0} ms).",
                t.IoLatencyWarnMs, t.IoLatencySustainedMinutes, lowest, series[^1].Value, t.IoLatencyCriticalMs)
            : Loc.T("Average write latency stayed at {0:0} ms or more for {1:0} minutes (lowest {2:0}, latest {3:0}; critical from {4:0} ms).",
                t.IoLatencyWarnMs, t.IoLatencySustainedMinutes, lowest, series[^1].Value, t.IoLatencyCriticalMs);
        return new(Key(instance, "io" + kind, ""), instance, critical ? Severity.Critical : Severity.Warning, title, detail);
    }

    /// <summary>Consecutive failed runs, newest first. Warning at the first threshold, critical at the second.</summary>
    public static Finding? UnreachableFinding(string instance, IReadOnlyList<StoredRun> recentRuns, AlertThresholds t)
    {
        var consecutive = recentRuns.TakeWhile(r => r.Status != Core.CollectionStatus.Ok).Count();
        if (consecutive >= t.UnreachableCriticalAfter)
            return new(Key(instance, "unreachable", ""), instance, Severity.Critical,
                Loc.T("Cannot connect"), Loc.T("{0} connection attempts in a row failed. Last error: {1}", consecutive, recentRuns[0].Message));
        if (consecutive >= t.UnreachableWarnAfter)
            return new(Key(instance, "unreachable", ""), instance, Severity.Warning,
                Loc.T("No answer"), Loc.T("{0} connection attempts in a row failed. Last error: {1}", consecutive, recentRuns[0].Message));
        return null;
    }

    /// <summary>A collector that fails or is denied N times in a row is itself a problem to report (self-monitoring).</summary>
    public static Finding? CollectorFailingFinding(string instance, string collector, IReadOnlyList<StoredRun> recentRuns, AlertThresholds t)
    {
        if (recentRuns.Count < t.CollectorFailingAfter) return null;
        var window = recentRuns.Take(t.CollectorFailingAfter).ToList();
        if (window.Any(r => r.Status is not (Core.CollectionStatus.Failed or Core.CollectionStatus.NoPermission))) return null;
        return new(Key(instance, "collector", collector), instance, Severity.Warning,
            Loc.T("Check {0} keeps failing", collector),
            Loc.T("{0} runs in a row ended as {1} ({2}). Data for this check is missing.", t.CollectorFailingAfter, window[0].Status, window[0].Message));
    }

    public static string Key(string instance, string kind, string subject) => $"{instance}|{kind}|{subject}";
}
