using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>
/// Which kinds of problem are mailed (and posted to Teams). A rule that is not mailed is still checked: its problems are
/// recorded like any other, so the pages and the MCP tools show them.
/// </summary>
public static class MailChoices
{
    /// <summary>Stored as the notification time of a problem that nobody has been told about.</summary>
    public static readonly DateTime NeverMailed = DateTime.UnixEpoch;

    /// <summary>The rule a problem kind (the middle part of the alert key) belongs to. Null for kinds without a rule: those are always mailed.</summary>
    public static string? RuleOfKind(string kind) => kind switch
    {
        "dbstate" => AlertRules.RuleDatabaseState,
        "suspect" => AlertRules.RuleSuspectPages,
        "backup" => AlertRules.RuleBackup,
        "dbcc" => AlertRules.RuleDbcc,
        "disk" or "diskfall" => AlertRules.RuleDisk,
        "dbgrowth" => AlertRules.RuleDbGrowth,
        "log" => AlertRules.RuleLog,
        "jobfail" or "jobslow" => AlertRules.RuleJobs,
        "cpu" => AlertRules.RuleCpu,
        "blocking" => AlertRules.RuleBlocking,
        "ple" or "grants" => AlertRules.RuleMemory,
        "deadlocks" => AlertRules.RuleDeadlocks,
        "connections" => AlertRules.RuleConnections,
        "ioread" or "iowrite" => AlertRules.RuleIoLatency,
        "ag" => AlertRules.RuleAvailabilityGroups,
        "logship" => AlertRules.RuleLogShipping,
        "mirror" => AlertRules.RuleMirroring,
        "express" => AlertRules.RuleExpressLimit,
        "tempdb" => AlertRules.RuleTempDb,
        "failed_logins" => AlertRules.RuleFailedLogins,
        _ => null,
    };

    public static bool IsMailed(string alertKey, IReadOnlySet<string>? noMailRules) =>
        noMailRules is null || noMailRules.Count == 0
        || alertKey.Split('|') is not [_, var kind, ..] || RuleOfKind(kind) is not { } rule || !noMailRules.Contains(rule);

    /// <summary>True when no mail ever went out for this stored problem.</summary>
    public static bool WasNeverMailed(AlertRecord alert) => alert.LastNotifiedUtc <= NeverMailed;
}
