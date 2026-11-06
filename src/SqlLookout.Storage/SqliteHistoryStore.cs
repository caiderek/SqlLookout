using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SqlLookout.Core;

namespace SqlLookout.Storage;

public sealed class SqliteHistoryStore : IHistoryStore
{
    private const int SchemaVersion = 1;
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly record struct GateSlot(string Method, long WaitedTicks, long StartedTicks);

    private async Task<GateSlot> EnterAsync(CancellationToken ct, [CallerMemberName] string method = "")
    {
        var asked = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(ct);
        var got = Stopwatch.GetTimestamp();
        return new GateSlot(method, got - asked, got);
    }

    private void Leave(GateSlot slot)
    {
        StoreTrace.Current?.Add(slot.Method, slot.WaitedTicks, Stopwatch.GetTimestamp() - slot.StartedTicks);
        _gate.Release();
    }

    private readonly bool _readOnly;

    // Which (instance, collector, metric, subject) combinations exist. Kept in its own small table so "the newest value
    // of everything" does not have to scan every stored sample to find the combinations.
    private readonly HashSet<(string Instance, string Collector, string Metric, string? Subject)> _knownKeys = [];
    private bool _hasKeyTable;

    /// <summary>
    /// With <paramref name="readOnly"/> the file is opened in SQLite's read-only mode: nothing is created or migrated, and
    /// any write fails inside SQLite. Used by the MCP server, which must never change the monitor's data.
    /// </summary>
    public SqliteHistoryStore(string dataSource, bool readOnly = false)
    {
        _readOnly = readOnly;
        _connection = new SqliteConnection(readOnly ? $"Data Source={dataSource};Mode=ReadOnly" : $"Data Source={dataSource}");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _connection.OpenAsync(ct);
        if (_readOnly)
        {
            // No DDL and no PRAGMA that writes. A file that was never set up by SqlLookout has no schema version.
            await using var check = _connection.CreateCommand();
            check.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) != SchemaVersion)
                throw new InvalidOperationException("This is not a SqlLookout history file of a version this program can read.");
            // A file last written by an earlier build has no key table; those are read the slow way.
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SampleKeys';";
            _hasKeyTable = Convert.ToInt32(await check.ExecuteScalarAsync(ct)) > 0;
            return;
        }
        await ExecuteAsync("PRAGMA journal_mode=WAL;", ct);
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS Runs (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Instance     TEXT NOT NULL,
                Collector    TEXT NOT NULL,
                StartedUtc   TEXT NOT NULL,
                DurationMs   INTEGER NOT NULL,
                Status       TEXT NOT NULL,
                Message      TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS Samples (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId        INTEGER NOT NULL REFERENCES Runs(Id) ON DELETE CASCADE,
                Instance     TEXT NOT NULL,
                Collector    TEXT NOT NULL,
                CollectedUtc TEXT NOT NULL,
                Metric       TEXT NOT NULL,
                Subject      TEXT NULL,
                Value        REAL NULL,
                Text         TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Samples_Lookup ON Samples (Instance, Collector, Metric, Subject, CollectedUtc);
            CREATE INDEX IF NOT EXISTS IX_Samples_Series ON Samples (Instance, Metric, Subject, CollectedUtc);
            CREATE INDEX IF NOT EXISTS IX_Samples_Time ON Samples (CollectedUtc);
            CREATE INDEX IF NOT EXISTS IX_Runs_Time ON Runs (StartedUtc);
            CREATE INDEX IF NOT EXISTS IX_Runs_Lookup ON Runs (Instance, Collector, Id);
            CREATE TABLE IF NOT EXISTS SampleKeys (
                Instance   TEXT NOT NULL,
                Collector  TEXT NOT NULL,
                Metric     TEXT NOT NULL,
                Subject    TEXT NOT NULL,
                HasSubject INTEGER NOT NULL,
                PRIMARY KEY (Instance, Collector, Metric, Subject, HasSubject)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS Alerts (
                Key             TEXT PRIMARY KEY,
                Instance        TEXT NOT NULL,
                Severity        TEXT NOT NULL,
                Title           TEXT NOT NULL,
                Detail          TEXT NOT NULL,
                FirstUtc        TEXT NOT NULL,
                LastNotifiedUtc TEXT NOT NULL,
                ResolvedUtc     TEXT NULL
            );
            -- One row per time a problem was open (Alerts keeps only the latest per key). Purged with the samples.
            CREATE TABLE IF NOT EXISTS AlertLog (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Key         TEXT NOT NULL,
                Instance    TEXT NOT NULL,
                Severity    TEXT NOT NULL,
                Title       TEXT NOT NULL,
                Detail      TEXT NOT NULL,
                FirstUtc    TEXT NOT NULL,
                ResolvedUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AlertLog_Key ON AlertLog (Key, ResolvedUtc);
            CREATE INDEX IF NOT EXISTS IX_AlertLog_Time ON AlertLog (FirstUtc);
            CREATE TABLE IF NOT EXISTS State (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            -- Not written by this version; kept so existing data files keep the same schema.
            CREATE TABLE IF NOT EXISTS Events (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                TimeUtc    TEXT NOT NULL,
                Instance   TEXT NULL,
                Source     TEXT NOT NULL,
                Type       TEXT NOT NULL,
                Detail     TEXT NULL
            );
            """, ct);
        await ExecuteAsync("PRAGMA foreign_keys=ON;", ct);
        await ExecuteAsync($"PRAGMA user_version={SchemaVersion};", ct);

        // A data file made before the key table existed: fill it once from what is stored.
        await using (var empty = _connection.CreateCommand())
        {
            empty.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM SampleKeys);";
            if ((long)(await empty.ExecuteScalarAsync(ct))! == 1)
                await ExecuteAsync("""
                    INSERT OR IGNORE INTO SampleKeys (Instance, Collector, Metric, Subject, HasSubject)
                    SELECT DISTINCT Instance, Collector, Metric, COALESCE(Subject, ''), Subject IS NOT NULL FROM Samples;
                    """, ct);
        }
        _hasKeyTable = true;
    }

    public async Task AppendAsync(string instance, string collector, DateTime collectedUtc, TimeSpan duration,
        CollectionResult result, CancellationToken ct = default)
    {
        var when = FormatTime(collectedUtc);
        var slot = await EnterAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);

            await using var runCmd = _connection.CreateCommand();
            runCmd.Transaction = tx;
            runCmd.CommandText = """
                INSERT INTO Runs (Instance, Collector, StartedUtc, DurationMs, Status, Message)
                VALUES ($i, $c, $t, $d, $s, $m);
                SELECT last_insert_rowid();
                """;
            runCmd.Parameters.AddWithValue("$i", instance);
            runCmd.Parameters.AddWithValue("$c", collector);
            runCmd.Parameters.AddWithValue("$t", when);
            runCmd.Parameters.AddWithValue("$d", (long)duration.TotalMilliseconds);
            runCmd.Parameters.AddWithValue("$s", result.Status.ToString());
            runCmd.Parameters.AddWithValue("$m", (object?)result.Message ?? DBNull.Value);
            var runId = (long)(await runCmd.ExecuteScalarAsync(ct))!;

            foreach (var s in result.Samples)
            {
                if (_knownKeys.Add((instance, collector, s.Metric, s.Subject)))
                {
                    await using var keyCmd = _connection.CreateCommand();
                    keyCmd.Transaction = tx;
                    keyCmd.CommandText = """
                        INSERT OR IGNORE INTO SampleKeys (Instance, Collector, Metric, Subject, HasSubject)
                        VALUES ($i, $c, $m, $s, $h);
                        """;
                    keyCmd.Parameters.AddWithValue("$i", instance);
                    keyCmd.Parameters.AddWithValue("$c", collector);
                    keyCmd.Parameters.AddWithValue("$m", s.Metric);
                    keyCmd.Parameters.AddWithValue("$s", s.Subject ?? "");
                    keyCmd.Parameters.AddWithValue("$h", s.Subject is null ? 0 : 1);
                    await keyCmd.ExecuteNonQueryAsync(ct);
                }

                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO Samples (RunId, Instance, Collector, CollectedUtc, Metric, Subject, Value, Text)
                    VALUES ($r, $i, $c, $t, $m, $s, $v, $x);
                    """;
                cmd.Parameters.AddWithValue("$r", runId);
                cmd.Parameters.AddWithValue("$i", instance);
                cmd.Parameters.AddWithValue("$c", collector);
                cmd.Parameters.AddWithValue("$t", when);
                cmd.Parameters.AddWithValue("$m", s.Metric);
                cmd.Parameters.AddWithValue("$s", (object?)s.Subject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$v", (object?)s.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$x", (object?)s.Text ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            Leave(slot);
        }
    }

    /// <summary>Instances that have at least one stored run, by name.</summary>
    public async Task<IReadOnlyList<string>> ListInstancesAsync(CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Instance FROM Runs ORDER BY Instance;";
            var list = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
            return list;
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<IReadOnlyList<StoredSample>> GetLatestAsync(string instance, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            // Newest row per key, in two index-only steps: list the keys (a covering-index scan), then take the newest
            // row of each key (one index seek). Asking "is this row the newest of its key?" once per stored row
            // takes minutes once an instance has weeks of history.
            var keys = new List<(string Collector, string Metric, string? Subject)>();
            await using (var keyCmd = _connection.CreateCommand())
            {
                keyCmd.CommandText = _hasKeyTable
                    ? "SELECT Collector, Metric, CASE WHEN HasSubject = 1 THEN Subject END FROM SampleKeys WHERE Instance = $i ORDER BY Collector, Metric, HasSubject, Subject;"
                    : "SELECT DISTINCT Collector, Metric, Subject FROM Samples WHERE Instance = $i ORDER BY Collector, Metric, Subject;";
                keyCmd.Parameters.AddWithValue("$i", instance);
                await using var keyReader = await keyCmd.ExecuteReaderAsync(ct);
                while (await keyReader.ReadAsync(ct))
                    keys.Add((keyReader.GetString(0), keyReader.GetString(1), keyReader.IsDBNull(2) ? null : keyReader.GetString(2)));
            }

            var list = new List<StoredSample>(keys.Count);
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT CollectedUtc, Value, Text FROM Samples
                WHERE Instance = $i AND Collector = $c AND Metric = $m AND Subject IS $s
                ORDER BY CollectedUtc DESC, Id DESC LIMIT 1;
                """;
            var pInstance = cmd.Parameters.AddWithValue("$i", instance);
            var pCollector = cmd.Parameters.AddWithValue("$c", "");
            var pMetric = cmd.Parameters.AddWithValue("$m", "");
            var pSubject = cmd.Parameters.AddWithValue("$s", DBNull.Value);
            foreach (var (collector, metric, subject) in keys)
            {
                pInstance.Value = instance;
                pCollector.Value = collector;
                pMetric.Value = metric;
                pSubject.Value = (object?)subject ?? DBNull.Value;
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) continue;
                list.Add(new StoredSample(
                    instance,
                    collector,
                    ParseTime(reader.GetString(0)),
                    new Sample(
                        metric,
                        subject,
                        reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2))));
            }
            return list;
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<IReadOnlyList<(DateTime Utc, double Value)>> GetSeriesAsync(
        string instance, string metric, string? subject, DateTime sinceUtc, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT CollectedUtc, Value FROM Samples
                WHERE Instance = $i AND Metric = $m AND Subject IS $s
                  AND CollectedUtc >= $t AND Value IS NOT NULL
                ORDER BY CollectedUtc, Id;
                """;
            cmd.Parameters.AddWithValue("$i", instance);
            cmd.Parameters.AddWithValue("$m", metric);
            cmd.Parameters.AddWithValue("$s", (object?)subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", FormatTime(sinceUtc));
            var list = new List<(DateTime, double)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add((ParseTime(reader.GetString(0)), reader.GetDouble(1)));
            return list;
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<IReadOnlyList<StoredRun>> GetRecentRunsAsync(
        string instance, string collector, int count, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT StartedUtc, Status, Message FROM Runs
                WHERE Instance = $i AND Collector = $c ORDER BY Id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$i", instance);
            cmd.Parameters.AddWithValue("$c", collector);
            cmd.Parameters.AddWithValue("$n", count);
            var list = new List<StoredRun>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new StoredRun(collector, ParseTime(reader.GetString(0)),
                    Enum.Parse<CollectionStatus>(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            return list;
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<IReadOnlyList<AlertRecord>> GetActiveAlertsAsync(CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT Key, Instance, Severity, Title, Detail, FirstUtc, LastNotifiedUtc
                FROM Alerts WHERE ResolvedUtc IS NULL ORDER BY Instance, Key;
                """;
            var list = new List<AlertRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AlertRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), ParseTime(reader.GetString(5)),
                    ParseTime(reader.GetString(6)), null));
            }
            return list;
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task UpsertAlertAsync(AlertRecord alert, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Alerts (Key, Instance, Severity, Title, Detail, FirstUtc, LastNotifiedUtc, ResolvedUtc)
                VALUES ($k, $i, $s, $t, $d, $f, $n, NULL)
                ON CONFLICT(Key) DO UPDATE SET
                    Severity = excluded.Severity, Title = excluded.Title, Detail = excluded.Detail,
                    LastNotifiedUtc = excluded.LastNotifiedUtc, ResolvedUtc = NULL,
                    FirstUtc = CASE WHEN Alerts.ResolvedUtc IS NULL THEN Alerts.FirstUtc ELSE excluded.FirstUtc END;
                """;
            cmd.Parameters.AddWithValue("$k", alert.Key);
            cmd.Parameters.AddWithValue("$i", alert.Instance);
            cmd.Parameters.AddWithValue("$s", alert.Severity);
            cmd.Parameters.AddWithValue("$t", alert.Title);
            cmd.Parameters.AddWithValue("$d", alert.Detail);
            cmd.Parameters.AddWithValue("$f", FormatTime(alert.FirstUtc));
            cmd.Parameters.AddWithValue("$n", FormatTime(alert.LastNotifiedUtc));
            await cmd.ExecuteNonQueryAsync(ct);

            // The log: refresh the open row, or start one (a new problem, one that came back, or one open before the log existed).
            await using var log = _connection.CreateCommand();
            log.CommandText = "UPDATE AlertLog SET Severity = $s, Title = $t, Detail = $d WHERE Key = $k AND ResolvedUtc IS NULL;";
            log.Parameters.AddWithValue("$k", alert.Key);
            log.Parameters.AddWithValue("$s", alert.Severity);
            log.Parameters.AddWithValue("$t", alert.Title);
            log.Parameters.AddWithValue("$d", alert.Detail);
            if (await log.ExecuteNonQueryAsync(ct) == 0)
            {
                log.CommandText = """
                    INSERT INTO AlertLog (Key, Instance, Severity, Title, Detail, FirstUtc, ResolvedUtc)
                    SELECT $k, $i, $s, $t, $d, FirstUtc, NULL FROM Alerts WHERE Key = $k;
                    """;
                log.Parameters.AddWithValue("$i", alert.Instance);
                await log.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task ResolveAlertAsync(string key, DateTime resolvedUtc, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Alerts SET ResolvedUtc = $t WHERE Key = $k AND ResolvedUtc IS NULL;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$t", FormatTime(resolvedUtc));
            await cmd.ExecuteNonQueryAsync(ct);
            cmd.CommandText = "UPDATE AlertLog SET ResolvedUtc = $t WHERE Key = $k AND ResolvedUtc IS NULL;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<IReadOnlyList<AlertRecord>> GetAlertLogAsync(DateTime sinceUtc, string? instance, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            // Still open, or ended after the cutoff. Times are fixed-width text, so string comparison orders them.
            cmd.CommandText = """
                SELECT Key, Instance, Severity, Title, Detail, FirstUtc, ResolvedUtc
                FROM AlertLog
                WHERE (ResolvedUtc IS NULL OR ResolvedUtc >= $since) AND ($i IS NULL OR Instance = $i COLLATE NOCASE)
                ORDER BY FirstUtc DESC, Id DESC;
                """;
            cmd.Parameters.AddWithValue("$since", FormatTime(sinceUtc));
            cmd.Parameters.AddWithValue("$i", (object?)instance ?? DBNull.Value);
            var list = new List<AlertRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var first = ParseTime(reader.GetString(5));
                list.Add(new AlertRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), first, first, reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6))));
            }
            return list;
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM State WHERE Key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO State (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            // Samples go with their run (ON DELETE CASCADE).
            cmd.CommandText = "DELETE FROM Runs WHERE StartedUtc < $t;";
            cmd.Parameters.AddWithValue("$t", FormatTime(cutoffUtc));
            var removed = await cmd.ExecuteNonQueryAsync(ct);

            // Forget combinations that no longer have any sample (a dropped database, say).
            cmd.Parameters.Clear();
            cmd.CommandText = """
                DELETE FROM SampleKeys WHERE NOT EXISTS (
                    SELECT 1 FROM Samples s WHERE s.Instance = SampleKeys.Instance AND s.Collector = SampleKeys.Collector
                      AND s.Metric = SampleKeys.Metric AND s.Subject IS (CASE WHEN SampleKeys.HasSubject = 1 THEN SampleKeys.Subject END));
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            cmd.Parameters.Clear();
            cmd.CommandText = "DELETE FROM AlertLog WHERE ResolvedUtc IS NOT NULL AND ResolvedUtc < $t;";
            cmd.Parameters.AddWithValue("$t", FormatTime(cutoffUtc));
            await cmd.ExecuteNonQueryAsync(ct);
            _knownKeys.Clear();
            return removed;
        }
        finally
        {
            Leave(slot);
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Fixed-width UTC text sorts correctly as a string; refusing non-UTC input stops silent time-zone bugs.
    private static string FormatTime(DateTime utc) =>
        utc.Kind == DateTimeKind.Utc
            ? utc.ToString(TimeFormat, CultureInfo.InvariantCulture)
            : throw new ArgumentException("History times must be UTC.", nameof(utc));

    private static DateTime ParseTime(string text) =>
        DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
