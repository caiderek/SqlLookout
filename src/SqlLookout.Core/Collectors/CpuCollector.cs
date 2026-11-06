using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// CPU use over the last minute: SQL Server's share and the machine's total (100 minus idle), from the scheduler monitor
/// ring buffer. That buffer gets one record a minute and exists on every version since 2005.
/// </summary>
public sealed class CpuCollector : ICollector
{
    public string Name => "cpu";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT TOP (1)
            rb.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int'),
            rb.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int')
        FROM (SELECT [timestamp], CONVERT(xml, record) AS record
              FROM sys.dm_os_ring_buffers
              WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' AND record LIKE N'%<SystemHealth>%') AS rb
        ORDER BY rb.[timestamp] DESC;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        // A freshly started instance may not have written its first record yet: no sample, not an error.
        if (!await reader.ReadAsync(ct)) return CollectionResult.Ok([]);
        double sql = reader.GetInt32(0);
        double idle = reader.GetInt32(1);
        return CollectionResult.Ok(
        [
            new Sample("cpu_sql_percent", null, sql),
            new Sample("cpu_total_percent", null, Math.Clamp(100 - idle, sql, 100)),
        ]);
    }
}
