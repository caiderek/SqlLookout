using SqlLookout.Core;
using SqlLookout.Core.Collectors;

namespace SqlLookout.Tests;

/// <summary>The parts of the performance collectors that are decisions, tested without a server.</summary>
public class PerformanceLogicTests
{
    [Fact]
    public void Head_blockers_skip_sessions_that_are_themselves_blocked()
    {
        // 51 blocks 52, 52 blocks 53, 60 blocks 61: the heads are 51 and 60, not 52.
        var blocked = new Dictionary<int, (int, long)> { [52] = (51, 10), [53] = (52, 5), [61] = (60, 1) };
        Assert.Equal([51, 60], BlockingCollector.HeadBlockers(blocked));
    }

    [Fact]
    public void Head_blockers_are_empty_when_nothing_is_blocked_or_all_wait_in_a_cycle()
    {
        Assert.Empty(BlockingCollector.HeadBlockers(new Dictionary<int, (int, long)>()));
        Assert.Empty(BlockingCollector.HeadBlockers(new Dictionary<int, (int, long)> { [1] = (2, 1), [2] = (1, 1) }));
    }

    [Fact]
    public void Two_sessions_blocked_by_one_blocker_name_it_once()
    {
        var blocked = new Dictionary<int, (int, long)> { [52] = (51, 10), [53] = (51, 5) };
        Assert.Equal([51], BlockingCollector.HeadBlockers(blocked));
    }

    [Fact]
    public void Io_latency_is_stall_per_operation_over_the_window()
    {
        var first = new Dictionary<string, IoLatencyCollector.Counters> { ["Sales"] = new(100, 1000, 50, 500) };
        var second = new Dictionary<string, IoLatencyCollector.Counters> { ["Sales"] = new(110, 1050, 60, 800) };
        var samples = IoLatencyCollector.Diff(first, second);

        Assert.Equal(5.0, samples.Single(s => s.Metric == "io_read_latency_ms" && s.Subject == "Sales").Value);    // 50 ms over 10 reads
        Assert.Equal(30.0, samples.Single(s => s.Metric == "io_write_latency_ms" && s.Subject == "Sales").Value);  // 300 ms over 10 writes
        Assert.Equal(5.0, samples.Single(s => s.Metric == "io_read_latency_ms" && s.Subject is null).Value);
    }

    [Fact]
    public void Io_latency_skips_idle_databases_new_databases_and_counters_that_went_backwards()
    {
        var first = new Dictionary<string, IoLatencyCollector.Counters>
        {
            ["Idle"] = new(5, 50, 5, 50),
            ["Restarted"] = new(1000, 9000, 1000, 9000),
            ["Busy"] = new(0, 0, 0, 0),
        };
        var second = new Dictionary<string, IoLatencyCollector.Counters>
        {
            ["Idle"] = new(5, 50, 5, 50),                    // no operations in the window
            ["Restarted"] = new(3, 10, 3, 10),               // went backwards
            ["New"] = new(10, 100, 10, 100),                 // no first reading
            ["Busy"] = new(4, 8, 0, 0),                      // reads only
        };
        var samples = IoLatencyCollector.Diff(first, second);

        Assert.DoesNotContain(samples, s => s.Subject is "Idle" or "Restarted" or "New");
        Assert.Equal(2.0, samples.Single(s => s.Metric == "io_read_latency_ms" && s.Subject == "Busy").Value);
        Assert.DoesNotContain(samples, s => s.Metric == "io_write_latency_ms");   // no writes anywhere, so no total either
        Assert.Equal(2.0, samples.Single(s => s.Metric == "io_read_latency_ms" && s.Subject is null).Value);   // the restarted one is not in the total
    }
}
