using Microsoft.Data.Sqlite;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>Alerts for blocking, memory (page life expectancy, memory grants), deadlocks, connections and disk I/O latency.</summary>
public sealed class PerformanceAlertTests : IAsyncLifetime
{
    private static readonly AlertThresholds T = new();
    private static readonly HashSet<string> None = [];
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static List<(DateTime Utc, double Value)> PerMinute(params double[] values) =>
        values.Select((v, i) => (Now.AddMinutes(i), v)).ToList();

    private static StoredSample S(string metric, double? value, string? text = null) =>
        new("srv", "blocking", Now, new Sample(metric, null, value, text));

    // ---------------------------------------------------------------- blocking

    [Theory]
    [InlineData(59.9, null)]
    [InlineData(60, Severity.Warning)]
    [InlineData(299.9, Severity.Warning)]
    [InlineData(300, Severity.Critical)]
    public void Blocking_levels_follow_the_longest_current_wait(double wait, Severity? expected)
    {
        var f = AlertRules.BlockingFinding("srv", [S("blocked_sessions", 3), S("blocking_longest_wait_sec", wait)], T);

        Assert.Equal(expected, f?.Severity);
    }

    [Fact]
    public void Blocking_names_the_head_blockers_and_the_count_and_goes_through_the_normal_evaluation()
    {
        var latest = new[] { S("blocked_sessions", 4), S("blocking_longest_wait_sec", 120), S("blocking_head_blockers", 1, "57 (app@HOST1)") };

        var findings = AlertRules.Evaluate("srv", latest, T, None);

        var f = Assert.Single(findings);
        Assert.Equal("srv|blocking|", f.Key);
        Assert.Contains("4 session(s) are blocked", f.Detail);
        Assert.Contains("120 s", f.Detail);
        Assert.Contains("57 (app@HOST1)", f.Detail);
    }

    [Fact]
    public void Blocking_can_be_switched_off_and_a_reading_with_nobody_blocked_is_quiet()
    {
        var latest = new[] { S("blocked_sessions", 4), S("blocking_longest_wait_sec", 500) };

        Assert.Empty(AlertRules.Evaluate("srv", latest, T, new HashSet<string> { AlertRules.RuleBlocking }));
        Assert.Empty(AlertRules.Evaluate("srv", [S("blocked_sessions", 0), S("blocking_longest_wait_sec", 0)], T, None));
        Assert.Empty(AlertRules.Evaluate("srv", [], T, None));
    }

    // ---------------------------------------------------------------- page life expectancy

    [Fact]
    public void Page_life_expectancy_alerts_only_when_every_reading_of_the_period_is_below_the_limit()
    {
        var low = PerMinute(Enumerable.Repeat(120.0, 11).ToArray());                // 10 minutes at 120 s

        var f = AlertRules.PageLifeExpectancyFinding("srv", low, T);

        Assert.Equal(Severity.Warning, f?.Severity);
        Assert.Equal("srv|ple|", f!.Key);
        Assert.Contains("120", f.Title);

        var recovered = Enumerable.Repeat(120.0, 11).ToArray();
        recovered[4] = 300;                                                          // exactly the limit is fine
        Assert.Null(AlertRules.PageLifeExpectancyFinding("srv", PerMinute(recovered), T));
    }

    [Fact]
    public void Page_life_expectancy_is_quiet_right_after_a_restart_or_with_too_little_history()
    {
        // A restart: the value climbs one second per second, so it passes the limit within the period.
        var climbing = Enumerable.Range(0, 11).Select(i => i * 60.0).ToArray();
        Assert.Null(AlertRules.PageLifeExpectancyFinding("srv", PerMinute(climbing), T));
        Assert.Null(AlertRules.PageLifeExpectancyFinding("srv", PerMinute(Enumerable.Repeat(50.0, 5).ToArray()), T));   // 4 minutes
        Assert.Null(AlertRules.PageLifeExpectancyFinding("srv", PerMinute(50, 50), T));
        Assert.Null(AlertRules.PageLifeExpectancyFinding("srv", [], T));
    }

    // ---------------------------------------------------------------- memory grants

    [Fact]
    public void Memory_grants_alert_only_when_a_query_waits_at_every_reading_for_the_whole_period()
    {
        var waiting = PerMinute(Enumerable.Repeat(2.0, 6).ToArray());               // 5 minutes

        Assert.Equal(Severity.Warning, AlertRules.MemoryGrantsFinding("srv", waiting, T)?.Severity);
        Assert.Equal("srv|grants|", AlertRules.MemoryGrantsFinding("srv", waiting, T)!.Key);

        var blip = Enumerable.Repeat(2.0, 6).ToArray();
        blip[3] = 0;
        Assert.Null(AlertRules.MemoryGrantsFinding("srv", PerMinute(blip), T));
        Assert.Null(AlertRules.MemoryGrantsFinding("srv", PerMinute(1, 1, 1), T));   // 2 minutes
        Assert.Null(AlertRules.MemoryGrantsFinding("srv", [], T));
    }

