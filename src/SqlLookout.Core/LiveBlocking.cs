using Microsoft.Data.SqlClient;
using SqlLookout.Core.Collectors;

namespace SqlLookout.Core;

public sealed record BlockedSession(
    int SessionId, int BlockedBy, long WaitMs, string? WaitType, string? WaitResource, string Database, string Login, string Host, string Text);

/// <summary>The session at the head of a chain. It may be idle (sleeping with an open transaction), so its text is the last statement it ran.</summary>
public sealed record BlockerSession(
    int SessionId, string Login, string Host, string Program, string Status, int OpenTransactions, string Database, string LastText);

public sealed record BlockingChain(int HeadSessionId, BlockerSession? Head, IReadOnlyList<BlockedSession> Blocked, long LongestWaitMs);

/// <summary>
/// Who is blocking whom right now, read on demand like <see cref="LiveQueries"/> and never stored. Text comes from the
/// monitored server and can contain data. Needs VIEW SERVER STATE.
/// </summary>
public static class LiveBlocking
{
    public const int BlockedLimit = 200;
    public const int ChainLimit = 20;

    private const string BlockedSql = """
        SELECT TOP (200) r.session_id, r.blocking_session_id, r.wait_time, r.wait_type, r.wait_resource,
               ISNULL(DB_NAME(r.database_id), N''), ISNULL(s.login_name, N''), ISNULL(s.host_name, N''),
               SUBSTRING(t.text, (r.statement_start_offset / 2) + 1,
                   ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2) + 1)
        FROM sys.dm_exec_requests AS r
        JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
        WHERE r.blocking_session_id > 0 AND r.session_id <> @@SPID
        ORDER BY r.wait_time DESC;
        """;

    // The head blocker is often not running anything (an open transaction left by a client), so it has no row in
    // dm_exec_requests; its last statement is found through its connection.
    private const string HeadSql = """
        SELECT s.session_id, ISNULL(s.login_name, N''), ISNULL(s.host_name, N''), ISNULL(s.program_name, N''), s.status,
               (SELECT COUNT(*) FROM sys.dm_tran_session_transactions AS x WHERE x.session_id = s.session_id),
               ISNULL(DB_NAME(s.database_id), N''), t.text
        FROM sys.dm_exec_sessions AS s
        LEFT JOIN sys.dm_exec_connections AS c ON c.session_id = s.session_id
        OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS t
        WHERE s.session_id IN ({0});
        """;

    /// <summary>One chain per head blocker, longest wait first. Everything waiting behind a head, however deep, is listed under it.</summary>
    public static List<BlockingChain> BuildChains(IReadOnlyList<BlockedSession> blocked, IReadOnlyDictionary<int, BlockerSession> heads)
    {
        var map = blocked.GroupBy(b => b.SessionId).ToDictionary(g => g.Key, g => (Blocker: g.First().BlockedBy, WaitMs: g.First().WaitMs));
        var chains = new List<BlockingChain>();
        foreach (var head in BlockingCollector.HeadBlockers(map))
        {
            var behind = new List<BlockedSession>();
            var seen = new HashSet<int> { head };
            var queue = new Queue<int>([head]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var b in blocked.Where(x => x.BlockedBy == current && seen.Add(x.SessionId)))
                {
                    behind.Add(b);
                    queue.Enqueue(b.SessionId);
                }
            }
            heads.TryGetValue(head, out var info);
            chains.Add(new BlockingChain(head, info, behind.OrderByDescending(b => b.WaitMs).ToList(), behind.Count == 0 ? 0 : behind.Max(b => b.WaitMs)));
        }
        return chains.OrderByDescending(c => c.LongestWaitMs).ToList();
    }

    public static async Task<List<BlockingChain>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        var blocked = new List<BlockedSession>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = BlockedSql;
            cmd.CommandTimeout = 15;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                blocked.Add(new BlockedSession(
                    r.GetInt16(0), r.GetInt16(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : LiveQueries.Clean(r.GetString(4), 120),
                    r.GetString(5), r.GetString(6), r.GetString(7), LiveQueries.Clean(r.IsDBNull(8) ? null : r.GetString(8))));
        }
        if (blocked.Count == 0) return [];

        var headIds = BlockingCollector.HeadBlockers(blocked.GroupBy(b => b.SessionId).ToDictionary(g => g.Key, g => (g.First().BlockedBy, g.First().WaitMs)))
            .Take(ChainLimit).ToList();
        var heads = new Dictionary<int, BlockerSession>();
        if (headIds.Count > 0)
        {
            await using var cmd = connection.CreateCommand();
            // Integers read from the server, joined as numbers: nothing from a client reaches this text.
            cmd.CommandText = string.Format(HeadSql, string.Join(",", headIds));
            cmd.CommandTimeout = 15;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt16(0);
                heads[id] = new BlockerSession(id, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6),
                    LiveQueries.Clean(r.IsDBNull(7) ? null : r.GetString(7)));
            }
        }
        return BuildChains(blocked, heads).Take(ChainLimit).ToList();
    }
}
