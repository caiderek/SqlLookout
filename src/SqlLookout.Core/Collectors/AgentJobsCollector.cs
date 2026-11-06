using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// SQL Agent jobs: last outcome (0 failed, 1 succeeded, 3 canceled, 5 unknown), minutes a running job has been running,
/// and the average duration of its successful runs. Disabled jobs are skipped. Express has no Agent.
/// </summary>
public sealed class AgentJobsCollector : ICollector
{
    public string Name => "agent_jobs";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public string? UnsupportedReason(ServerInfo server) =>
        server.IsExpress ? "SQL Server Express has no SQL Server Agent" : null;

    private const string LastOutcomeSql = """
        SELECT j.name, js.last_run_outcome
        FROM msdb.dbo.sysjobs AS j
        JOIN msdb.dbo.sysjobservers AS js ON js.job_id = j.job_id
        WHERE j.enabled = 1 AND js.server_id = 0;
        """;

    private const string RunningSql = """
        SELECT j.name, DATEDIFF(MINUTE, ja.start_execution_date, GETDATE())
        FROM msdb.dbo.sysjobactivity AS ja
        JOIN msdb.dbo.sysjobs AS j ON j.job_id = ja.job_id
        WHERE ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
          AND ja.start_execution_date IS NOT NULL
          AND ja.stop_execution_date IS NULL;
        """;

    // run_duration is encoded HHMMSS as an integer; step_id 0 is the job outcome row.
    private const string AverageSql = """
        SELECT j.name,
               AVG(CAST((h.run_duration / 10000) * 3600 + ((h.run_duration / 100) % 100) * 60 + (h.run_duration % 100) AS float)) / 60.0
        FROM msdb.dbo.sysjobhistory AS h
        JOIN msdb.dbo.sysjobs AS j ON j.job_id = h.job_id
        WHERE h.step_id = 0 AND h.run_status = 1
        GROUP BY j.name;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        await ReadAsync(connection, LastOutcomeSql, ct,
            r => samples.Add(new Sample("job_last_outcome", r.GetString(0), r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1)))));
        await ReadAsync(connection, RunningSql, ct,
            r => samples.Add(new Sample("job_running_min", r.GetString(0), r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1)))));
        // A job that is not running writes an empty reading. Without it the last "running" reading of a job that has
        // since finished would stay the newest one for good, and the job would keep looking like it is running long.
        var running = samples.Where(s => s.Metric == "job_running_min").Select(s => s.Subject).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var job in samples.Where(s => s.Metric == "job_last_outcome").Select(s => s.Subject!).ToList())
            if (!running.Contains(job)) samples.Add(new Sample("job_running_min", job, null));
        await ReadAsync(connection, AverageSql, ct,
            r => samples.Add(new Sample("job_avg_duration_min", r.GetString(0), r.IsDBNull(1) ? null : Convert.ToDouble(r.GetValue(1)))));
        return CollectionResult.Ok(samples);
    }

    private async Task ReadAsync(SqlConnection connection, string sql, CancellationToken ct, Action<SqlDataReader> onRow)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) onRow(reader);
    }
}
