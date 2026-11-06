using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Average read and write latency (ms) per database and for the whole instance. The file-stats DMV only counts since
/// startup, so this reads it twice a few seconds apart and reports the difference: latency of the last few seconds,
/// not the average of weeks. Databases with no I/O in the window get no sample.
/// </summary>
public sealed class IoLatencyCollector : ICollector
{
    public string Name => "io_latency";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    /// <summary>Gap between the two readings. Tests shorten it.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(5);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT ISNULL(DB_NAME(database_id), CONVERT(nvarchar(20), database_id)),
               SUM(num_of_reads), SUM(io_stall_read_ms), SUM(num_of_writes), SUM(io_stall_write_ms)
        FROM sys.dm_io_virtual_file_stats(NULL, NULL)
        GROUP BY database_id;
        """;

    public readonly record struct Counters(long Reads, long ReadStall, long Writes, long WriteStall);

    /// <summary>
    /// Latency samples from two readings of the cumulative counters. A database whose counters went backwards was
    /// restarted or recreated in the window and is skipped; one that appeared in between has no first reading.
    /// </summary>
    public static List<Sample> Diff(IReadOnlyDictionary<string, Counters> first, IReadOnlyDictionary<string, Counters> second)
    {
        var samples = new List<Sample>();
        long tr = 0, trs = 0, tw = 0, tws = 0;
        foreach (var (db, b) in second)
        {
            if (!first.TryGetValue(db, out var a)) continue;
            var reads = b.Reads - a.Reads;
            var writes = b.Writes - a.Writes;
            var readStall = b.ReadStall - a.ReadStall;
            var writeStall = b.WriteStall - a.WriteStall;
            if (reads < 0 || writes < 0 || readStall < 0 || writeStall < 0) continue;
            tr += reads; trs += readStall; tw += writes; tws += writeStall;
            if (reads > 0) samples.Add(new Sample("io_read_latency_ms", db, Math.Round((double)readStall / reads, 1)));
            if (writes > 0) samples.Add(new Sample("io_write_latency_ms", db, Math.Round((double)writeStall / writes, 1)));
        }
        if (tr > 0) samples.Add(new Sample("io_read_latency_ms", null, Math.Round((double)trs / tr, 1)));
        if (tw > 0) samples.Add(new Sample("io_write_latency_ms", null, Math.Round((double)tws / tw, 1)));
        return samples;
    }

    private async Task<Dictionary<string, Counters>> SnapshotAsync(SqlConnection connection, CancellationToken ct)
    {
        var map = new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = new Counters(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        return map;
    }

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var first = await SnapshotAsync(connection, ct);
        await Task.Delay(Window, ct);
        var second = await SnapshotAsync(connection, ct);

        var samples = Diff(first, second);
        return CollectionResult.Ok(samples);
    }
}
