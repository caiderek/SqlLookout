using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Minutes since the last full / differential / log backup per database (null Value = never).
/// Works on 2008 and later: msdb.dbo.backupset is the only source.
/// </summary>
public sealed class BackupAgeCollector : ICollector
{
    public string Name => "backup_age";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public string? UnsupportedReason(ServerInfo server) => null;

    // COLLATE DATABASE_DEFAULT: msdb and master can differ in collation on migrated servers.
    // GETDATE() on both sides, so the difference is correct whatever the server time zone is.
    private const string Sql = """
        SELECT d.name,
               DATEDIFF(MINUTE, MAX(CASE WHEN b.type = 'D' THEN b.backup_finish_date END), GETDATE()),
               DATEDIFF(MINUTE, MAX(CASE WHEN b.type = 'I' THEN b.backup_finish_date END), GETDATE()),
               DATEDIFF(MINUTE, MAX(CASE WHEN b.type = 'L' THEN b.backup_finish_date END), GETDATE())
        FROM sys.databases AS d
        LEFT JOIN msdb.dbo.backupset AS b
               ON b.database_name COLLATE DATABASE_DEFAULT = d.name COLLATE DATABASE_DEFAULT
        WHERE d.name <> N'tempdb'
        GROUP BY d.name;
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
            var db = reader.GetString(0);
            samples.Add(new Sample("last_full_backup_age_min", db, ReadMinutes(reader, 1)));
            samples.Add(new Sample("last_diff_backup_age_min", db, ReadMinutes(reader, 2)));
            samples.Add(new Sample("last_log_backup_age_min", db, ReadMinutes(reader, 3)));
        }
        return CollectionResult.Ok(samples);
    }

    private static double? ReadMinutes(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
