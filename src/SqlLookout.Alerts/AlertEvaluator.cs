using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

/// <summary>Gathers what the rules need from the store (latest samples, volume history, recent runs) and runs them.</summary>
public sealed class AlertEvaluator(IHistoryStore store, AlertThresholds commonThresholds, ISet<string> disabledRules,
    Func<string, IReadOnlyList<string>>? ignoredDatabases = null, Func<string, AlertThresholds>? thresholdsFor = null)
{
    public async Task<List<Finding>> EvaluateAsync(string instance, DateTime nowUtc, CancellationToken ct = default)
    {
        var thresholds = thresholdsFor?.Invoke(instance) ?? commonThresholds;
        var latest = await store.GetLatestAsync(instance, ct);
        var findings = AlertRules.Evaluate(instance, latest, thresholds, disabledRules, ignoredDatabases?.Invoke(instance));

        if (!disabledRules.Contains(AlertRules.RuleDisk))
        {
            var since = nowUtc.AddHours(-thresholds.DiskFallWindowHours);
            foreach (var volume in latest.Where(s => s.Sample.Metric == "disk_free_bytes").Select(s => s.Sample.Subject!).Distinct())
            {
                var series = await store.GetSeriesAsync(instance, "disk_free_bytes", volume, since, ct);
                var fall = AlertRules.DiskFallFinding(instance, volume, series, thresholds);
                if (fall is not null) findings.Add(fall);
            }
        }

        if (!disabledRules.Contains(AlertRules.RuleCpu) && latest.Any(s => s.Sample.Metric == "cpu_total_percent"))
        {
            var series = await store.GetSeriesAsync(instance, "cpu_total_percent", null, nowUtc.AddMinutes(-thresholds.CpuSustainedMinutes), ct);
            var cpu = AlertRules.CpuFinding(instance, series, thresholds);
            if (cpu is not null) findings.Add(cpu);
        }

        if (!disabledRules.Contains(AlertRules.RuleMemory))
        {
            if (latest.Any(s => s.Sample.Metric == "memory_ple_seconds"))
            {
                var ple = await store.GetSeriesAsync(instance, "memory_ple_seconds", null, nowUtc.AddMinutes(-thresholds.MemoryPleSustainedMinutes), ct);
                if (AlertRules.PageLifeExpectancyFinding(instance, ple, thresholds) is { } low) findings.Add(low);
            }
            if (latest.Any(s => s.Sample.Metric == "memory_grants_pending"))
            {
                var grants = await store.GetSeriesAsync(instance, "memory_grants_pending", null, nowUtc.AddMinutes(-thresholds.MemoryGrantsPendingMinutes), ct);
                if (AlertRules.MemoryGrantsFinding(instance, grants, thresholds) is { } waiting) findings.Add(waiting);
            }
        }

        if (!disabledRules.Contains(AlertRules.RuleDeadlocks) && latest.Any(s => s.Sample.Metric == "deadlocks_total"))
        {
            var totals = await store.GetSeriesAsync(instance, "deadlocks_total", null, nowUtc.AddHours(-thresholds.DeadlockWindowHours), ct);
            if (AlertRules.DeadlockFinding(instance, totals, thresholds) is { } deadlocks) findings.Add(deadlocks);
        }

        if (!disabledRules.Contains(AlertRules.RuleConnections) && latest.Any(s => s.Sample.Metric == "connections_user"))
        {
            var series = await store.GetSeriesAsync(instance, "connections_user", null, nowUtc.AddMinutes(-thresholds.ConnectionsSustainedMinutes), ct);
            if (AlertRules.ConnectionsFinding(instance, series, thresholds) is { } many) findings.Add(many);
        }

        if (!disabledRules.Contains(AlertRules.RuleIoLatency))
        {
            foreach (var (kind, metric) in new[] { ("read", "io_read_latency_ms"), ("write", "io_write_latency_ms") })
            {
                // Latency exists only for the whole-instance row (no subject); an idle window writes no sample at all.
                if (!latest.Any(s => s.Sample.Metric == metric && s.Sample.Subject is null)) continue;
                var series = await store.GetSeriesAsync(instance, metric, null, nowUtc.AddMinutes(-thresholds.IoLatencySustainedMinutes), ct);
                if (AlertRules.IoLatencyFinding(instance, kind, series, thresholds) is { } slow) findings.Add(slow);
            }
        }

        var reach = AlertRules.UnreachableFinding(instance,
            await store.GetRecentRunsAsync(instance, CollectorCatalog.Connectivity, thresholds.UnreachableCriticalAfter, ct), thresholds);
        if (reach is not null) findings.Add(reach);

        foreach (var collector in CollectorCatalog.All())
        {
            var runs = await store.GetRecentRunsAsync(instance, collector.Name, thresholds.CollectorFailingAfter, ct);
            var failing = AlertRules.CollectorFailingFinding(instance, collector.Name, runs, thresholds);
            if (failing is not null) findings.Add(failing);
        }
        return findings;
    }
}
