using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Log file size (MB) and percent used per database, from DBCC SQLPERF(LOGSPACE).
/// Chosen over sys.dm_db_log_space_usage because that one is 2012+ and reports one database at a time.
/// </summary>
public sealed class LogUsageCollector : ICollector
{
    public string Name => "log_usage";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DBCC SQLPERF(LOGSPACE) WITH NO_INFOMSGS;";
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Columns: Database Name, Log Size (MB), Log Space Used (%), Status. Types are real/float, read by position.
            var db = reader.GetString(0);
            samples.Add(new Sample("log_size_mb", db, Convert.ToDouble(reader.GetValue(1))));
            samples.Add(new Sample("log_used_percent", db, Convert.ToDouble(reader.GetValue(2))));
        }
        return CollectionResult.Ok(samples);
    }
}
