using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>tempdb running out of room. Judged on the collector's newest run; with no capacity reading nothing is judged.</summary>
public static class TempDbAlertRules
{
    public const string Collector = "tempdb";

    public static Finding? Evaluate(string instance, IReadOnlyList<StoredSample> latest, AlertThresholds t)
    {
        var current = HaAlertRules.Current(latest, Collector);
        double? Value(string metric) => current.FirstOrDefault(s => s.Sample.Metric == metric && s.Sample.Subject is null)?.Sample.Value;

        if (Value("tempdb_capacity_mb") is not { } capacity || capacity <= 0) return null;
        if (Value("tempdb_used_mb") is not { } used) return null;
        var percent = used / capacity * 100;
        if (percent < t.TempDbWarnPercent) return null;

        var lines = new List<string>
        {
            Loc.T("tempdb has {0:0} MB in use of the {1:0} MB it can reach ({2:0}%)", used, capacity, percent),
            Loc.T("User objects {0:0} MB, internal objects {1:0} MB, version store {2:0} MB",
                Value("tempdb_user_mb") ?? 0, Value("tempdb_internal_mb") ?? 0, Value("tempdb_version_mb") ?? 0),
        };
        foreach (var s in current.Where(s => s.Sample.Metric == "tempdb_session_mb" && s.Sample.Value is not null)
                     .OrderByDescending(s => s.Sample.Value).Take(3))
            lines.Add(Loc.T("Session {0} {1}: {2:0} MB", s.Sample.Subject ?? "", s.Sample.Text ?? "", s.Sample.Value));

        return new(AlertRules.Key(instance, "tempdb", ""), instance,
            percent >= t.TempDbCriticalPercent ? Severity.Critical : Severity.Warning,
            Loc.T("tempdb is {0:0}% full", percent), string.Join("\n", lines));
    }
}