    // ---------------------------------------------------------------- deadlocks

    [Theory]
    [InlineData(2, null)]
    [InlineData(3, Severity.Warning)]
    [InlineData(19, Severity.Warning)]
    [InlineData(20, Severity.Critical)]
    public void Deadlock_levels_follow_the_rise_of_the_running_total(int rise, Severity? expected)
    {
        var f = AlertRules.DeadlockFinding("srv", PerMinute(1000, 1000, 1000 + rise), T);

        Assert.Equal(expected, f?.Severity);
    }

    [Fact]
    public void Deadlocks_already_in_the_total_before_the_window_do_not_count_and_a_restart_does_not_hide_new_ones()
    {
        Assert.Null(AlertRules.DeadlockFinding("srv", PerMinute(5000, 5000, 5000), T));            // a big total, no rise
        Assert.Null(AlertRules.DeadlockFinding("srv", PerMinute(7), T));                           // one reading
        Assert.Null(AlertRules.DeadlockFinding("srv", [], T));

        // 100 -> 102 (+2), the instance restarts and the first reading after it is already 4 (those 4 are new), then 6 (+2): 8.
        var f = AlertRules.DeadlockFinding("srv", PerMinute(100, 102, 4, 6), T);
        Assert.Equal(Severity.Warning, f?.Severity);
        Assert.Contains("8 deadlock(s)", f!.Detail);
    }

    // ---------------------------------------------------------------- connections

    [Theory]
    [InlineData(499, null)]
    [InlineData(500, Severity.Warning)]
    [InlineData(1499, Severity.Warning)]
    [InlineData(1500, Severity.Critical)]
    public void Connection_levels_follow_the_lowest_reading_of_the_period(double lowest, Severity? expected)
    {
        var f = AlertRules.ConnectionsFinding("srv", PerMinute(lowest + 10, lowest, lowest + 40, lowest + 5, lowest + 1, lowest + 2), T);

        Assert.Equal(expected, f?.Severity);
        if (f is not null) Assert.Equal("srv|connections|", f.Key);
    }

    [Fact]
    public void Connections_ignore_a_short_spike_and_too_little_history()
    {
        Assert.Null(AlertRules.ConnectionsFinding("srv", PerMinute(900, 900, 100, 900, 900, 900), T));   // one reading dipped
        Assert.Null(AlertRules.ConnectionsFinding("srv", PerMinute(900, 900, 900), T));                  // 2 minutes
        Assert.Null(AlertRules.ConnectionsFinding("srv", PerMinute(900, 900), T));
        Assert.Null(AlertRules.ConnectionsFinding("srv", [], T));
    }

    // ---------------------------------------------------------------- disk I/O latency

    [Theory]
    [InlineData(49.9, null)]
    [InlineData(50, Severity.Warning)]
    [InlineData(149.9, Severity.Warning)]
    [InlineData(150, Severity.Critical)]
    public void Io_latency_levels_follow_the_lowest_reading_of_the_period(double lowest, Severity? expected)
    {
        var series = Enumerable.Range(0, 4).Select(i => (Now.AddMinutes(i * 5), lowest + i)).ToList();    // 15 minutes, every 5

        var read = AlertRules.IoLatencyFinding("srv", "read", series, T);
        var write = AlertRules.IoLatencyFinding("srv", "write", series, T);

        Assert.Equal(expected, read?.Severity);
        Assert.Equal(expected, write?.Severity);
        if (read is not null) Assert.Equal("srv|ioread|", read.Key);
        if (write is not null) Assert.Equal("srv|iowrite|", write.Key);
    }

    [Fact]
    public void Io_latency_ignores_one_slow_window_and_too_little_history()
    {
        List<(DateTime, double)> Every5(params double[] v) => v.Select((x, i) => (Now.AddMinutes(i * 5), x)).ToList();

        Assert.Null(AlertRules.IoLatencyFinding("srv", "read", Every5(300, 10, 300, 300), T));            // a good reading in the period
        Assert.Null(AlertRules.IoLatencyFinding("srv", "read", Every5(300, 300, 300), T));                // 10 minutes of a 15 minute period
        Assert.Null(AlertRules.IoLatencyFinding("srv", "read", Every5(300, 300), T));
        Assert.Null(AlertRules.IoLatencyFinding("srv", "read", [], T));
    }

