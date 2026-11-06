using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Log shipping as the msdb monitor tables record it: minutes since the last log backup (primary side) and since the last
/// restore (secondary side), with the threshold the log shipping setup itself uses. A pair whose alert is switched off in
/// the setup is skipped. A null age means the job has never run. Every run writes <c>logship_items</c> so the newest run is
/// always known. The tables hold one side per instance, or both on a monitor server.
/// </summary>
public sealed class LogShippingCollector : ICollector
{
    public string Name => "log_shipping";
    public TimeSpan DefaultInterval => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public string? UnsupportedReason(ServerInfo server) =>
        server.IsExpress ? "SQL Server Express does not support log shipping" : null;

    // The tables exist only where log shipping was ever set up; deferred name resolution plus the OBJECT_ID guard keep
    // the query from failing on an instance without them.
    private const string PrimarySql = """
        IF OBJECT_ID(N'msdb.dbo.log_shipping_monitor_primary') IS NOT NULL
            SELECT primary_database, primary_server, DATEDIFF(MINUTE, last_backup_date, GETDATE()), backup_threshold
            FROM msdb.dbo.log_shipping_monitor_primary
            WHERE ISNULL(threshold_alert_enabled, 1) = 1;
        """;

    private const string SecondarySql = """
        IF OBJECT_ID(N'msdb.dbo.log_shipping_monitor_secondary') IS NOT NULL
            SELECT secondary_database, secondary_server, DATEDIFF(MINUTE, last_restored_date, GETDATE()), restore_threshold
            FROM msdb.dbo.log_shipping_monitor_secondary
            WHERE ISNULL(threshold_alert_enabled, 1) = 1;
        """;

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var samples = new List<Sample>();
        var items = 0;
        foreach (var (sql, ageMetric, thresholdMetric) in new[]
                 { (PrimarySql, "ls_backup_age_min", "ls_backup_threshold_min"), (SecondarySql, "ls_restore_age_min", "ls_restore_threshold_min") })
        {
            var rows = await Read(connection, sql, ct);
            items += rows.Count;
            // A monitor server can hold the same database name from several servers; only then is the server added, so the age
            // and the limit of one pair never get mixed with another and the usual single-server subject stays the plain name.
            var repeated = rows.GroupBy(r => r.Database, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var subject = repeated.Contains(r.Database) ? $"{r.Database}{ServerSuffixOpen}{r.Server}{ServerSuffixClose}" : r.Database;
                samples.Add(new Sample(ageMetric, subject, r.Age));
                samples.Add(new Sample(thresholdMetric, subject, r.Threshold));
            }
        }
        samples.Add(new Sample("logship_items", null, items));
        return CollectionResult.Ok(samples);
    }

    public const string ServerSuffixOpen = " (";
    public const string ServerSuffixClose = ")";

    /// <summary>The database name without the " (server)" tail added for a repeated name; returns the input when there is none.</summary>
    public static string DatabaseOf(string subject)
    {
        var i = subject.LastIndexOf(ServerSuffixOpen, StringComparison.Ordinal);
        return i > 0 && subject.EndsWith(ServerSuffixClose, StringComparison.Ordinal) ? subject[..i] : subject;
    }

    private static async Task<List<(string Database, string Server, double? Age, double? Threshold)>> Read(SqlConnection connection, string sql, CancellationToken ct)
    {
        var rows = new List<(string, string, double?, double?)>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? null : Convert.ToDouble(reader.GetValue(2)), reader.IsDBNull(3) ? null : Convert.ToDouble(reader.GetValue(3))));
        return rows;
    }
}
