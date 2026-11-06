using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Page life expectancy (seconds), memory grants waiting, and SQL Server's memory use against its target, from the
/// performance counter DMV. The object name carries the instance name on named instances, so it is matched with LIKE.
/// </summary>
public sealed class MemoryCollector : ICollector
{
    public string Name => "memory";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT RTRIM(counter_name), cntr_value
        FROM sys.dm_os_performance_counters
        WHERE (object_name LIKE N'%:Buffer Manager%' AND counter_name = N'Page life expectancy')
           OR (object_name LIKE N'%:Memory Manager%'
               AND counter_name IN (N'Memory Grants Pending', N'Total Server Memory (KB)', N'Target Server Memory (KB)'));
        """;

    // SQL Server's own verdict on the machine's physical memory (high / steady / transitioning / low).
    private const string OsStateSql = "SELECT system_memory_state_desc FROM sys.dm_os_sys_memory;";

    /// <summary>1 = available memory is high, 2 = steady or transitioning, 3 = low; null for text it does not know.</summary>
    public static double? OsStateCode(string? desc) =>
        desc is null ? null
        : desc.Contains("high", StringComparison.OrdinalIgnoreCase) ? 1
        : desc.Contains("low", StringComparison.OrdinalIgnoreCase) ? 3
        : desc.Contains("steady", StringComparison.OrdinalIgnoreCase) || desc.Contains("transition", StringComparison.OrdinalIgnoreCase) ? 2
        : null;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var value = Convert.ToDouble(reader.GetValue(1));
            switch (reader.GetString(0))
            {
                case "Page life expectancy": samples.Add(new Sample("memory_ple_seconds", null, value)); break;
                case "Memory Grants Pending": samples.Add(new Sample("memory_grants_pending", null, value)); break;
                case "Total Server Memory (KB)": samples.Add(new Sample("memory_used_mb", null, Math.Round(value / 1024, 1))); break;
                case "Target Server Memory (KB)": samples.Add(new Sample("memory_target_mb", null, Math.Round(value / 1024, 1))); break;
            }
        }
        await reader.CloseAsync();
        cmd.CommandText = OsStateSql;
        var desc = await cmd.ExecuteScalarAsync(ct) as string;
        if (OsStateCode(desc) is { } code) samples.Add(new Sample("memory_os_state", null, code, desc));
        return CollectionResult.Ok(samples);
    }
}
