using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Failed logins in the last <see cref="WindowMinutes"/> minutes, read from the current SQL Server error log. The count comes from
/// error 18456 (the number is the same in every language, though the words around it are translated); the names and client addresses
/// come from the "Login failed for user '...'" lines, and the client is the last "[label: address]" of the line whatever its language. Writes <c>failed_logins</c>
/// (the count, also the run marker, so it is written on every run) and <c>failed_login_user</c> (subject = login, value = count,
/// text = the clients it came from) for the busiest logins. Reading the error log needs sysadmin, or EXECUTE on xp_readerrorlog.
/// Failures are only in the log when login auditing is on for failed logins (the default).
/// </summary>
public sealed partial class FailedLoginsCollector : ICollector
{
    public const int WindowMinutes = 15;
    private const int MaxLogins = 10;

    public string Name => "failed_logins";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public string? UnsupportedReason(ServerInfo server) => null;

    // Arguments: log 0 (current), type 1 (SQL Server log), two search strings, from, to, order. Times are the server's local time.
    private const string CountSql = """
        DECLARE @to datetime, @from datetime;
        SET @to = GETDATE(); SET @from = DATEADD(MINUTE, -@minutes, @to);
        EXEC master.dbo.xp_readerrorlog 0, 1, N'18456', NULL, @from, @to, N'desc';
        """;

    private const string DetailSql = """
        DECLARE @to datetime, @from datetime;
        SET @to = GETDATE(); SET @from = DATEADD(MINUTE, -@minutes, @to);
        EXEC master.dbo.xp_readerrorlog 0, 1, N'Login failed for user', NULL, @from, @to, N'desc';
        """;

    [GeneratedRegex(@"Login failed for user '(?<login>.*?)'\.(?:.*\[[^\]:]+: (?<client>[^\]]*)\]\s*$)?", RegexOptions.Singleline)]
    private static partial Regex LoginLine();

    /// <summary>The login and client address of one "Login failed for user ..." line; null if the line is not one.</summary>
    public static (string Login, string Client)? Parse(string text)
    {
        var m = LoginLine().Match(text);
        if (!m.Success) return null;
        var client = m.Groups["client"].Success ? m.Groups["client"].Value.Trim() : "";
        return (m.Groups["login"].Value, client);
    }

    /// <summary>Failures per login, busiest first (ties by name), each with its clients ordered by how many failures they caused.</summary>
    public static List<(string Login, int Count, string Clients)> Summarize(IEnumerable<string> lines)
    {
        return lines.Select(Parse).Where(p => p is not null).Select(p => p!.Value)
            .GroupBy(p => p.Login, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Login: g.First().Login, Count: g.Count(),
                Clients: string.Join(", ", g.Where(p => p.Client.Length > 0).GroupBy(p => p.Client, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(c => c.Count()).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Take(3).Select(c => c.Key))))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> ReadAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        var texts = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = (int)Timeout.TotalSeconds;
        cmd.Parameters.AddWithValue("@minutes", WindowMinutes);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) texts.Add(r.IsDBNull(2) ? "" : r.GetString(2));
        return texts;
    }

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var count = (await ReadAsync(connection, CountSql, ct)).Count;
        var samples = new List<Sample> { new("failed_logins", null, count) };
        if (count > 0)
            foreach (var (login, n, clients) in Summarize(await ReadAsync(connection, DetailSql, ct)).Take(MaxLogins))
                samples.Add(new Sample("failed_login_user", login, n, clients.Length > 0 ? clients : null));
        return CollectionResult.Ok(samples);
    }
}
