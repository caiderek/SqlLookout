using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using SqlLookout.Core;

namespace SqlLookout.Storage;

/// <summary>
/// <see cref="IHistoryStore"/> in a SQL Server database that belongs to SqlLookout. Same tables and meaning as the SQLite
/// store; times are fixed-width UTC text in NVARCHAR columns so that parameters and columns have the same type and the
/// indexes can be used. Values longer than the key columns allow are cut (see the Max constants).
/// </summary>
public sealed class SqlServerHistoryStore : IHistoryStore
{
    private const int SchemaVersion = 1;
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    private const int MaxSubject = 400;
    private const int MaxKey = 450;
    private const string CaseInsensitive = "SQL_Latin1_General_CP1_CI_AS";

    private readonly string _connectionString;
    private SqlConnection _connection;
    private readonly bool _readOnly;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<(string Instance, string Collector, string Metric, string? Subject)> _knownKeys = [];

    private readonly record struct GateSlot(string Method, long WaitedTicks, long StartedTicks);

    /// <summary>Runs deleted per statement when purging, so each transaction and the log stay small.</summary>
    public int PurgeBatchSize { get; init; } = 5000;

    /// <param name="readOnly">Nothing is created or changed and every write method throws. Used by the MCP server.</param>
    public SqlServerHistoryStore(string connectionString, bool readOnly = false)
    {
        _readOnly = readOnly;
        _connectionString = connectionString;
        _connection = new SqlConnection(connectionString);
    }

