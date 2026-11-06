using System.Data;
using Microsoft.Data.SqlClient;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>
/// A throw-away database on a real SQL Server for the store tests. The server comes from the environment variable
/// SQLLOOKOUT_TEST_SQL (a connection string whose login may create databases); without it the local default instance is used
/// with Windows authentication.
/// </summary>
internal sealed class SqlServerTestDb : IAsyncDisposable
{
    /// <summary>
    /// Tests that create and drop databases on the local server, and the collector tests that expect it to be quiet (no
    /// blocking anywhere), share this xunit collection so they do not run at the same time.
    /// </summary>
    public const string LocalServerCollection = "local SQL Server";

    private readonly string _serverConnection;
    private readonly string _name;

    public string ConnectionString { get; }

    private SqlServerTestDb(string serverConnection, string name)
    {
        _serverConnection = serverConnection;
        _name = name;
        ConnectionString = new SqlConnectionStringBuilder(serverConnection) { InitialCatalog = name }.ConnectionString;
    }

    public static async Task<SqlServerTestDb> CreateAsync()
    {
        var server = Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_SQL")
            ?? "Server=.;Integrated Security=true;TrustServerCertificate=true";
        var name = "SqlLookoutTest_" + Guid.NewGuid().ToString("N");
        try
        {
            await using var connection = new SqlConnection(server);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{name}];";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException("The SQL Server store tests need a SQL Server where this login can create databases (set SQLLOOKOUT_TEST_SQL to a connection string). " + ex.Message, ex);
        }
        return new SqlServerTestDb(server, name);
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(_serverConnection);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>Closes every other connection to this database, as a network break or a server restart would.</summary>
    public async Task KillConnectionsAsync()
    {
        await using var connection = new SqlConnection(_serverConnection);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DECLARE @sql NVARCHAR(MAX) = N'';
            SELECT @sql += N'KILL ' + CAST(session_id AS NVARCHAR(10)) + N';'
            FROM sys.dm_exec_sessions WHERE database_id = DB_ID(@db) AND is_user_process = 1 AND session_id <> @@SPID;
            EXEC (@sql);
            """;
        cmd.Parameters.AddWithValue("@db", _name);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Every settings store test, run against SQL Server.</summary>
[Collection(SqlServerTestDb.LocalServerCollection)]
public sealed class SqlServerSettingsStoreTests : SettingsStoreTests
{
    private SqlServerTestDb _db = null!;

    protected override bool IsSqlite => false;

    public override async Task InitializeAsync()
    {
        _db = await SqlServerTestDb.CreateAsync();
        try
        {
            _store = SettingsStore.ForSqlServer(_db.ConnectionString, new FakeSecretProtector());
            await _store.InitializeAsync();
        }
        catch
        {
            // xunit does not call DisposeAsync when InitializeAsync throws, so the database would be left behind.
            await _db.DisposeAsync();
            throw;
        }
    }

    public override async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override string RawDump()
    {
        var users = _db.ScalarAsync("SELECT COALESCE(STRING_AGG(CAST(UserName AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT + '|' + Hash + '|' + Salt, ';'), '') FROM Users;").GetAwaiter().GetResult();
        var instances = _db.ScalarAsync("SELECT COALESCE(STRING_AGG(CAST(Name AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT + '|' + Server + '|' + COALESCE(UserName, '') + '|' + COALESCE(PasswordProtected, ''), ';'), '') FROM Instances;").GetAwaiter().GetResult();
        var settings = _db.ScalarAsync("SELECT COALESCE(STRING_AGG(CAST([Key] AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT + '|' + [Value], ';'), '') FROM Settings;").GetAwaiter().GetResult();
        return users + "\n" + instances + "\n" + settings;
    }

    [Fact]
    public async Task A_second_start_on_the_same_database_keeps_the_data()
    {
        await _store.CreateAdminAsync("admin-user", "a-long-password");
        await _store.SaveInstanceAsync(new InstanceRecord("prod", "PROD01", AuthModes.Windows, null, EncryptModes.Trust, true, false), null, isNew: true);

        await using var again = SettingsStore.ForSqlServer(_db.ConnectionString, new FakeSecretProtector());
        await again.InitializeAsync();

        Assert.True(await again.HasAdminAsync());
        Assert.Equal("PROD01", (await again.GetInstanceAsync("PROD"))!.Server);
    }

    [Fact]
    public async Task The_connection_is_opened_again_after_it_was_closed_by_the_server()
    {
        await _store.CreateAdminAsync("admin-user", "a-long-password");

        await _db.KillConnectionsAsync();

        Assert.True(await _store.HasAdminAsync());
        var (id, key) = await _store.IssueApiKeyAsync("tool", DateTime.UtcNow.AddDays(5), null);
        Assert.True(id > 0);
        Assert.StartsWith("slk_", key);
    }
}

/// <summary>Every history store test, run against SQL Server, plus what only applies to it.</summary>
[Collection(SqlServerTestDb.LocalServerCollection)]
public sealed class SqlServerHistoryStoreTests : SqliteHistoryStoreTests
{
    private SqlServerTestDb _db = null!;

    protected override bool IsSqlite => false;

    public override async Task InitializeAsync()
    {
        _db = await SqlServerTestDb.CreateAsync();
        try
        {
            _store = new SqlServerHistoryStore(_db.ConnectionString);
            await _store.InitializeAsync();
        }
        catch
        {
            await _db.DisposeAsync();
            throw;
        }
    }

    public override async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static CollectionResult Ok(params Sample[] samples) => CollectionResult.Ok(samples);

    private static readonly DateTime T0 = new(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_second_start_on_the_same_database_keeps_the_history()
    {
        await _store.AppendAsync("srv", "disk", T0, TimeSpan.FromMilliseconds(5), Ok(new Sample("free", "C:\\", 7)));

        await using var again = new SqlServerHistoryStore(_db.ConnectionString);
        await again.InitializeAsync();

        Assert.Equal(7, Assert.Single(await again.GetLatestAsync("srv")).Sample.Value);
    }

    [Fact]
    public async Task The_connection_is_opened_again_after_it_was_closed_by_the_server()
    {
        await _store.AppendAsync("srv", "disk", T0, TimeSpan.FromMilliseconds(5), Ok(new Sample("free", "C:\\", 1)));

        await _db.KillConnectionsAsync();
        await _store.AppendAsync("srv", "disk", T0.AddMinutes(5), TimeSpan.FromMilliseconds(5), Ok(new Sample("free", "C:\\", 2)));

        Assert.Equal(2, Assert.Single(await _store.GetLatestAsync("srv")).Sample.Value);
    }

    [Fact]
    public async Task A_failed_batch_is_rolled_back_whole_and_its_keys_are_written_again_next_time()
    {
        var bad = Ok(new Sample("m1", "A", 1), new Sample("m2", "B", double.NaN));
        await Assert.ThrowsAnyAsync<Exception>(() => _store.AppendAsync("srv", "c", T0, TimeSpan.Zero, bad));
        Assert.Equal(0L, Convert.ToInt64(await _db.ScalarAsync("SELECT COUNT(*) FROM Runs;")));

        await _store.AppendAsync("srv", "c", T0.AddMinutes(1), TimeSpan.Zero, Ok(new Sample("m1", "A", 3)));

        Assert.Equal(3, Assert.Single(await _store.GetLatestAsync("srv")).Sample.Value);
    }

    [Fact]
    public async Task A_subject_longer_than_the_column_is_cut_and_still_found()
    {
        var longSubject = new string('x', 500);
        await _store.AppendAsync("srv", "c", T0, TimeSpan.Zero, Ok(new Sample("m", longSubject, 5)));

        var latest = Assert.Single(await _store.GetLatestAsync("srv"));
        Assert.Equal(400, latest.Sample.Subject!.Length);
        Assert.Single(await _store.GetSeriesAsync("srv", "m", longSubject, T0.AddHours(-1)));
    }

    [Fact]
    public async Task Purge_deletes_in_several_batches()
    {
        await using var small = new SqlServerHistoryStore(_db.ConnectionString) { PurgeBatchSize = 2 };
        await small.InitializeAsync();
        for (var i = 0; i < 7; i++)
            await small.AppendAsync("srv", "c", T0.AddMinutes(i), TimeSpan.Zero, Ok(new Sample("m", null, i)));

        var removed = await small.PurgeOlderThanAsync(T0.AddMinutes(5));

        Assert.Equal(5, removed);
        Assert.Equal(2L, Convert.ToInt64(await _db.ScalarAsync("SELECT COUNT(*) FROM Runs;")));
        Assert.Equal(2L, Convert.ToInt64(await _db.ScalarAsync("SELECT COUNT(*) FROM Samples;")));
    }

    [Fact]
    public async Task Read_only_store_reads_but_refuses_every_write()
    {
        await _store.AppendAsync("srv", "c", T0, TimeSpan.Zero, Ok(new Sample("m", null, 4)));
        await _store.SetStateAsync("k", "v");

        await using var reader = new SqlServerHistoryStore(_db.ConnectionString, readOnly: true);
        await reader.InitializeAsync();

        Assert.Equal(4, Assert.Single(await reader.GetLatestAsync("srv")).Sample.Value);
        Assert.Equal(["srv"], await reader.ListInstancesAsync());
        Assert.Equal("v", await reader.GetStateAsync("k"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.AppendAsync("srv", "c", T0, TimeSpan.Zero, Ok()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SetStateAsync("k", "w"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.PurgeOlderThanAsync(T0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ResolveAlertAsync("a", T0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.UpsertAlertAsync(new AlertRecord("a", "srv", "warning", "t", "d", T0, T0, null)));
    }

    [Fact]
    public async Task Read_only_store_refuses_a_database_that_is_not_a_sqllookout_history()
    {
        await using var other = await SqlServerTestDb.CreateAsync();
        await using var reader = new SqlServerHistoryStore(other.ConnectionString, readOnly: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.InitializeAsync());
        // Read-only never creates anything, so the refused database stays empty.
        Assert.IsType<DBNull>(await other.ScalarAsync("SELECT OBJECT_ID(N'Runs', N'U');"));
    }

    [Fact]
    public async Task Latest_stays_fast_with_many_stored_readings()
    {
        // 15 keys x 300 readings; GetLatestAsync reads the key list and then one row per key.
        for (var i = 0; i < 300; i++)
        {
            var samples = Enumerable.Range(0, 15).Select(k => new Sample($"m{k}", k % 2 == 0 ? null : "s", i)).ToArray();
            await _store.AppendAsync("big", "c", T0.AddMinutes(i), TimeSpan.Zero, Ok(samples));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var latest = await _store.GetLatestAsync("big");
        watch.Stop();

        Assert.Equal(15, latest.Count);
        Assert.All(latest, s => Assert.Equal(299, s.Sample.Value));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"GetLatestAsync took {watch.Elapsed.TotalSeconds:0.0} s");
    }
}
