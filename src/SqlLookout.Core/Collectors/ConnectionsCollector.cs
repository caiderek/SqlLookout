using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>User sessions connected right now, and how many of them are executing a request.</summary>
public sealed class ConnectionsCollector : ICollector
{
    public string Name => "connections";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT COUNT(*), SUM(CASE WHEN status = N'running' THEN 1 ELSE 0 END)
        FROM sys.dm_exec_sessions
        WHERE is_user_process = 1;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return CollectionResult.Ok(
        [
            new Sample("connections_user", null, reader.GetInt32(0)),
            new Sample("connections_running", null, reader.IsDBNull(1) ? 0 : reader.GetInt32(1)),
        ]);
    }
}
