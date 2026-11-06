using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Free and total bytes per volume that holds database files.
/// sys.dm_os_volume_stats first shipped in 2008 R2 SP1 (10.50.2500); older builds report Unsupported.
/// </summary>
public sealed class DiskSpaceCollector : ICollector
{
    public string Name => "disk_space";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) =>
        server.IsAtLeast(10, 50, 2500) ? null : "sys.dm_os_volume_stats needs SQL Server 2008 R2 SP1 or later";

    // The function is called once per file, so free space can differ slightly between two files on the same volume
    // while a log or data file grows. One row per volume (the smaller free value) keeps the series consistent.
    private const string Sql = """
        SELECT vs.volume_mount_point, MAX(vs.total_bytes), MIN(vs.available_bytes)
        FROM sys.master_files AS mf
        CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs
        GROUP BY vs.volume_mount_point;
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
            var volume = reader.GetString(0);
            samples.Add(new Sample("disk_total_bytes", volume, reader.GetInt64(1)));
            samples.Add(new Sample("disk_free_bytes", volume, reader.GetInt64(2)));
        }
        return CollectionResult.Ok(samples);
    }
}
