using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlLookout.Core;

public sealed record RunningQuery(
    int SessionId, string Login, string Host, string Database, string Status, long ElapsedMs,
    string? WaitType, int BlockedBy, long CpuMs, long Reads, string Text);

public sealed record HeavyQuery(
    long Executions, long TotalElapsedMs, double AvgElapsedMs, double AvgCpuMs, double AvgReads,
    DateTime? LastRunServerTime, string Database, string Text);

/// <summary>
/// A look at what a server is doing right now and which statements cost the most since they were cached. Read on demand
/// when someone opens the page, never stored. Query text can contain data and comes from the monitored server, so callers
/// show it escaped and only to signed-in users. Needs VIEW SERVER STATE; works from SQL Server 2005.
/// </summary>
public static class LiveQueries
{
    public const int MaxTextLength = 400;
    /// <summary>The read asks for one more than this so the page can say the list was cut.</summary>
    public const int RunningLimit = 50;
    public const int HeavyLimit = 15;

    // The statement being run, not the whole batch: the offsets are in bytes of UTF-16 text (-1 = to the end).
    private const string RunningSql = """
        SELECT TOP (51) r.session_id, ISNULL(s.login_name, N''), ISNULL(s.host_name, N''), ISNULL(DB_NAME(r.database_id), N''),
               r.status, r.total_elapsed_time, r.wait_type, r.blocking_session_id, r.cpu_time, r.logical_reads,
               SUBSTRING(t.text, (r.statement_start_offset / 2) + 1,
                   ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2) + 1)
        FROM sys.dm_exec_requests AS r
        JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
        WHERE s.is_user_process = 1 AND r.session_id <> @@SPID AND ISNULL(s.program_name, N'') <> N'SqlLookout'
        ORDER BY r.total_elapsed_time DESC;
        """;

    // total_elapsed_time and total_worker_time are microseconds. Statements that mention this view are left out so the
    // monitor's own look does not show up in its own list.
    // sys.dm_exec_sql_text(...).dbid is NULL for ad-hoc and prepared statements, which are most of them, so the database
    // comes from the plan attributes, looked up only for the rows that made the list.
    private const string HeavySql = """
        WITH top_stats AS (
            SELECT TOP (@top + 10) qs.execution_count, qs.total_elapsed_time, qs.total_worker_time, qs.total_logical_reads,
                   qs.last_execution_time, qs.sql_handle, qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset
            FROM sys.dm_exec_query_stats AS qs
            ORDER BY qs.total_elapsed_time DESC
        ),
        ranked AS (
            SELECT TOP (@top) t.*
            FROM top_stats AS t
            CROSS APPLY sys.dm_exec_sql_text(t.sql_handle) AS f
            WHERE f.text NOT LIKE N'%dm_exec_query_stats%'
            ORDER BY t.total_elapsed_time DESC
        )
        SELECT k.execution_count, k.total_elapsed_time / 1000, k.total_worker_time / 1000, k.total_logical_reads,
               k.last_execution_time, ISNULL(DB_NAME(COALESCE(st.dbid, pa.value)), N''),
               SUBSTRING(st.text, (k.statement_start_offset / 2) + 1,
                   ((CASE k.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE k.statement_end_offset END - k.statement_start_offset) / 2) + 1)
        FROM ranked AS k
        CROSS APPLY sys.dm_exec_sql_text(k.sql_handle) AS st
        OUTER APPLY (SELECT TOP (1) CONVERT(int, a.value) AS value FROM sys.dm_exec_plan_attributes(k.plan_handle) AS a WHERE a.attribute = N'dbid') AS pa
        ORDER BY k.total_elapsed_time DESC;
        """;

    /// <summary>One line of text: runs of whitespace become one space, and long text is cut with an ellipsis.</summary>
    public static string Clean(string? text, int max = MaxTextLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length > max) break;
        }
        return sb.Length > max ? sb.ToString(0, max) + "…" : sb.ToString();
    }

    public static string Duration(double ms) =>
        ms < 1000 ? $"{ms:0} ms" : ms < 60_000 ? $"{ms / 1000:0.#} s" : ms < 3_600_000 ? $"{ms / 60_000:0.#} min" : $"{ms / 3_600_000:0.#} h";

    public static async Task<List<RunningQuery>> ReadRunningAsync(SqlConnection connection, CancellationToken ct)
    {
        var list = new List<RunningQuery>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = RunningSql;
        cmd.CommandTimeout = 25;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RunningQuery(
                r.GetInt16(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? 0 : r.GetInt16(7), r.GetInt32(8), r.GetInt64(9),
                Clean(r.IsDBNull(10) ? null : r.GetString(10))));
        return list;
    }

    public static async Task<List<HeavyQuery>> ReadHeaviestAsync(SqlConnection connection, CancellationToken ct, int top = HeavyLimit)
    {
        var list = new List<HeavyQuery>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = HeavySql;
        cmd.Parameters.AddWithValue("@top", top);
        cmd.CommandTimeout = 25;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var runs = Math.Max(1, r.GetInt64(0));
            list.Add(new HeavyQuery(
                r.GetInt64(0), r.GetInt64(1), (double)r.GetInt64(1) / runs, (double)r.GetInt64(2) / runs, (double)r.GetInt64(3) / runs,
                r.IsDBNull(4) ? null : r.GetDateTime(4), r.GetString(5), Clean(r.IsDBNull(6) ? null : r.GetString(6))));
        }
        return list;
    }
}
