using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// State of every database. Value is sys.databases.state (0 ONLINE, 1 RESTORING, 2 RECOVERING, 3 RECOVERY_PENDING,
/// 4 SUSPECT, 5 EMERGENCY, 6 OFFLINE), Text is state_desc. Numbers are used for decisions, text only for display.
/// </summary>
public sealed class DatabaseStateCollector : ICollector
{
    public string Name => "database_state";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, state, state_desc FROM sys.databases;";
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new Sample("db_state", reader.GetString(0), Convert.ToInt32(reader.GetValue(1)), reader.GetString(2)));
        }
        return CollectionResult.Ok(samples);
    }
}
