using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Deadlocks since the instance started (a running total; the rise between two samples is the number in that period).
/// </summary>
public sealed class DeadlockCollector : ICollector
{
    public string Name => "deadlocks";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT ISNULL(SUM(cntr_value), 0)
        FROM sys.dm_os_performance_counters
        WHERE object_name LIKE N'%:Locks%' AND counter_name = N'Number of Deadlocks/sec' AND instance_name = N'_Total';
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        var total = Convert.ToDouble(await cmd.ExecuteScalarAsync(ct));
        return CollectionResult.Ok([new Sample("deadlocks_total", null, total)]);
    }
}
