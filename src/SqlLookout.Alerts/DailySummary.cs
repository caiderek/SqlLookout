using System.Text;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>
/// The once-a-day "I am alive" mail. It doubles as the check for a dead collector: if this mail stops arriving,
/// the service is down (the service cannot alert about itself).
/// </summary>
public static class DailySummary
{
    /// <param name="current">The problems found right now. When given, the summary lists these (warnings that were never mailed included) with their details; otherwise it lists the alerts that were mailed.</param>
    public static async Task<string> BuildAsync(IHistoryStore store, IReadOnlyList<string> instances, DateTime nowUtc,
        IReadOnlyList<Finding>? current = null, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        text.AppendLine(Loc.T("SqlLookout daily summary, {0:yyyy-MM-dd HH:mm} UTC", nowUtc));
        text.AppendLine();

        var active = await store.GetActiveAlertsAsync(ct);
        if (current is not null)
        {
            var firstSeen = active.ToDictionary(a => a.Key, a => a.FirstUtc);
            var mutes = (await AlertMutes.ListAsync(store, ct)).ToDictionary(m => m.Key);
            text.AppendLine(current.Count == 0 ? Loc.T("No active alerts.") : Loc.T("{0} active alert(s):", current.Count));
            foreach (var f in current.OrderByDescending(f => f.Severity).ThenBy(f => f.Instance, StringComparer.OrdinalIgnoreCase))
            {
                var tag = f.Severity == Severity.Critical ? Loc.T("CRITICAL") : Loc.T("WARNING");
                var since = firstSeen.TryGetValue(f.Key, out var first) ? "  " + Loc.T("(since {0:yyyy-MM-dd HH:mm} UTC)", first) : "";
                var muted = mutes.TryGetValue(f.Key, out var mute) && mute.Silences(f, nowUtc)
                    ? "  " + (mute.Kind == MuteKind.Acknowledged ? Loc.T("acknowledged by a person") : Loc.T("snoozed until {0:yyyy-MM-dd HH:mm} UTC", mute.UntilUtc))
                    : "";
                text.AppendLine($"  [{tag}] {f.Instance}: {f.Title}{since}{muted}");
                foreach (var line in f.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries)) text.AppendLine("      " + line);
            }
        }
        else
        {
            text.AppendLine(active.Count == 0 ? Loc.T("No active alerts.") : Loc.T("{0} active alert(s):", active.Count));
            foreach (var a in active.OrderByDescending(a => a.Severity).ThenBy(a => a.Instance))
            {
                var tag = a.Severity == nameof(Severity.Critical) ? Loc.T("CRITICAL") : Loc.T("WARNING");
                text.AppendLine($"  [{tag}] {a.Instance}: {a.Title}  " + Loc.T("(since {0:yyyy-MM-dd HH:mm} UTC)", a.FirstUtc));
            }
        }
        text.AppendLine();

        foreach (var instance in instances)
        {
            var reach = await store.GetRecentRunsAsync(instance, CollectorCatalog.Connectivity, 1, ct);
            var latest = await store.GetLatestAsync(instance, ct);
            var last = latest.Count == 0 ? (DateTime?)null : latest.Max(s => s.CollectedUtc);
            var dbCount = latest.Count(s => s.Sample.Metric == "db_state");
            var state = reach.Count == 0 ? Loc.T("no data yet") : reach[0].Status == CollectionStatus.Ok ? Loc.T("reachable") : Loc.T("UNREACHABLE");
            var lastText = last is null ? Loc.T("never") : Loc.T("{0:yyyy-MM-dd HH:mm} UTC", last);
            text.AppendLine(Loc.T("{0}: {1}, {2} database(s), last data {3}", instance, state, dbCount, lastText));
        }
        return text.ToString();
    }
}
