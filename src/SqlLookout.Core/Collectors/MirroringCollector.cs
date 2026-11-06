using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Database mirroring (deprecated, but still common on older estates): the state of every mirrored database
/// (0 suspended, 1 disconnected from the partner, 2 synchronizing, 3 pending failover, 4 synchronized) and, when a witness
/// is set up, the witness state (0 unknown, 1 connected, 2 disconnected). Every run writes <c>mirroring_databases</c> so the
/// newest run is always known. Databases without mirroring have no row.
/// </summary>
public sealed class MirroringCollector : ICollector
{
    public string Name => "mirroring";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    // Express can only be a witness, which has no mirrored database of its own.
    public string? UnsupportedReason(ServerInfo server) =>
        server.IsExpress ? "SQL Server Express cannot hold a mirrored database" : null;

    private const string Sql = """
        SELECT DB_NAME(database_id), mirroring_state, ISNULL(mirroring_state_desc, N''), ISNULL(mirroring_role_desc, N''),
               mirroring_witness_state
        FROM sys.database_mirroring
        WHERE mirroring_guid IS NOT NULL;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        var count = 0;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            count++;
            var db = reader.GetString(0);
            samples.Add(new Sample("mirroring_state", db, reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1)),
                $"{reader.GetString(3)} {reader.GetString(2)}".Trim()));
            if (!reader.IsDBNull(4))
                samples.Add(new Sample("mirroring_witness_state", db, Convert.ToInt32(reader.GetValue(4))));
        }
        samples.Add(new Sample("mirroring_databases", null, count));
        return CollectionResult.Ok(samples);
    }
}
