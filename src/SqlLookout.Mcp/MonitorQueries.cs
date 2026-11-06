using System.Text.Json;
using System.Text.Json.Serialization;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Mcp;

/// <summary>
/// What the MCP tools answer, built from a store that is opened read-only. Kept apart from the MCP attributes so the
/// answers can be tested without a protocol round trip. Everything here is data read from the monitor's history:
/// database, job and login names in it come from the monitored servers and are untrusted text.
/// </summary>
public sealed class MonitorQueries(IHistoryStore store)
{
    public const int MaxMetricsPerCollector = 200;
    public const int MaxPoints = 500;
    public const int MaxHours = 24 * 30;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    private static string Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");

    public async Task<object> ListInstancesAsync(CancellationToken ct, InstanceScope? scope = null)
    {
        var alerts = await store.GetActiveAlertsAsync(ct);
        var rows = new List<object>();
        foreach (var name in await VisibleInstancesAsync(scope, ct))
        {
            var latest = await store.GetLatestAsync(name, ct);
            var reach = await store.GetRecentRunsAsync(name, CollectorCatalog.Connectivity, 1, ct);
            var mine = alerts.Where(a => string.Equals(a.Instance, name, StringComparison.OrdinalIgnoreCase)).ToList();
            rows.Add(new
            {
                instance = name,
                reachable = reach.Count == 0 ? (bool?)null : reach[0].Status == CollectionStatus.Ok,
                lastDataUtc = latest.Count == 0 ? null : Utc(latest.Max(s => s.CollectedUtc)),
                activeCritical = mine.Count(a => a.Severity == "Critical"),
                activeWarning = mine.Count(a => a.Severity == "Warning"),
            });
        }
        return new { instances = rows };
    }

    public async Task<object> GetProblemsAsync(string? instance, CancellationToken ct, InstanceScope? scope = null)
    {
        var only = string.IsNullOrWhiteSpace(instance) ? null : await RequireInstanceAsync(instance, scope, ct);
        var visible = only is null ? (scope is null || scope.IsAll ? null : await VisibleInstancesAsync(scope, ct)) : [only];
        var alerts = (await store.GetActiveAlertsAsync(ct))
            .Where(a => visible is null || visible.Contains(a.Instance, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Severity == "Critical").ThenBy(a => a.Instance).ThenBy(a => a.Title)
            .Select(a => new { instance = a.Instance, severity = a.Severity, title = a.Title, detail = a.Detail, sinceUtc = Utc(a.FirstUtc) })
            .ToList();
        return new { count = alerts.Count, problems = alerts };
    }

    public async Task<object> GetOverviewAsync(string instance, CancellationToken ct, InstanceScope? scope = null)
    {
        instance = await RequireInstanceAsync(instance, scope, ct);
        var latest = await store.GetLatestAsync(instance, ct);
        var collectors = new List<object>();
        foreach (var group in latest.GroupBy(s => s.Collector).OrderBy(g => g.Key))
        {
            // A subject that stopped being reported (a dropped database, a wait type that left the top ten) keeps its
            // last row forever; only rows from the collector's newest run describe the present.
            var newest = group.Max(s => s.CollectedUtc);
            var current = group.Where(s => s.CollectedUtc == newest).ToList();
            var run = (await store.GetRecentRunsAsync(instance, group.Key, 1, ct)).FirstOrDefault();
            collectors.Add(new
            {
                collector = group.Key,
                lastRunUtc = Utc(newest),
                lastStatus = run?.Status.ToString(),
                lastMessage = run?.Message,
                truncated = current.Count > MaxMetricsPerCollector ? true : (bool?)null,
                metrics = current.Take(MaxMetricsPerCollector)
                    .Select(s => new { metric = s.Sample.Metric, subject = s.Sample.Subject, value = s.Sample.Value, text = s.Sample.Text })
                    .ToList(),
            });
        }
        return new { instance, collectors };
    }

    public async Task<object> GetMetricHistoryAsync(string instance, string metric, string? subject, double hours, int maxPoints,
        DateTime nowUtc, CancellationToken ct, InstanceScope? scope = null)
    {
        instance = await RequireInstanceAsync(instance, scope, ct);
        if (string.IsNullOrWhiteSpace(metric)) throw new ArgumentException("metric is required (see get_overview for the names).");
        if (!(hours > 0) || hours > MaxHours) throw new ArgumentException($"hours must be above 0 and at most {MaxHours}.");
        if (maxPoints is < 2 or > MaxPoints) throw new ArgumentException($"maxPoints must be between 2 and {MaxPoints}.");

        var series = await store.GetSeriesAsync(instance, metric, string.IsNullOrEmpty(subject) ? null : subject, nowUtc.AddHours(-hours), ct);
        if (series.Count == 0)
            return new { instance, metric, subject, hours, points = 0, note = "No samples for that metric and subject in the period. Check the names with get_overview." };

        var shown = Downsample(series, maxPoints);
        return new
        {
            instance, metric, subject, hours,
            points = series.Count,
            min = series.Min(p => p.Value),
            max = series.Max(p => p.Value),
            average = Math.Round(series.Average(p => p.Value), 3),
            latest = series[^1].Value,
            shown = shown.Count,
            samples = shown.Select(p => new { utc = Utc(p.Utc), value = p.Value }).ToList(),
        };
    }

    /// <summary>Evenly spaced picks that always keep the first and the last point.</summary>
    public static List<(DateTime Utc, double Value)> Downsample(IReadOnlyList<(DateTime Utc, double Value)> series, int maxPoints)
    {
        if (series.Count <= maxPoints) return series.ToList();
        var result = new List<(DateTime, double)>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
            result.Add(series[(int)Math.Round(i * (series.Count - 1) / (double)(maxPoints - 1))]);
        return result;
    }

    /// <summary>Instances the caller may see, as stored in the history.</summary>
    private async Task<IReadOnlyList<string>> VisibleInstancesAsync(InstanceScope? scope, CancellationToken ct) =>
        (await store.ListInstancesAsync(ct)).Where(n => scope is null || scope.Allows(n)).ToList();

    /// <summary>
    /// The stored spelling of the name. A name that does not exist and one the caller may not see get the same answer,
    /// and the answer never lists other names, so a limited key cannot learn what else is monitored.
    /// </summary>
    internal async Task<string> RequireInstanceAsync(string instance, InstanceScope? scope, CancellationToken ct)
    {
        var known = await VisibleInstancesAsync(scope, ct);
        var match = known.FirstOrDefault(n => string.Equals(n, instance?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException(UnknownInstance);
    }

    public const string UnknownInstance = "Unknown instance or not allowed for this key. Use list_instances for the names you can use.";
}
