using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// The ten wait types that cost the most time in a short window, as milliseconds of waiting per second. Reads the
/// cumulative DMV twice and reports the difference, so it shows what the server is waiting on now, not since startup.
/// Idle and housekeeping waits that do not mean a problem are left out.
/// </summary>
public sealed class WaitStatsCollector : ICollector
{
    public string Name => "wait_stats";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    /// <summary>Gap between the two readings. Tests shorten it.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(5);

    public string? UnsupportedReason(ServerInfo server) => null;

    public static readonly HashSet<string> Benign = new(StringComparer.OrdinalIgnoreCase)
    {
        "SLEEP_TASK", "SLEEP_SYSTEMTASK", "SLEEP_BPOOL_FLUSH", "SLEEP_DBSTARTUP", "SLEEP_DCOMSTARTUP", "SLEEP_MASTERDBREADY",
        "SLEEP_MASTERMDREADY", "SLEEP_MASTERUPGRADED", "SLEEP_MSDBSTARTUP", "SLEEP_TEMPDBSTARTUP", "SLEEP_WORKSPACE_ALLOCATEPAGE",
        "LAZYWRITER_SLEEP", "CHECKPOINT_QUEUE", "REQUEST_FOR_DEADLOCK_SEARCH", "XE_TIMER_EVENT", "XE_DISPATCHER_WAIT",
        "XE_DISPATCHER_JOIN", "XE_LIVE_TARGET_TVF", "XE_TIMER_MUTEX", "LOGMGR_QUEUE", "FT_IFTS_SCHEDULER_IDLE_WAIT",
        "FT_IFTSHC_MUTEX", "BROKER_TO_FLUSH", "BROKER_TASK_STOP", "BROKER_EVENTHANDLER", "BROKER_RECEIVE_WAITFOR",
        "BROKER_TRANSMITTER", "BROKER_CONNECTION_RECEIVE_TASK", "DISPATCHER_QUEUE_SEMAPHORE", "SQLTRACE_BUFFER_FLUSH",
        "SQLTRACE_INCREMENTAL_FLUSH_SLEEP", "SQLTRACE_WAIT_ENTRIES", "WAITFOR", "WAIT_FOR_RESULTS",
        "HADR_FILESTREAM_IOMGR_IOCOMPLETION", "HADR_WORK_QUEUE", "HADR_CLUSAPI_CALL", "HADR_TIMER_TASK", "HADR_LOGCAPTURE_WAIT",
        "HADR_NOTIFICATION_DEQUEUE", "DIRTY_PAGE_POLL", "ONDEMAND_TASK_QUEUE", "PARALLEL_REDO_WORKER_WAIT_WORK",
        "PREEMPTIVE_XE_GETTARGETSTATE", "PWAIT_ALL_COMPONENTS_INITIALIZED", "PWAIT_DIRECTLOGCONSUMER_GETNEXT",
        "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP", "QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP", "QDS_ASYNC_QUEUE",
        "SERVER_IDLE_CHECK", "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_FILE_BUFFER", "WAIT_XTP_OFFLINE_CKPT_NEW_LOG",
        "WAIT_XTP_CKPT_CLOSE", "CLR_AUTO_EVENT", "CLR_MANUAL_EVENT", "CLR_SEMAPHORE", "KSOURCE_WAKEUP",
        "MEMORY_ALLOCATION_EXT", "REDO_THREAD_PENDING_WORK", "UCS_SESSION_REGISTRATIONS", "VDI_CLIENT_OTHER",
        "SOS_WORK_DISPATCHER",
    };

    private const string Sql = "SELECT wait_type, wait_time_ms FROM sys.dm_os_wait_stats WHERE wait_time_ms > 0;";

    private async Task<Dictionary<string, long>> SnapshotAsync(SqlConnection connection, CancellationToken ct)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[reader.GetString(0)] = reader.GetInt64(1);
        return map;
    }

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var first = await SnapshotAsync(connection, ct);
        var started = DateTime.UtcNow;
        await Task.Delay(Window, ct);
        var second = await SnapshotAsync(connection, ct);
        var seconds = Math.Max((DateTime.UtcNow - started).TotalSeconds, 0.001);

        var top = second
            .Where(kv => !Benign.Contains(kv.Key) && first.TryGetValue(kv.Key, out var a) && kv.Value > a)
            .Select(kv => (Type: kv.Key, Ms: kv.Value - first[kv.Key]))
            .OrderByDescending(x => x.Ms)
            .Take(10)
            .Select(x => new Sample("wait_ms_per_sec", x.Type, Math.Round(x.Ms / seconds, 1)))
            .ToList();
        return CollectionResult.Ok(top);
    }
}
