using SqlLookout.Core.Collectors;

namespace SqlLookout.Core;

public static class CollectorCatalog
{
    /// <summary>Name used for the reachability pseudo-collector (recorded by the monitor, not an ICollector).</summary>
    public const string Connectivity = "connectivity";

    public static IReadOnlyList<ICollector> All() =>
    [
        new BackupAgeCollector(),
        new DiskSpaceCollector(),
        new LogUsageCollector(),
        new DatabaseStateCollector(),
        new FileSizeCollector(),
        new SuspectPagesCollector(),
        new DbccLastGoodCollector(),
        new AgentJobsCollector(),
        new CpuCollector(),
        new MemoryCollector(),
        new ConnectionsCollector(),
        new BlockingCollector(),
        new DeadlockCollector(),
        new IoLatencyCollector(),
        new WaitStatsCollector(),
        new AvailabilityGroupsCollector(),
        new LogShippingCollector(),
        new MirroringCollector(),
        new ExpressLimitCollector(),
        new TempDbCollector(),
        new FailedLoginsCollector(),
    ];
}
