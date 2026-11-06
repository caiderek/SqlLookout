using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Count of unrepaired suspect pages per database (msdb.dbo.suspect_pages, event_type 1 = 823/824 error,
/// 2 = bad checksum, 3 = torn page). Types 4, 5, 7 mean restored, repaired or deallocated and are not counted.
/// </summary>
public sealed class SuspectPagesCollector : ICollector
{
    public string Name => "suspect_pages";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(15);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT d.name, COUNT(sp.page_id)
        FROM sys.databases AS d
        LEFT JOIN msdb.dbo.suspect_pages AS sp
               ON sp.database_id = d.database_id AND sp.event_type IN (1, 2, 3)
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
            samples.Add(new Sample("suspect_page_count", reader.GetString(0), Convert.ToInt32(reader.GetValue(1))));
        }
        return CollectionResult.Ok(samples);
    }
}
