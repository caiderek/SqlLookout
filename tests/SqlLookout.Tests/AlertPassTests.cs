using Microsoft.Data.Sqlite;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public sealed class AlertPassTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-pass-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Recorder(bool delivers) : IAlertSender
    {
        public List<string> Bodies { get; } = [];
        public bool Delivers => delivers;
        public Task SendAsync(string subject, string body, CancellationToken ct)
        {
            Bodies.Add(body);
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        _store = new SqliteHistoryStore(_file);
        await _store.InitializeAsync();
        await _store.AppendAsync("prod", CollectorCatalog.Connectivity, Now, TimeSpan.Zero, CollectionResult.Ok([]));
        await _store.AppendAsync("prod", "database_state", Now, TimeSpan.Zero, CollectionResult.Ok([new Sample("db_state", "Sales", 4, "SUSPECT")]));
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private static SqlLookoutConfig Config() => new()
    {
        Instances = [new InstanceConfig { Name = "prod", ConnectionString = "Server=x;Integrated Security=true" }],
        Alerts = new AlertSettings { SummaryTime = null },
    };

    [Fact]
    public async Task Without_email_nothing_is_recorded_as_told_and_everything_is_mailed_once_email_exists()
    {
        var none = new Recorder(delivers: false);
        var loop = new MonitorLoop(Config(), _store, none, _ => { });
        await loop.RunAlertPassAsync(Now, CancellationToken.None);
        await loop.RunAlertPassAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.Empty(none.Bodies);
        Assert.Empty(await _store.GetActiveAlertsAsync());          // not counted as "already told"

        // Email gets configured later: the problem that is still there is mailed now.
        var email = new Recorder(delivers: true);
        await new MonitorLoop(Config(), _store, email, _ => { }).RunAlertPassAsync(Now.AddMinutes(2), CancellationToken.None);

        var mail = Assert.Single(email.Bodies);
        Assert.Contains("prod: Database Sales is SUSPECT", mail);
        Assert.Single(await _store.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task Deleted_instances_alerts_are_closed_quietly()
    {
        var email = new Recorder(delivers: true);
        await new MonitorLoop(Config(), _store, email, _ => { }).RunAlertPassAsync(Now, CancellationToken.None);
        Assert.Single(await _store.GetActiveAlertsAsync());

        var noInstances = new SqlLookoutConfig { Alerts = new AlertSettings { SummaryTime = null } };
        await new MonitorLoop(noInstances, _store, email, _ => { }).RunAlertPassAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.Empty(await _store.GetActiveAlertsAsync());
        Assert.Single(email.Bodies);                                  // no "resolved" mail for a server that was removed
    }
}
