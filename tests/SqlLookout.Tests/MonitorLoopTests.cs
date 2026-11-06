using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public sealed class MonitorLoopTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-mon-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;
    private readonly List<string> _log = [];

    public async Task InitializeAsync()
    {
        _store = new SqliteHistoryStore(_file);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private MonitorLoop Loop(params InstanceConfig[] instances) =>
        new(new SqlLookoutConfig { Instances = [.. instances] }, _store, new ConsoleAlertSender(), _log.Add);

    private static string LocalConnection =>
        Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
        ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";

    [Fact]
    public async Task Reachable_instance_records_connectivity_and_every_collector()
    {
        var instance = new InstanceConfig { Name = "ok", ConnectionString = LocalConnection };
        var failures = await Loop(instance).CollectInstanceOnceAsync(instance, true, [], new ServerInfoCache(TimeSpan.FromHours(1)), CancellationToken.None);

        Assert.Equal(0, failures);
        Assert.Equal(CollectionStatus.Ok, (await _store.GetRecentRunsAsync("ok", CollectorCatalog.Connectivity, 1)).Single().Status);
        foreach (var c in CollectorCatalog.All())
            Assert.Single(await _store.GetRecentRunsAsync("ok", c.Name, 5));
    }

    [Fact]
    public async Task Unreachable_instance_records_a_failed_connection_without_leaking_the_connection_string()
    {
        var instance = new InstanceConfig
        {
            Name = "down",
            ConnectionString = "Server=localhost,1;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=2",
        };
        var lastRun = new Dictionary<string, DateTime>();
        var failures = await Loop(instance).CollectInstanceOnceAsync(instance, true, lastRun, new ServerInfoCache(TimeSpan.FromHours(1)), CancellationToken.None);

        Assert.Equal(1, failures);
        var run = (await _store.GetRecentRunsAsync("down", CollectorCatalog.Connectivity, 1)).Single();
        Assert.Equal(CollectionStatus.Failed, run.Status);
        Assert.DoesNotContain("localhost", run.Message ?? "");
        Assert.DoesNotContain("localhost", string.Join("|", _log));
        Assert.Empty(await _store.GetLatestAsync("down"));
        // Back-off: every collector is marked as just tried, so none is due again right away.
        Assert.Equal(CollectorCatalog.All().Count, lastRun.Count);
    }

    [Fact]
    public async Task Collectors_that_are_not_due_are_skipped_and_disabled_ones_never_run()
    {
        var instance = new InstanceConfig { Name = "ok", ConnectionString = LocalConnection };
        var config = new SqlLookoutConfig { Instances = [instance], DisabledCollectors = ["agent_jobs", "dbcc_last_good"] };
        var loop = new MonitorLoop(config, _store, new ConsoleAlertSender(), _log.Add);
        var lastRun = new Dictionary<string, DateTime>();
        var cache = new ServerInfoCache(TimeSpan.FromHours(1));

        await loop.CollectInstanceOnceAsync(instance, false, lastRun, cache, CancellationToken.None);
        await loop.CollectInstanceOnceAsync(instance, false, lastRun, cache, CancellationToken.None);

        Assert.Single(await _store.GetRecentRunsAsync("ok", "backup_age", 5));     // second pass: not due yet
        Assert.Empty(await _store.GetRecentRunsAsync("ok", "agent_jobs", 5));
        Assert.Empty(await _store.GetRecentRunsAsync("ok", "dbcc_last_good", 5));
    }
}
