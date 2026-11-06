using SqlLookout.Core;
using SqlLookout.Core.Collectors;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>Failed logins in the collector's window (newest run only). ONE finding per instance; the busiest logins are listed.</summary>
public static class FailedLoginAlertRules
{
    public const string Collector = "failed_logins";

    public static Finding? Evaluate(string instance, IReadOnlyList<StoredSample> latest, AlertThresholds t)
    {
        var current = HaAlertRules.Current(latest, Collector);
        if (current.FirstOrDefault(s => s.Sample.Metric == "failed_logins" && s.Sample.Subject is null)?.Sample.Value is not { } count) return null;
        if (count < t.FailedLoginWarnCount) return null;

        var lines = new List<string>
        {
            Loc.T("{0:0} failed login(s) in the last {1} minutes (warning from {2}, critical from {3})",
                count, FailedLoginsCollector.WindowMinutes, t.FailedLoginWarnCount, t.FailedLoginCriticalCount),
        };
        foreach (var s in current.Where(s => s.Sample.Metric == "failed_login_user" && s.Sample.Subject is not null && s.Sample.Value is not null)
                     .OrderByDescending(s => s.Sample.Value).ThenBy(s => s.Sample.Subject, StringComparer.OrdinalIgnoreCase).Take(5))
            lines.Add(string.IsNullOrEmpty(s.Sample.Text)
                ? Loc.T("{0}: {1:0} failure(s)", s.Sample.Subject, s.Sample.Value)
                : Loc.T("{0}: {1:0} failure(s) from {2}", s.Sample.Subject, s.Sample.Value, s.Sample.Text));

        return new(AlertRules.Key(instance, "failed_logins", ""), instance,
            count >= t.FailedLoginCriticalCount ? Severity.Critical : Severity.Warning,
            Loc.T("Many failed logins"), string.Join('\n', lines));
    }
}
