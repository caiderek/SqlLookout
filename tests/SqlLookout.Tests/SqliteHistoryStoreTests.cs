using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>The history store tests; <see cref="SqlServerHistoryStoreTests"/> runs every one of them against SQL Server.</summary>
public class SqliteHistoryStoreTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-test-{Guid.NewGuid():N}.db");
    protected IHistoryStore _store = null!;

    /// <summary>False in the SQL Server run: the test that fills a SQLite file by hand does not apply there.</summary>
    protected virtual bool IsSqlite => true;

    public virtual async Task InitializeAsync()
    {
        _store = new SqliteHistoryStore(_file);
        await _store.InitializeAsync();
    }

    public virtual async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*"))
            File.Delete(f);
    }

    private static CollectionResult Ok(params Sample[] samples) => CollectionResult.Ok(samples);

    [Fact]
    public async Task Latest_returns_newest_value_per_key()
    {
        var t1 = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        await _store.AppendAsync("srv", "disk_space", t1, TimeSpan.FromMilliseconds(5), Ok(new Sample("disk_free_bytes", "C:\\", 100)));
        await _store.AppendAsync("srv", "disk_space", t1.AddMinutes(5), TimeSpan.FromMilliseconds(5), Ok(new Sample("disk_free_bytes", "C:\\", 90)));
        await _store.AppendAsync("srv", "disk_space", t1.AddMinutes(5), TimeSpan.FromMilliseconds(5), Ok(new Sample("disk_free_bytes", "D:\\", 500)));

        var latest = await _store.GetLatestAsync("srv");

        Assert.Equal(2, latest.Count);
        Assert.Equal(90, latest.Single(s => s.Sample.Subject == "C:\\").Sample.Value);
        Assert.Equal(500, latest.Single(s => s.Sample.Subject == "D:\\").Sample.Value);
        Assert.Equal(t1.AddMinutes(5), latest[0].CollectedUtc);
        Assert.Equal(DateTimeKind.Utc, latest[0].CollectedUtc.Kind);
    }

    [Fact]
    public async Task Null_value_and_null_subject_round_trip()
    {
        var t = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        await _store.AppendAsync("srv", "backup_age", t, TimeSpan.Zero, Ok(
            new Sample("last_full_backup_age_min", "Sales", null),
            new Sample("server_metric", null, 7)));

        var latest = await _store.GetLatestAsync("srv");

        Assert.Null(latest.Single(s => s.Sample.Subject == "Sales").Sample.Value);
        Assert.Equal(7, latest.Single(s => s.Sample.Subject is null).Sample.Value);
    }

    [Fact]
    public async Task Instances_are_kept_apart()
    {
        var t = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        await _store.AppendAsync("a", "log_usage", t, TimeSpan.Zero, Ok(new Sample("log_used_percent", "db", 10)));
        await _store.AppendAsync("b", "log_usage", t, TimeSpan.Zero, Ok(new Sample("log_used_percent", "db", 99)));

        Assert.Equal(10, (await _store.GetLatestAsync("a")).Single().Sample.Value);
        Assert.Equal(99, (await _store.GetLatestAsync("b")).Single().Sample.Value);
    }

    [Fact]
    public async Task Purge_removes_old_runs_and_their_samples_only()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fresh = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        await _store.AppendAsync("srv", "log_usage", old, TimeSpan.Zero, Ok(new Sample("log_used_percent", "x", 1)));
        await _store.AppendAsync("srv", "log_usage", fresh, TimeSpan.Zero, Ok(new Sample("log_used_percent", "y", 2)));

        var removed = await _store.PurgeOlderThanAsync(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, removed);
        var left = await _store.GetLatestAsync("srv");
        Assert.Equal("y", left.Single().Sample.Subject);
    }

    [Fact]
    public async Task Non_utc_time_is_refused()
    {
        var local = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Local);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.AppendAsync("srv", "c", local, TimeSpan.Zero, Ok()));
    }

    [Fact]
    public async Task Latest_keeps_server_wide_and_named_values_apart_and_ignores_other_instances()
    {
        var t = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        await _store.AppendAsync("a", "c", t, TimeSpan.Zero, Ok(new Sample("m", null, 1), new Sample("m", "x", 2)));
        await _store.AppendAsync("a", "c", t.AddMinutes(1), TimeSpan.Zero, Ok(new Sample("m", null, 10), new Sample("m", "x", 20)));
        await _store.AppendAsync("b", "c", t.AddMinutes(5), TimeSpan.Zero, Ok(new Sample("m", null, 99)));

        var latest = await _store.GetLatestAsync("a");

        Assert.Equal(2, latest.Count);
        Assert.Equal(10, latest.Single(s => s.Sample.Subject is null).Sample.Value);
        Assert.Equal(20, latest.Single(s => s.Sample.Subject == "x").Sample.Value);
    }

    [Fact]
    public async Task Latest_keeps_null_and_named_subjects_apart_and_drops_keys_whose_samples_were_purged()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        await _store.AppendAsync("srv", "c", old, TimeSpan.Zero, Ok(new Sample("m", null, 1), new Sample("gone", "x", 2)));
        await _store.AppendAsync("srv", "c", now, TimeSpan.Zero, Ok(new Sample("m", null, 3), new Sample("m", "", 4), new Sample("m", "a", 5)));

        var before = await _store.GetLatestAsync("srv");
        Assert.Equal(4, before.Count);
        Assert.Equal(3, before.Single(s => s.Sample.Metric == "m" && s.Sample.Subject is null).Sample.Value);
        Assert.Equal(4, before.Single(s => s.Sample.Subject == "").Sample.Value);

        await _store.PurgeOlderThanAsync(now.AddDays(-1));
        await _store.AppendAsync("srv", "c", now.AddMinutes(1), TimeSpan.Zero, Ok(new Sample("gone", "x", 9)));

        var after = await _store.GetLatestAsync("srv");
        Assert.Equal(4, after.Count);
        Assert.Equal(9, after.Single(s => s.Sample.Metric == "gone").Sample.Value);
    }

    [Fact]
    public async Task Latest_stays_fast_when_an_instance_has_weeks_of_history()
    {
        if (!IsSqlite) return;
        // 15 keys x 3,000 readings = 45,000 rows for one instance. Checking "is this the newest of its key?"
        // once per stored row would need minutes at 130,000 rows; GetLatestAsync must stay fast.
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_file}"))
        {
            await raw.OpenAsync();
            await using var tx = raw.BeginTransaction();
            await using var run = raw.CreateCommand();
            run.Transaction = tx;
            run.CommandText = "INSERT INTO Runs (Instance, Collector, StartedUtc, DurationMs, Status) VALUES ('big', 'c', '2026-10-01 00:00:00', 1, 'Ok');";
            await run.ExecuteNonQueryAsync();
            await using var insert = raw.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO Samples (RunId, Instance, Collector, CollectedUtc, Metric, Subject, Value) VALUES (1, 'big', 'c', $t, $m, $s, $v);";
            var pt = insert.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Text);
            var pm = insert.Parameters.Add("$m", Microsoft.Data.Sqlite.SqliteType.Text);
            var ps = insert.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Text);
            var pv = insert.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Real);
            var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 3000; i++)
                for (var k = 0; k < 15; k++)
                {
                    pt.Value = start.AddMinutes(i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
                    pm.Value = $"m{k}";
                    ps.Value = k % 2 == 0 ? DBNull.Value : "s";
                    pv.Value = i;
                    await insert.ExecuteNonQueryAsync();
                }
            await tx.CommitAsync();
        }

        // A data file written by an earlier build has no list of keys yet; opening it builds the list once.
        await using var upgraded = new SqliteHistoryStore(_file);
        await upgraded.InitializeAsync();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var latest = await upgraded.GetLatestAsync("big");
        watch.Stop();

        Assert.Equal(15, latest.Count);
        Assert.All(latest, s => Assert.Equal(2999, s.Sample.Value));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"GetLatestAsync took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task Alert_keeps_its_first_time_while_open_and_starts_a_new_episode_after_it_was_resolved()
    {
        var t0 = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
        await _store.UpsertAlertAsync(new AlertRecord("k1", "srv", "warning", "t", "d1", t0, t0, null));
        await _store.UpsertAlertAsync(new AlertRecord("k1", "srv", "critical", "t2", "d2", t0.AddMinutes(10), t0.AddMinutes(10), null));

        var open = Assert.Single(await _store.GetActiveAlertsAsync());
        Assert.Equal(t0, open.FirstUtc);
        Assert.Equal("critical", open.Severity);
        Assert.Equal("d2", open.Detail);
        Assert.Equal(t0.AddMinutes(10), open.LastNotifiedUtc);

        await _store.ResolveAlertAsync("k1", t0.AddMinutes(20));
        Assert.Empty(await _store.GetActiveAlertsAsync());

        await _store.UpsertAlertAsync(new AlertRecord("k1", "srv", "warning", "t3", "d3", t0.AddMinutes(30), t0.AddMinutes(30), null));
        Assert.Equal(t0.AddMinutes(30), Assert.Single(await _store.GetActiveAlertsAsync()).FirstUtc);

        var log = await _store.GetAlertLogAsync(t0.AddHours(-1), null);
        Assert.Equal(2, log.Count);
        Assert.Equal(t0.AddMinutes(30), log[0].FirstUtc);
        Assert.Null(log[0].ResolvedUtc);
        Assert.Equal(t0, log[1].FirstUtc);
        Assert.Equal(t0.AddMinutes(20), log[1].ResolvedUtc);
        Assert.Equal("critical", log[1].Severity);
        Assert.Empty(await _store.GetAlertLogAsync(t0.AddHours(-1), "other"));
        Assert.Equal(2, (await _store.GetAlertLogAsync(t0.AddHours(-1), "SRV")).Count);
    }

    [Fact]
    public async Task State_values_are_overwritten_not_duplicated()
    {
        Assert.Null(await _store.GetStateAsync("summary"));
        await _store.SetStateAsync("summary", "2026-10-19");
        await _store.SetStateAsync("summary", "2026-10-20");
        Assert.Equal("2026-10-20", await _store.GetStateAsync("summary"));
    }
}