    private async Task<GateSlot> EnterAsync(CancellationToken ct, [CallerMemberName] string method = "")
    {
        var asked = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLiveAsync(ct);
        }
        catch
        {
            _gate.Release();
            throw;
        }
        var got = Stopwatch.GetTimestamp();
        return new GateSlot(method, got - asked, got);
    }

    /// <summary>
    /// A connection that the server or the network closed still looks open until it is used, and the pool cannot tell
    /// either. A one-row query finds out; a broken connection is replaced, with the pool emptied of its dead siblings.
    /// </summary>
    private async Task EnsureLiveAsync(CancellationToken ct)
    {
        if (_connection.State == ConnectionState.Open)
        {
            try
            {
                await using var ping = _connection.CreateCommand();
                ping.CommandText = "SELECT 1;";
                await ping.ExecuteScalarAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
            }
        }
        // A broken connection object is not reused: it is dropped, together with the pool's dead siblings, and a new one is opened.
        try
        {
            SqlConnection.ClearPool(_connection);
            await _connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
        }
        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync(ct);
    }

    private void Leave(GateSlot slot)
    {
        StoreTrace.Current?.Add(slot.Method, slot.WaitedTicks, Stopwatch.GetTimestamp() - slot.StartedTicks);
        _gate.Release();
    }

    private void RequireWritable()
    {
        if (_readOnly) throw new InvalidOperationException("This history store was opened read-only.");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _connection.OpenAsync(ct);
        if (_readOnly)
        {
            await using var check = _connection.CreateCommand();
            // The table is looked up first: a query that names a missing table fails when it is compiled, not when it runs.
            const string notHistory = "This is not a SqlLookout history database of a version this program can read.";
            check.CommandText = "SELECT OBJECT_ID(N'SchemaVersion', N'U');";
            if (await check.ExecuteScalarAsync(ct) is null or DBNull) throw new InvalidOperationException(notHistory);
            check.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            if (await check.ExecuteScalarAsync(ct) is not int version || version != SchemaVersion) throw new InvalidOperationException(notHistory);
            return;
        }

        await ExecuteAsync($"""
            IF OBJECT_ID(N'Runs', N'U') IS NULL
            CREATE TABLE Runs (
                Id         BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Instance   NVARCHAR(64)  COLLATE {CaseInsensitive} NOT NULL,
                Collector  NVARCHAR(64)  NOT NULL,
                StartedUtc NVARCHAR(24)  NOT NULL,
                DurationMs BIGINT        NOT NULL,
                Status     NVARCHAR(32)  NOT NULL,
                Message    NVARCHAR(MAX) NULL
            );
            IF OBJECT_ID(N'Samples', N'U') IS NULL
            CREATE TABLE Samples (
                Id           BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                RunId        BIGINT        NOT NULL REFERENCES Runs(Id) ON DELETE CASCADE,
                Instance     NVARCHAR(64)  COLLATE {CaseInsensitive} NOT NULL,
                Collector    NVARCHAR(64)  NOT NULL,
                CollectedUtc NVARCHAR(24)  NOT NULL,
                Metric       NVARCHAR(128) NOT NULL,
                Subject      NVARCHAR({MaxSubject}) NULL,
                Value        FLOAT         NULL,
                [Text]       NVARCHAR(MAX) NULL
            );
            IF OBJECT_ID(N'SampleKeys', N'U') IS NULL
            CREATE TABLE SampleKeys (
                Instance   NVARCHAR(64)  COLLATE {CaseInsensitive} NOT NULL,
                Collector  NVARCHAR(64)  NOT NULL,
                Metric     NVARCHAR(128) NOT NULL,
                Subject    NVARCHAR({MaxSubject}) NOT NULL,
                HasSubject INT           NOT NULL,
                PRIMARY KEY NONCLUSTERED (Instance, Collector, Metric, Subject, HasSubject)
            );
            IF OBJECT_ID(N'Alerts', N'U') IS NULL
            CREATE TABLE Alerts (
                [Key]           NVARCHAR({MaxKey}) NOT NULL PRIMARY KEY,
                Instance        NVARCHAR(64)  COLLATE {CaseInsensitive} NOT NULL,
                Severity        NVARCHAR(32)  NOT NULL,
                Title           NVARCHAR(MAX) NOT NULL,
                Detail          NVARCHAR(MAX) NOT NULL,
                FirstUtc        NVARCHAR(24)  NOT NULL,
                LastNotifiedUtc NVARCHAR(24)  NOT NULL,
                ResolvedUtc     NVARCHAR(24)  NULL
            );
            IF OBJECT_ID(N'AlertLog', N'U') IS NULL
            CREATE TABLE AlertLog (
                Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [Key]       NVARCHAR({MaxKey}) NOT NULL,
                Instance    NVARCHAR(64)  COLLATE {CaseInsensitive} NOT NULL,
                Severity    NVARCHAR(32)  NOT NULL,
                Title       NVARCHAR(MAX) NOT NULL,
                Detail      NVARCHAR(MAX) NOT NULL,
                FirstUtc    NVARCHAR(24)  NOT NULL,
                ResolvedUtc NVARCHAR(24)  NULL
            );
            IF OBJECT_ID(N'State', N'U') IS NULL
            CREATE TABLE State (
                [Key]   NVARCHAR(256) NOT NULL PRIMARY KEY,
                [Value] NVARCHAR(MAX) NOT NULL
            );
            IF OBJECT_ID(N'SchemaVersion', N'U') IS NULL
            BEGIN
                CREATE TABLE SchemaVersion (Version INT NOT NULL);
                INSERT INTO SchemaVersion (Version) VALUES ({SchemaVersion});
            END
            """, ct);

        await CreateIndexAsync("IX_Samples_Lookup", "Samples", "Instance, Collector, Metric, Subject, CollectedUtc", ct);
        await CreateIndexAsync("IX_Samples_Series", "Samples", "Instance, Metric, Subject, CollectedUtc", ct);
        await CreateIndexAsync("IX_Samples_Time", "Samples", "CollectedUtc", ct);
        await CreateIndexAsync("IX_Samples_Run", "Samples", "RunId", ct);
        await CreateIndexAsync("IX_Runs_Time", "Runs", "StartedUtc", ct);
        await CreateIndexAsync("IX_Runs_Lookup", "Runs", "Instance, Collector, Id", ct);
        await CreateIndexAsync("IX_AlertLog_Key", "AlertLog", "[Key], ResolvedUtc", ct);
        await CreateIndexAsync("IX_AlertLog_Time", "AlertLog", "FirstUtc", ct);
    }

    private Task CreateIndexAsync(string name, string table, string columns, CancellationToken ct) =>
        ExecuteAsync($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'{table}')) CREATE INDEX {name} ON {table} ({columns});", ct);

    public async Task AppendAsync(string instance, string collector, DateTime collectedUtc, TimeSpan duration,
        CollectionResult result, CancellationToken ct = default)
    {
        RequireWritable();
        var when = FormatTime(collectedUtc);
        var slot = await EnterAsync(ct);
        try
        {
            await using var tx = (SqlTransaction)await _connection.BeginTransactionAsync(ct);

            await using var runCmd = _connection.CreateCommand();
            runCmd.Transaction = tx;
            runCmd.CommandText = """
                INSERT INTO Runs (Instance, Collector, StartedUtc, DurationMs, Status, Message)
                OUTPUT INSERTED.Id
                VALUES (@i, @c, @t, @d, @s, @m);
                """;
            runCmd.Parameters.AddWithValue("@i", instance);
            runCmd.Parameters.AddWithValue("@c", collector);
            runCmd.Parameters.AddWithValue("@t", when);
            runCmd.Parameters.AddWithValue("@d", (long)duration.TotalMilliseconds);
            runCmd.Parameters.AddWithValue("@s", result.Status.ToString());
            runCmd.Parameters.AddWithValue("@m", (object?)result.Message ?? DBNull.Value);
            var runId = Convert.ToInt64(await runCmd.ExecuteScalarAsync(ct));

            foreach (var s in result.Samples)
            {
                var subject = Cap(s.Subject, MaxSubject);
                if (_knownKeys.Add((instance, collector, s.Metric, subject)))
                {
                    await using var keyCmd = _connection.CreateCommand();
                    keyCmd.Transaction = tx;
                    keyCmd.CommandText = """
                        IF NOT EXISTS (SELECT 1 FROM SampleKeys WHERE Instance = @i AND Collector = @c AND Metric = @m AND Subject = @s AND HasSubject = @h)
                            INSERT INTO SampleKeys (Instance, Collector, Metric, Subject, HasSubject) VALUES (@i, @c, @m, @s, @h);
                        """;
                    keyCmd.Parameters.AddWithValue("@i", instance);
                    keyCmd.Parameters.AddWithValue("@c", collector);
                    keyCmd.Parameters.AddWithValue("@m", s.Metric);
                    keyCmd.Parameters.AddWithValue("@s", subject ?? "");
                    keyCmd.Parameters.AddWithValue("@h", subject is null ? 0 : 1);
                    await keyCmd.ExecuteNonQueryAsync(ct);
                }

                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO Samples (RunId, Instance, Collector, CollectedUtc, Metric, Subject, Value, [Text])
                    VALUES (@r, @i, @c, @t, @m, @s, @v, @x);
                    """;
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.Parameters.AddWithValue("@i", instance);
                cmd.Parameters.AddWithValue("@c", collector);
                cmd.Parameters.AddWithValue("@t", when);
                cmd.Parameters.AddWithValue("@m", s.Metric);
                cmd.Parameters.AddWithValue("@s", (object?)subject ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@v", (object?)s.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@x", (object?)s.Text ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            // A failed batch may have registered keys that were rolled back; forget them so the next run writes them again.
            _knownKeys.Clear();
            throw;
        }
        finally
        {
            Leave(slot);
        }
    }

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
            var keys = new List<(string Collector, string Metric, string? Subject)>();
            await using (var keyCmd = _connection.CreateCommand())
            {
                keyCmd.CommandText = "SELECT Collector, Metric, CASE WHEN HasSubject = 1 THEN Subject END FROM SampleKeys WHERE Instance = @i ORDER BY Collector, Metric, HasSubject, Subject;";
                keyCmd.Parameters.AddWithValue("@i", instance);
                await using var keyReader = await keyCmd.ExecuteReaderAsync(ct);
                while (await keyReader.ReadAsync(ct))
                    keys.Add((keyReader.GetString(0), keyReader.GetString(1), keyReader.IsDBNull(2) ? null : keyReader.GetString(2)));
            }

            // A NULL subject needs "IS NULL"; comparing with "=" would never match and "OR ... IS NULL" prevents an index seek.
            const string newestWithSubject = """
                SELECT TOP 1 CollectedUtc, Value, [Text] FROM Samples
                WHERE Instance = @i AND Collector = @c AND Metric = @m AND Subject = @s
                ORDER BY CollectedUtc DESC, Id DESC;
                """;
            const string newestWithoutSubject = """
                SELECT TOP 1 CollectedUtc, Value, [Text] FROM Samples
                WHERE Instance = @i AND Collector = @c AND Metric = @m AND Subject IS NULL
                ORDER BY CollectedUtc DESC, Id DESC;
                """;

            var list = new List<StoredSample>(keys.Count);
            foreach (var (collector, metric, subject) in keys)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = subject is null ? newestWithoutSubject : newestWithSubject;
                cmd.Parameters.AddWithValue("@i", instance);
                cmd.Parameters.AddWithValue("@c", collector);
                cmd.Parameters.AddWithValue("@m", metric);
                if (subject is not null) cmd.Parameters.AddWithValue("@s", subject);
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
            subject = Cap(subject, MaxSubject);
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT CollectedUtc, Value FROM Samples
                WHERE Instance = @i AND Metric = @m AND {(subject is null ? "Subject IS NULL" : "Subject = @s")}
                  AND CollectedUtc >= @t AND Value IS NOT NULL
                ORDER BY CollectedUtc, Id;
                """;
            cmd.Parameters.AddWithValue("@i", instance);
            cmd.Parameters.AddWithValue("@m", metric);
            if (subject is not null) cmd.Parameters.AddWithValue("@s", subject);
            cmd.Parameters.AddWithValue("@t", FormatTime(sinceUtc));
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
                SELECT TOP (@n) StartedUtc, Status, Message FROM Runs
                WHERE Instance = @i AND Collector = @c ORDER BY Id DESC;
                """;
            cmd.Parameters.AddWithValue("@i", instance);
            cmd.Parameters.AddWithValue("@c", collector);
            cmd.Parameters.AddWithValue("@n", count);
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
                SELECT [Key], Instance, Severity, Title, Detail, FirstUtc, LastNotifiedUtc
                FROM Alerts WHERE ResolvedUtc IS NULL ORDER BY Instance, [Key];
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
        RequireWritable();
        var key = Cap(alert.Key, MaxKey)!;
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            // In an UPDATE every right-hand side sees the old row, so FirstUtc keeps its value while the problem stayed open.
            cmd.CommandText = """
                UPDATE Alerts SET
                    Severity = @s, Title = @t, Detail = @d, LastNotifiedUtc = @n, ResolvedUtc = NULL,
                    FirstUtc = CASE WHEN ResolvedUtc IS NULL THEN FirstUtc ELSE @f END
                WHERE [Key] = @k;
                IF @@ROWCOUNT = 0
                    INSERT INTO Alerts ([Key], Instance, Severity, Title, Detail, FirstUtc, LastNotifiedUtc, ResolvedUtc)
                    VALUES (@k, @i, @s, @t, @d, @f, @n, NULL);
                """;
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@i", alert.Instance);
            cmd.Parameters.AddWithValue("@s", alert.Severity);
            cmd.Parameters.AddWithValue("@t", alert.Title);
            cmd.Parameters.AddWithValue("@d", alert.Detail);
            cmd.Parameters.AddWithValue("@f", FormatTime(alert.FirstUtc));
            cmd.Parameters.AddWithValue("@n", FormatTime(alert.LastNotifiedUtc));
            await cmd.ExecuteNonQueryAsync(ct);

            // The log: refresh the open row, or start one (a new problem, one that came back, or one open before the log existed).
            await using var log = _connection.CreateCommand();
            log.CommandText = """
                UPDATE AlertLog SET Severity = @s, Title = @t, Detail = @d WHERE [Key] = @k AND ResolvedUtc IS NULL;
                IF @@ROWCOUNT = 0
                    INSERT INTO AlertLog ([Key], Instance, Severity, Title, Detail, FirstUtc, ResolvedUtc)
                    SELECT @k, @i, @s, @t, @d, FirstUtc, NULL FROM Alerts WHERE [Key] = @k;
                """;
            log.Parameters.AddWithValue("@k", key);
            log.Parameters.AddWithValue("@i", alert.Instance);
            log.Parameters.AddWithValue("@s", alert.Severity);
            log.Parameters.AddWithValue("@t", alert.Title);
            log.Parameters.AddWithValue("@d", alert.Detail);
            await log.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task ResolveAlertAsync(string key, DateTime resolvedUtc, CancellationToken ct = default)
    {
        RequireWritable();
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE Alerts SET ResolvedUtc = @t WHERE [Key] = @k AND ResolvedUtc IS NULL;
                UPDATE AlertLog SET ResolvedUtc = @t WHERE [Key] = @k AND ResolvedUtc IS NULL;
                """;
            cmd.Parameters.AddWithValue("@k", Cap(key, MaxKey)!);
            cmd.Parameters.AddWithValue("@t", FormatTime(resolvedUtc));
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
            cmd.CommandText = $"""
                SELECT [Key], Instance, Severity, Title, Detail, FirstUtc, ResolvedUtc
                FROM AlertLog
                WHERE (ResolvedUtc IS NULL OR ResolvedUtc >= @since) AND (@i IS NULL OR Instance = @i COLLATE {CaseInsensitive})
                ORDER BY FirstUtc DESC, Id DESC;
                """;
            cmd.Parameters.AddWithValue("@since", FormatTime(sinceUtc));
            cmd.Parameters.Add(new SqlParameter("@i", SqlDbType.NVarChar, 64) { Value = (object?)instance ?? DBNull.Value });
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
            cmd.CommandText = "SELECT [Value] FROM State WHERE [Key] = @k;";
            cmd.Parameters.AddWithValue("@k", key);
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        RequireWritable();
        var slot = await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE State SET [Value] = @v WHERE [Key] = @k;
                IF @@ROWCOUNT = 0 INSERT INTO State ([Key], [Value]) VALUES (@k, @v);
                """;
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            Leave(slot);
        }
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        RequireWritable();
        var slot = await EnterAsync(ct);
        try
        {
            var cutoff = FormatTime(cutoffUtc);
            var removed = 0;
            // Samples go with their run (ON DELETE CASCADE). Batches keep each transaction, and the log, small.
            while (true)
            {
                await using var batch = _connection.CreateCommand();
                batch.CommandTimeout = 300;
                batch.CommandText = "DELETE TOP (@n) FROM Runs WHERE StartedUtc < @t;";
                batch.Parameters.AddWithValue("@n", PurgeBatchSize);
                batch.Parameters.AddWithValue("@t", cutoff);
                var n = await batch.ExecuteNonQueryAsync(ct);
                removed += n;
                if (n < PurgeBatchSize) break;
            }

            // Forget combinations that no longer have any sample (a dropped database, say).
            await using var cmd = _connection.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = """
                DELETE FROM SampleKeys WHERE NOT EXISTS (
                    SELECT 1 FROM Samples s WHERE s.Instance = SampleKeys.Instance AND s.Collector = SampleKeys.Collector
                      AND s.Metric = SampleKeys.Metric
                      AND ((SampleKeys.HasSubject = 1 AND s.Subject = SampleKeys.Subject) OR (SampleKeys.HasSubject = 0 AND s.Subject IS NULL)));
                DELETE FROM AlertLog WHERE ResolvedUtc IS NOT NULL AND ResolvedUtc < @t;
                """;
            cmd.Parameters.AddWithValue("@t", cutoff);
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

    private static string? Cap(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

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