    [Fact]
    public async Task The_evaluator_raises_connection_and_io_alerts_from_the_history_and_each_can_be_disabled()
    {
        for (var i = 0; i <= 15; i++)
        {
            var t = Now.AddMinutes(-15 + i);
            await _store.AppendAsync("prod", "connections", t, TimeSpan.Zero, CollectionResult.Ok([new Sample("connections_user", null, 800)]));
            if (i % 5 == 0)
                await _store.AppendAsync("prod", "io_latency", t, TimeSpan.Zero, CollectionResult.Ok(
                [
                    new Sample("io_read_latency_ms", null, 80), new Sample("io_write_latency_ms", null, 10),
                    new Sample("io_read_latency_ms", "MyDb", 200),   // per-database rows never alert
                ]));
        }

        var all = await new AlertEvaluator(_store, T, new HashSet<string>()).EvaluateAsync("prod", Now, CancellationToken.None);

        Assert.Equal(Severity.Warning, Assert.Single(all, f => f.Key == "prod|connections|").Severity);
        Assert.Equal(Severity.Warning, Assert.Single(all, f => f.Key == "prod|ioread|").Severity);
        Assert.DoesNotContain(all, f => f.Key == "prod|iowrite|");                                       // write latency is fine

        var noConn = await new AlertEvaluator(_store, T, new HashSet<string> { AlertRules.RuleConnections }).EvaluateAsync("prod", Now, CancellationToken.None);
        Assert.DoesNotContain(noConn, f => f.Key == "prod|connections|");
        Assert.Contains(noConn, f => f.Key == "prod|ioread|");

        var noIo = await new AlertEvaluator(_store, T, new HashSet<string> { AlertRules.RuleIoLatency }).EvaluateAsync("prod", Now, CancellationToken.None);
        Assert.DoesNotContain(noIo, f => f.Key == "prod|ioread|");
        Assert.Contains(noIo, f => f.Key == "prod|connections|");
    }

    [Fact]
    public void The_connection_and_io_limits_must_be_consistent()
    {
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { ConnectionsWarnCount = 1500, ConnectionsCriticalCount = 500 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { ConnectionsSustainedMinutes = 1 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { IoLatencyWarnMs = 200, IoLatencyCriticalMs = 50 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { IoLatencySustainedMinutes = 4 }.Validate());
    }

    // ---------------------------------------------------------------- the whole pass over a real store

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-perfalert-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteHistoryStore(_file);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    [Fact]
    public async Task The_evaluator_reads_the_history_and_raises_memory_and_deadlock_alerts_and_each_can_be_disabled()
    {
        for (var i = 0; i <= 12; i++)
        {
            var t = Now.AddMinutes(-12 + i);
            await _store.AppendAsync("prod", "memory", t, TimeSpan.Zero, CollectionResult.Ok(
                [new Sample("memory_ple_seconds", null, 90), new Sample("memory_grants_pending", null, 3)]));
            await _store.AppendAsync("prod", "deadlocks", t, TimeSpan.Zero, CollectionResult.Ok(
                [new Sample("deadlocks_total", null, 50 + i)]));
        }

        var all = await new AlertEvaluator(_store, T, new HashSet<string>()).EvaluateAsync("prod", Now, CancellationToken.None);

        Assert.Contains(all, f => f.Key == "prod|ple|");
        Assert.Contains(all, f => f.Key == "prod|grants|");
        var deadlocks = Assert.Single(all, f => f.Key == "prod|deadlocks|");
        Assert.Contains("12 deadlock(s)", deadlocks.Detail);

        var noMemory = await new AlertEvaluator(_store, T, new HashSet<string> { AlertRules.RuleMemory }).EvaluateAsync("prod", Now, CancellationToken.None);
        Assert.DoesNotContain(noMemory, f => f.Key is "prod|ple|" or "prod|grants|");
        Assert.Contains(noMemory, f => f.Key == "prod|deadlocks|");

        var noDeadlocks = await new AlertEvaluator(_store, T, new HashSet<string> { AlertRules.RuleDeadlocks }).EvaluateAsync("prod", Now, CancellationToken.None);
        Assert.DoesNotContain(noDeadlocks, f => f.Key == "prod|deadlocks|");
    }

    [Fact]
    public async Task The_new_limits_must_be_consistent_and_the_defaults_are_valid()
    {
        new AlertThresholds().Validate();

        Assert.Throws<InvalidDataException>(() => new AlertThresholds { BlockingWarnSeconds = 300, BlockingCriticalSeconds = 60 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { DeadlockWarnCount = 20, DeadlockCriticalCount = 3 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { MemoryPleWarnSeconds = 0 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { MemoryPleSustainedMinutes = 1 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { MemoryGrantsPendingMinutes = 500 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { DeadlockWindowHours = 0 }.Validate());
        await Task.CompletedTask;
    }
}
