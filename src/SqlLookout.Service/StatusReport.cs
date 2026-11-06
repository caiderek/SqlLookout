using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Service;

public sealed record CollectorState(string Name, CollectionStatus Status, DateTime LastRunUtc, string? Message, int Values);

/// <summary>What is known about one instance right now. Shared by the command line and the web dashboard.</summary>
public sealed record InstanceStatus(
    string Name,
    bool? Reachable,
    DateTime? LastConnectUtc,
    string? ConnectMessage,
    DateTime? LastDataUtc,
    IReadOnlyList<CollectorState> Collectors,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<StoredSample> Latest);

public static class StatusReport
{
    public static async Task<InstanceStatus> BuildAsync(IHistoryStore store, SqlLookoutConfig config, string instance,
        DateTime nowUtc, CancellationToken ct = default)
    {
        var latest = await store.GetLatestAsync(instance, ct);
        var reach = await store.GetRecentRunsAsync(instance, CollectorCatalog.Connectivity, 1, ct);

        var collectors = new List<CollectorState>();
        foreach (var c in CollectorCatalog.All())
        {
            var runs = await store.GetRecentRunsAsync(instance, c.Name, 1, ct);
            if (runs.Count == 0) continue;
            collectors.Add(new CollectorState(c.Name, runs[0].Status, runs[0].StartedUtc, runs[0].Message,
                latest.Count(s => s.Collector == c.Name)));
        }

        var disabled = config.Alerts.DisabledRules.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var findings = await new AlertEvaluator(store, config.Alerts.Thresholds, disabled, config.IgnoredFor, config.ThresholdsFor).EvaluateAsync(instance, nowUtc, ct);

        return new InstanceStatus(
            instance,
            reach.Count == 0 ? null : reach[0].Status == CollectionStatus.Ok,
            reach.Count == 0 ? null : reach[0].StartedUtc,
            reach.Count == 0 ? null : reach[0].Message,
            latest.Count == 0 ? null : latest.Max(s => s.CollectedUtc),
            collectors,
            findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Title).ToList(),
            latest);
    }
}
