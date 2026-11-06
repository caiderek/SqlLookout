using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>Total data-file and log-file size per database in MB (sys.master_files, 2008 and later). Growth comes from history.</summary>
public sealed class FileSizeCollector : ICollector
{
    public string Name => "file_size";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    // size is in 8 KB pages. type 0 = ROWS, 1 = LOG.
    private const string Sql = """
        SELECT DB_NAME(database_id), type, SUM(CAST(size AS bigint)) * 8 / 1024
        FROM sys.master_files
        WHERE type IN (0, 1)
        GROUP BY database_id, type;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            var metric = Convert.ToInt32(reader.GetValue(1)) == 0 ? "db_data_mb" : "db_log_mb";
            samples.Add(new Sample(metric, reader.GetString(0), Convert.ToDouble(reader.GetValue(2))));
        }
        return CollectionResult.Ok(samples);
    }
}
