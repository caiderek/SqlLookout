using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Service;

/// <summary>
/// The scheduling loop: one worker per instance (so one slow server never delays another), plus one alert loop.
/// The configuration is read again on every pass, so instances added, removed or edited in the web UI take effect
/// within seconds without a restart. A collector run never throws out of here; failures are recorded as runs and
/// surfaced by the alert rules.
/// </summary>
public sealed class MonitorLoop(Func<SqlLookoutConfig> getConfig, IHistoryStore store, Func<SqlLookoutConfig, IAlertSender> senderFor, Action<string> log)
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServerInfoRefresh = TimeSpan.FromHours(1);
    // Far-away servers (another country, VPN) can need well over 15 s for the handshake and login; a failed attempt only
    // delays that one instance's own worker. SQLLOOKOUT_CONNECT_TIMEOUT (seconds) overrides it.
    private static readonly int ConnectTimeoutSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("SQLLOOKOUT_CONNECT_TIMEOUT"), out var t) && t is >= 1 and <= 300 ? t : 45;

    /// <summary>Fixed configuration (the JSON file mode and the tests).</summary>
    public MonitorLoop(SqlLookoutConfig config, IHistoryStore store, IAlertSender sender, Action<string> log)
        : this(() => config, store, _ => sender, log)
    {
    }

    private sealed record Worker(string Fingerprint, CancellationTokenSource Cts, Task Task);

    public async Task RunAsync(CancellationToken ct)
    {
        var workers = new Dictionary<string, Worker>(StringComparer.OrdinalIgnoreCase);
        var alertLoop = Task.Run(() => AlertLoopAsync(ct), ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Reconcile(workers, ct);
                await DelayAsync(Tick, ct);
            }
        }
        finally
        {
            foreach (var w in workers.Values) w.Cts.Cancel();
            try { await Task.WhenAll(workers.Values.Select(w => w.Task).Append(alertLoop)); }
            catch (OperationCanceledException) { }
            foreach (var w in workers.Values) w.Cts.Dispose();
        }
    }

    /// <summary>Starts a worker for every enabled instance that has none, restarts one whose connection changed, stops removed ones.</summary>
    private void Reconcile(Dictionary<string, Worker> workers, CancellationToken ct)
    {
        var wanted = new Dictionary<string, (InstanceConfig Instance, string Fingerprint)>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in getConfig().Instances.Where(i => i.Enabled))
        {
            try { wanted[instance.Name] = (instance, Fingerprint(instance)); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                log($"{instance.Name}: cannot read its connection settings ({ex.GetType().Name})");
            }
        }

        foreach (var name in workers.Keys.ToList())
        {
            if (wanted.TryGetValue(name, out var w) && w.Fingerprint == workers[name].Fingerprint) continue;
            workers[name].Cts.Cancel();
            workers.Remove(name);
        }

        foreach (var (name, (instance, fingerprint)) in wanted)
        {
            if (workers.ContainsKey(name)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            workers[name] = new Worker(fingerprint, cts, Task.Run(() => InstanceLoopAsync(instance, cts.Token), cts.Token));
        }
    }

    private static string Fingerprint(InstanceConfig instance) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance.ResolveConnectionString())));

    /// <summary>One pass over one instance: connect, run every collector that is due (all of them when force is true).</summary>
    public async Task<int> CollectInstanceOnceAsync(InstanceConfig instance, bool force, Dictionary<string, DateTime> lastRun,
        ServerInfoCache cache, CancellationToken ct)
    {
        var config = getConfig();
        var disabled = config.DisabledCollectors.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var due = CollectorCatalog.All()
            .Where(c => !disabled.Contains(c.Name))
            .Where(c => force || !lastRun.TryGetValue(c.Name, out var last) || DateTime.UtcNow - last >= IntervalFor(config, c))
            .ToList();
        if (due.Count == 0) return 0;

        var failures = 0;
        var connectStart = DateTime.UtcNow;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        SqlConnection? connection = null;
        ServerInfo server;
        try
        {
            var builder = new SqlConnectionStringBuilder(instance.ResolveConnectionString())
            {
                ConnectTimeout = ConnectTimeoutSeconds,
                ConnectRetryCount = 0,
                ApplicationName = "SqlLookout",
            };
            connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            server = await cache.GetAsync(instance.Name, connection, ct);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or FormatException or ArgumentException)
        {
            if (connection is not null) await connection.DisposeAsync();
            // Never log the exception text: connection errors can echo parts of the connection string.
            var message = ex is SqlException sql ? $"SQL error {sql.Number}" : ex.GetType().Name;
            await store.AppendAsync(instance.Name, CollectorCatalog.Connectivity, connectStart, timer.Elapsed,
                new CollectionResult(CollectionStatus.Failed, [], message), CancellationToken.None);
            log($"{instance.Name}: cannot connect ({message})");
            // Back off: the next attempt waits for the collectors' own intervals (a minute or more by default),
            // so a server that is down is not hit every few seconds.
            foreach (var c in due) lastRun[c.Name] = connectStart;
            return 1;
        }

        await using (connection)
        {
            await store.AppendAsync(instance.Name, CollectorCatalog.Connectivity, connectStart, timer.Elapsed,
                CollectionResult.Ok([]), CancellationToken.None);

            foreach (var collector in due)
            {
                ct.ThrowIfCancellationRequested();
                var started = DateTime.UtcNow;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await CollectorRunner.RunAsync(collector, connection, server, ct);
                sw.Stop();
                await store.AppendAsync(instance.Name, collector.Name, started, sw.Elapsed, result, CancellationToken.None);
                lastRun[collector.Name] = started;
                if (result.Status is CollectionStatus.Failed or CollectionStatus.NoPermission)
                {
                    failures++;
                    log($"{instance.Name}: {collector.Name} {result.Status} ({result.Message})");
                }
            }
        }
        return failures;
    }

    private async Task InstanceLoopAsync(InstanceConfig instance, CancellationToken ct)
    {
        var lastRun = new Dictionary<string, DateTime>();
        var cache = new ServerInfoCache(ServerInfoRefresh);
        while (!ct.IsCancellationRequested)
        {
            try { await CollectInstanceOnceAsync(instance, force: false, lastRun, cache, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log($"{instance.Name}: unexpected {ex.GetType().Name}"); }
            await DelayAsync(Tick, ct);
        }
    }

    private async Task AlertLoopAsync(CancellationToken ct)
    {
        var lastPurge = DateTime.MinValue;

        // Give the first collection round a head start so the first evaluation sees data.
        await DelayAsync(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                await RunAlertPassAsync(now, ct);

                if (now - lastPurge > TimeSpan.FromHours(24))
                {
                    var removed = await store.PurgeOlderThanAsync(now.AddDays(-getConfig().RetentionDays), ct);
                    lastPurge = now;
                    if (removed > 0) log($"purged {removed} old run(s)");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log($"alert loop: {ex.GetType().Name}: {ex.Message}"); }
            await DelayAsync(TimeSpan.FromSeconds(60), ct);
        }
    }

    /// <summary>One evaluation: find the problems, mail what is new, close what cleared, send the daily summary when due.</summary>
    public async Task RunAlertPassAsync(DateTime nowUtc, CancellationToken ct)
    {
        var config = getConfig();
        var sender = senderFor(config);
        using var language = Loc.Use(config.Language);       // mails and stored alert texts follow the chosen language

        // No way to deliver (no email configured): leave every alert untouched, so that once email is set up the
        // problems that are still there are mailed, instead of having been counted as already told.
        if (!sender.Delivers) return;

        var disabledRules = config.Alerts.DisabledRules.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evaluator = new AlertEvaluator(store, config.Alerts.Thresholds, disabledRules, config.IgnoredFor, config.ThresholdsFor);
        var dispatcher = new AlertDispatcher(store, sender, TimeSpan.FromHours(config.Alerts.RenotifyHours), config.Alerts.Quiet(), TimeSpan.FromMinutes(config.Alerts.ResolveAfterMinutes), config.Alerts.WarningsInSummaryOnly,
            config.Alerts.Digest(), config.Alerts.UrgentKinds.ToHashSet(StringComparer.OrdinalIgnoreCase), config.Alerts.MergeHosts);
        var names = config.Instances.Where(i => i.Enabled).Select(i => i.Name).ToList();

        var findings = new List<Finding>();
        foreach (var name in names) findings.AddRange(await evaluator.EvaluateAsync(name, nowUtc, ct));
        await dispatcher.DispatchAsync(findings, names, nowUtc, DateTime.Now.TimeOfDay, ct);

        // An instance that was deleted or switched off can no longer clear its alerts; close them quietly.
        var known = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in (await store.GetActiveAlertsAsync(ct)).Where(a => !known.Contains(a.Instance)))
            await store.ResolveAlertAsync(stale.Key, nowUtc, ct);

        await MaybeSendSummaryAsync(config, sender, names, findings, nowUtc, ct);
    }

    private async Task MaybeSendSummaryAsync(SqlLookoutConfig config, IAlertSender sender, IReadOnlyList<string> names, IReadOnlyList<Finding> findings, DateTime nowUtc, CancellationToken ct)
    {
        var at = config.Alerts.Summary();
        if (at is null) return;
        var local = DateTime.Now;
        var today = local.ToString("yyyy-MM-dd");
        if (local.TimeOfDay < at.Value || await store.GetStateAsync("summary.last_sent", ct) == today) return;

        var body = await DailySummary.BuildAsync(store, names, nowUtc, findings, ct);
        await sender.SendAsync("[SqlLookout] Daily summary", body, ct);
        await store.SetStateAsync("summary.last_sent", today, ct);
    }

    private static TimeSpan IntervalFor(SqlLookoutConfig config, ICollector c) =>
        config.IntervalsSeconds.TryGetValue(c.Name, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : c.DefaultInterval;

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Remembers each instance's version so the loop does not query it on every pass.</summary>
public sealed class ServerInfoCache(TimeSpan lifetime)
{
    private readonly Dictionary<string, (ServerInfo Info, DateTime At)> _items = [];

    public async Task<ServerInfo> GetAsync(string instance, SqlConnection connection, CancellationToken ct)
    {
        if (_items.TryGetValue(instance, out var hit) && DateTime.UtcNow - hit.At < lifetime) return hit.Info;
        var info = await ServerInfo.ReadAsync(connection, ct);
        _items[instance] = (info, DateTime.UtcNow);
        return info;
    }
}
