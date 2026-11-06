using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Sessions waiting on a lock held by another session, the longest wait, and the head blockers (sessions that block
/// others while not blocked themselves). Negative blocking_session_id values are special markers, not sessions.
/// </summary>
public sealed class BlockingCollector : ICollector
{
    public string Name => "blocking";
    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(30);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT r.session_id, r.blocking_session_id, r.wait_time
        FROM sys.dm_exec_requests AS r
        WHERE r.blocking_session_id > 0;
        """;

    private const string HeadSql = """
        SELECT session_id, ISNULL(login_name, N''), ISNULL(host_name, N'')
        FROM sys.dm_exec_sessions
        WHERE session_id IN ({0});
        """;

    /// <summary>Blockers that are not themselves blocked, so a chain A blocks B blocks C names only A.</summary>
    public static List<int> HeadBlockers(IReadOnlyDictionary<int, (int Blocker, long WaitMs)> blocked) =>
        blocked.Values.Select(b => b.Blocker).Where(b => !blocked.ContainsKey(b)).Distinct().Order().ToList();

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var blocked = new Dictionary<int, (int Blocker, long WaitMs)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Sql;
            cmd.CommandTimeout = (int)Timeout.TotalSeconds;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                blocked[reader.GetInt16(0)] = (reader.GetInt16(1), reader.GetInt32(2));
        }

        var heads = HeadBlockers(blocked);
        string? headText = null;
        if (heads.Count > 0)
        {
            // Session ids are integers read from the server above and formatted as numbers, never text from a user.
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = string.Format(HeadSql, string.Join(",", heads));
            cmd.CommandTimeout = (int)Timeout.TotalSeconds;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var parts = new List<string>();
            while (await reader.ReadAsync(ct))
                parts.Add($"{reader.GetInt16(0)} ({reader.GetString(1)}@{reader.GetString(2)})");
            headText = string.Join(", ", parts);
        }

        return CollectionResult.Ok(
        [
            new Sample("blocked_sessions", null, blocked.Count),
            new Sample("blocking_longest_wait_sec", null, blocked.Count == 0 ? 0 : Math.Round(blocked.Values.Max(b => b.WaitMs) / 1000.0, 1)),
            new Sample("blocking_head_blockers", null, heads.Count, headText),
        ]);
    }
}
