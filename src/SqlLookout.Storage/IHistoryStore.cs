using SqlLookout.Core;

namespace SqlLookout.Storage;

public sealed record StoredSample(string Instance, string Collector, DateTime CollectedUtc, Sample Sample);

public sealed record StoredRun(string Collector, DateTime StartedUtc, CollectionStatus Status, string? Message);

/// <summary>An alert that has been raised. ResolvedUtc null = still active.</summary>
public sealed record AlertRecord(
    string Key, string Instance, string Severity, string Title, string Detail,
    DateTime FirstUtc, DateTime LastNotifiedUtc, DateTime? ResolvedUtc);

/// <summary>
/// The only way collectors, alerts, the dashboard and MCP touch history. All times are UTC.
/// Date functions, auto-increment and upsert syntax stay inside implementations so other backends can follow.
/// </summary>
public interface IHistoryStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Stores one collector run: a run row (status, duration) plus its samples, atomically.</summary>
    Task AppendAsync(string instance, string collector, DateTime collectedUtc, TimeSpan duration,
        CollectionResult result, CancellationToken ct = default);

    /// <summary>Instances that have at least one stored run, by name.</summary>
    Task<IReadOnlyList<string>> ListInstancesAsync(CancellationToken ct = default);

    /// <summary>Newest value per (collector, metric, subject) for the instance.</summary>
    Task<IReadOnlyList<StoredSample>> GetLatestAsync(string instance, CancellationToken ct = default);

    /// <summary>One metric's values for one subject since the cutoff, oldest first. Null values are left out.</summary>
    Task<IReadOnlyList<(DateTime Utc, double Value)>> GetSeriesAsync(
        string instance, string metric, string? subject, DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>Most recent runs of one collector, newest first.</summary>
    Task<IReadOnlyList<StoredRun>> GetRecentRunsAsync(string instance, string collector, int count, CancellationToken ct = default);

    Task<IReadOnlyList<AlertRecord>> GetActiveAlertsAsync(CancellationToken ct = default);
    /// <summary>Every time a problem was open that is still open or ended after the cutoff, newest first. instance null = all servers.</summary>
    Task<IReadOnlyList<AlertRecord>> GetAlertLogAsync(DateTime sinceUtc, string? instance, CancellationToken ct = default);
    Task UpsertAlertAsync(AlertRecord alert, CancellationToken ct = default);
    Task ResolveAlertAsync(string key, DateTime resolvedUtc, CancellationToken ct = default);

    /// <summary>Small key/value state (for example the day the last summary was sent).</summary>
    Task<string?> GetStateAsync(string key, CancellationToken ct = default);
    Task SetStateAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Deletes samples and runs older than the cutoff. Returns rows removed.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default);
}
