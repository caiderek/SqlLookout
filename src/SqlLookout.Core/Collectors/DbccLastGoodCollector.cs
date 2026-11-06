using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlLookout.Core.Collectors;

/// <summary>
/// Minutes since the last successful DBCC CHECKDB, per online database.
/// Value null with Text "never" = the engine says no good check was recorded; Text "unknown" = this version or login cannot tell.
/// DATABASEPROPERTYEX('LastGoodCheckDbTime') exists only on newer builds (returns NULL elsewhere); the fallback is
/// DBCC DBINFO, which older versions allow only to sysadmin, so a denied fallback degrades to "unknown" rather than failing the run.
/// </summary>
public sealed class DbccLastGoodCollector : ICollector
{
    public string Name => "dbcc_last_good";
    public TimeSpan DefaultInterval => TimeSpan.FromHours(1);
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public string? UnsupportedReason(ServerInfo server) => null;

    private const string Sql = """
        SELECT name,
               CAST(DATABASEPROPERTYEX(name, 'LastGoodCheckDbTime') AS datetime),
               DATEDIFF(MINUTE, CAST(DATABASEPROPERTYEX(name, 'LastGoodCheckDbTime') AS datetime), GETDATE())
        FROM sys.databases
        WHERE state = 0 AND name <> N'tempdb';
        """;

    private static readonly DateTime NeverMarker = new(1900, 1, 1);

    public async Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var rows = new List<(string Db, DateTime? Last, int? AgeMinutes)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = Sql;
            cmd.CommandTimeout = (int)Timeout.TotalSeconds;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }
        }

        var samples = new List<Sample>();
        foreach (var (db, firstLast, firstAge) in rows)
        {
            var last = firstLast;
            var age = firstAge;
            if (last is null)
            {
                last = await TryDbInfoAsync(connection, db, ct);
                age = last is null || last == NeverMarker ? null : (int)(DateTime.Now - last.Value).TotalMinutes;
            }

            if (last is null) samples.Add(new Sample("dbcc_last_good_age_min", db, null, "unknown"));
            else if (last == NeverMarker) samples.Add(new Sample("dbcc_last_good_age_min", db, null, "never"));
            else samples.Add(new Sample("dbcc_last_good_age_min", db, age));
        }
        return CollectionResult.Ok(samples);
    }

    private static async Task<DateTime?> TryDbInfoAsync(SqlConnection connection, string db, CancellationToken ct)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DBCC DBINFO(N'{db.Replace("'", "''")}') WITH TABLERESULTS, NO_INFOMSGS;";
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            // Result columns: ParentObject, Object, Field, VALUE. Read by name so a column-order change cannot shift us.
            var field = reader.GetOrdinal("Field");
            var value = reader.GetOrdinal("VALUE");
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetString(field).Trim() == "dbi_dbccLastKnownGood"
                    && DateTime.TryParse(reader.GetString(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    return parsed;
                }
            }
            return null;
        }
        catch (SqlException)
        {
            return null;
        }
    }
}
