using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// SQL Server Express refuses to grow a database past its size cap (4 GB for 2005/2008, 10 GB from 2008 R2, 50 GB from 2025). The cap counts
/// data files only, not the log, and not tempdb. Writes <c>express_data_mb</c> per database and <c>express_limit_mb</c> on
/// every run, so the newest run is always known and a dropped database stops counting.
/// </summary>
public sealed class ExpressLimitCollector : ICollector
{
    public string Name => "express_limit";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(15);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) =>
        server.IsExpress ? null : "Only SQL Server Express has a database size cap";

    /// <summary>Version 10.0 (2008) and older cap at 4 GB; 10.50 (2008 R2) to 2022 at 10 GB; 17.0 (2025) and later at 50 GB.</summary>
    public static double LimitMb(ServerInfo server) =>
        server.IsAtLeast(17, 0) ? 50 * 1024 : server.IsAtLeast(10, 50) ? 10 * 1024 : 4 * 1024;

    // size is in 8 KB pages. type 0 = ROWS (FILESTREAM is type 2 and not counted). tempdb is exempt from the cap.
    private const string Sql = """
        SELECT DB_NAME(database_id), SUM(CAST(size AS bigint)) * 8 / 1024
        FROM sys.master_files
        WHERE type = 0 AND database_id <> 2
        GROUP BY database_id;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample> { new("express_limit_mb", null, LimitMb(server)) };
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            samples.Add(new Sample("express_data_mb", reader.GetString(0), Convert.ToDouble(reader.GetValue(1))));
        }
        return CollectionResult.Ok(samples);
    }
}
