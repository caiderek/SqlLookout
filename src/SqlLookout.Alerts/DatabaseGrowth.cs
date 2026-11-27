using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>Size change of one database over a window, in MB. Data and log are the newest total of each file type.</summary>
public sealed record DatabaseGrowthRow(string Database, double DataMb, double LogMb, double DataGrownMb, double LogGrownMb, DateTime SinceUtc)
{
    public double SizeMb => DataMb + LogMb;
    public double GrownMb => DataGrownMb + LogGrownMb;
    /// <summary>Growth as a percentage of the size at the start of the window; null when that size is zero.</summary>
    public double? GrownPercent => SizeMb - GrownMb > 0 ? GrownMb / (SizeMb - GrownMb) * 100 : null;
}

public static class DatabaseGrowth
{
    public const string Collector = "file_size";
    private const string DataMetric = "db_data_mb", LogMetric = "db_log_mb";

    /// <summary>
    /// Growth of every database in the newest file size reading: that size minus the oldest one since the cutoff.
    /// A database that is no longer reported (dropped, renamed) is left out. Largest growth first.
    /// </summary>
    public static async Task<List<DatabaseGrowthRow>> ReadAsync(IHistoryStore store, string instance, IReadOnlyList<StoredSample> latest,
        DateTime sinceUtc, CancellationToken ct = default)
    {
        var sizes = latest.Where(s => s.Collector == Collector && s.Sample.Subject is not null && s.Sample.Value is not null).ToList();
        if (sizes.Count == 0) return [];
        var newest = sizes.Max(s => s.CollectedUtc);
        var rows = new List<DatabaseGrowthRow>();
        foreach (var db in sizes.Where(s => s.CollectedUtc == newest).GroupBy(s => s.Sample.Subject!, StringComparer.Ordinal))
        {
            double data = 0, log = 0, dataGrown = 0, logGrown = 0;
            DateTime? since = null;
            foreach (var current in db)
            {
                var metric = current.Sample.Metric;
                if (metric != DataMetric && metric != LogMetric) continue;
                var series = await store.GetSeriesAsync(instance, metric, db.Key, sinceUtc, ct);
                var size = current.Sample.Value!.Value;
                var grown = series.Count > 0 ? size - series[0].Value : 0;
                if (series.Count > 0 && (since is null || series[0].Utc < since)) since = series[0].Utc;
                if (metric == DataMetric) (data, dataGrown) = (size, grown);
                else (log, logGrown) = (size, grown);
            }
            if (since is { } from) rows.Add(new(db.Key, data, log, dataGrown, logGrown, from));
        }
        return rows.OrderByDescending(r => r.GrownMb).ThenBy(r => r.Database, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
