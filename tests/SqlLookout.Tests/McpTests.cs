using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlLookout.Core;
using SqlLookout.Mcp;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public sealed class McpTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqllookout-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private string File_ => Path.Combine(_dir, "sqllookout.db");
    private SqliteHistoryStore _writer = null!;
    private SqliteHistoryStore _reader = null!;
    private MonitorQueries _q = null!;
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _writer = new SqliteHistoryStore(File_);
        await _writer.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_reader is not null) await _reader.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task OpenReaderAsync()
    {
        _reader = new SqliteHistoryStore(File_, readOnly: true);
        await _reader.InitializeAsync();
        _q = new MonitorQueries(_reader);
    }

    private static CollectionResult Ok(params Sample[] s) => CollectionResult.Ok(s);

    private async Task SeedAsync()
    {
        await _writer.AppendAsync("prod", CollectorCatalog.Connectivity, Now, TimeSpan.Zero, Ok());
        await _writer.AppendAsync("prod", "wait_stats", Now.AddMinutes(-10), TimeSpan.Zero, Ok(new Sample("wait_ms_per_sec", "PAGEIOLATCH_SH", 50), new Sample("wait_ms_per_sec", "OLD_WAIT", 7)));
        await _writer.AppendAsync("prod", "wait_stats", Now, TimeSpan.Zero, Ok(new Sample("wait_ms_per_sec", "PAGEIOLATCH_SH", 80)));
        await _writer.AppendAsync("prod", "cpu", Now, TimeSpan.Zero, Ok(new Sample("cpu_total_percent", null, 42)));
        await _writer.AppendAsync("dev", CollectorCatalog.Connectivity, Now.AddMinutes(-5), TimeSpan.Zero,
            new CollectionResult(CollectionStatus.Failed, [], "SQL error 18456"));
        await _writer.UpsertAlertAsync(new AlertRecord("prod|disk|D:\\", "prod", "Warning", "Disk D:\\ is running low", "9 GB free", Now.AddHours(-3), Now, null));
        await _writer.UpsertAlertAsync(new AlertRecord("prod|backup|Sales", "prod", "Critical", "No full backup of Sales", "none", Now.AddHours(-2), Now, null));
        await _writer.UpsertAlertAsync(new AlertRecord("dev|old|x", "dev", "Critical", "Resolved one", "gone", Now.AddHours(-9), Now, null));
        await _writer.ResolveAlertAsync("dev|old|x", Now);
    }

    [Fact]
    public async Task Read_only_store_reads_but_cannot_write_or_set_up_a_file()
    {
        await SeedAsync();
        await OpenReaderAsync();

        Assert.Equal(["dev", "prod"], await _reader.ListInstancesAsync());
        await Assert.ThrowsAsync<SqliteException>(() =>
            _reader.AppendAsync("prod", "cpu", Now, TimeSpan.Zero, Ok(new Sample("cpu_total_percent", null, 1))));
        await Assert.ThrowsAsync<SqliteException>(() => _reader.SetStateAsync("k", "v"));

        // A file that SqlLookout never initialized is refused instead of being turned into a database.
        var stranger = Path.Combine(_dir, "other.db");
        await using (var c = new SqliteConnection($"Data Source={stranger}")) { await c.OpenAsync(); }
        await using var bad = new SqliteHistoryStore(stranger, readOnly: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.InitializeAsync());
        // A missing file is not created either.
        var missing = Path.Combine(_dir, "missing.db");
        await using var none = new SqliteHistoryStore(missing, readOnly: true);
        await Assert.ThrowsAsync<SqliteException>(() => none.InitializeAsync());
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task Instances_show_reachability_and_only_open_problems()
    {
        await SeedAsync();
        await OpenReaderAsync();
        var json = JsonDocument.Parse(MonitorQueries.ToJson(await _q.ListInstancesAsync(default))).RootElement.GetProperty("instances");

        var dev = json.EnumerateArray().Single(i => i.GetProperty("instance").GetString() == "dev");
        var prod = json.EnumerateArray().Single(i => i.GetProperty("instance").GetString() == "prod");
        Assert.False(dev.GetProperty("reachable").GetBoolean());
        Assert.True(prod.GetProperty("reachable").GetBoolean());
        Assert.Equal(0, dev.GetProperty("activeCritical").GetInt32());     // its only alert is resolved
        Assert.Equal(1, prod.GetProperty("activeCritical").GetInt32());
        Assert.Equal(1, prod.GetProperty("activeWarning").GetInt32());
    }

    [Fact]
    public async Task Problems_list_critical_first_and_filter_by_instance()
    {
        await SeedAsync();
        await OpenReaderAsync();

        var all = JsonDocument.Parse(MonitorQueries.ToJson(await _q.GetProblemsAsync(null, default))).RootElement;
        Assert.Equal(2, all.GetProperty("count").GetInt32());
        Assert.Equal("Critical", all.GetProperty("problems")[0].GetProperty("severity").GetString());

        var dev = JsonDocument.Parse(MonitorQueries.ToJson(await _q.GetProblemsAsync("dev", default))).RootElement;
        Assert.Equal(0, dev.GetProperty("count").GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(() => _q.GetProblemsAsync("nope", default));
    }

    [Fact]
    public async Task Overview_leaves_out_values_the_collector_no_longer_reports()
    {
        await SeedAsync();
        await OpenReaderAsync();
        var root = JsonDocument.Parse(MonitorQueries.ToJson(await _q.GetOverviewAsync("prod", default))).RootElement;

        var waits = root.GetProperty("collectors").EnumerateArray().Single(c => c.GetProperty("collector").GetString() == "wait_stats");
        var subjects = waits.GetProperty("metrics").EnumerateArray().Select(m => m.GetProperty("subject").GetString()).ToList();
        Assert.Equal(["PAGEIOLATCH_SH"], subjects);       // OLD_WAIT was only in the older run
        Assert.Equal(80, waits.GetProperty("metrics")[0].GetProperty("value").GetDouble());
        Assert.Equal("Ok", waits.GetProperty("lastStatus").GetString());
    }

    [Fact]
    public async Task History_summarises_the_period_and_refuses_bad_arguments()
    {
        for (var i = 0; i < 10; i++)
            await _writer.AppendAsync("prod", "cpu", Now.AddMinutes(-90 + i * 10), TimeSpan.Zero, Ok(new Sample("cpu_total_percent", null, 10 * (i + 1))));
        await _writer.AppendAsync("prod", CollectorCatalog.Connectivity, Now, TimeSpan.Zero, Ok());
        await OpenReaderAsync();

        var root = JsonDocument.Parse(MonitorQueries.ToJson(await _q.GetMetricHistoryAsync("prod", "cpu_total_percent", null, 2, 100, Now, default))).RootElement;
        Assert.Equal(10, root.GetProperty("points").GetInt32());
        Assert.Equal(10, root.GetProperty("min").GetDouble());
        Assert.Equal(100, root.GetProperty("max").GetDouble());
        Assert.Equal(55, root.GetProperty("average").GetDouble());
        Assert.Equal(100, root.GetProperty("latest").GetDouble());

        // Only the last 30 minutes: samples at -30, -20, -10 minutes ... values 70, 80, 90 and 100 (at -0? last is -0).
        var recent = JsonDocument.Parse(MonitorQueries.ToJson(await _q.GetMetricHistoryAsync("prod", "cpu_total_percent", null, 0.5, 100, Now, default))).RootElement;
        Assert.True(recent.GetProperty("points").GetInt32() < 10);
        Assert.Equal(100, recent.GetProperty("latest").GetDouble());

        var none = JsonDocument.Parse(MonitorQueries.ToJson(await _q.GetMetricHistoryAsync("prod", "no_such", null, 24, 100, Now, default))).RootElement;
        Assert.Equal(0, none.GetProperty("points").GetInt32());

        await Assert.ThrowsAsync<ArgumentException>(() => _q.GetMetricHistoryAsync("prod", "cpu_total_percent", null, 0, 100, Now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _q.GetMetricHistoryAsync("prod", "cpu_total_percent", null, MonitorQueries.MaxHours + 1, 100, Now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _q.GetMetricHistoryAsync("prod", "cpu_total_percent", null, 24, 1, Now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _q.GetMetricHistoryAsync("prod", "cpu_total_percent", null, 24, MonitorQueries.MaxPoints + 1, Now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _q.GetMetricHistoryAsync("prod", "", null, 24, 100, Now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _q.GetMetricHistoryAsync("nope", "cpu_total_percent", null, 24, 100, Now, default));
    }

    [Fact]
    public void Downsample_keeps_first_and_last_and_never_exceeds_the_limit()
    {
        var series = Enumerable.Range(0, 1000).Select(i => (Now.AddMinutes(i), (double)i)).ToList();
        var shown = MonitorQueries.Downsample(series, 50);
        Assert.Equal(50, shown.Count);
        Assert.Equal(0, shown[0].Value);
        Assert.Equal(999, shown[^1].Value);
        Assert.Equal(shown.OrderBy(p => p.Utc).ToList(), shown);
        Assert.Equal(series.Take(5).ToList(), MonitorQueries.Downsample(series.Take(5).ToList(), 50));
    }

    [Fact]
    public async Task The_real_program_serves_the_four_tools_over_stdio_while_the_monitor_keeps_writing()
    {
        await SeedAsync();
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SqlLookout.Cli", "bin",
            AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug", "net10.0", "sqllookout.dll"));
        Assert.True(File.Exists(dll), $"Program not built: {dll}");

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sqllookout-under-test",
            Command = "dotnet",
            Arguments = [dll, "mcp", "--data", _dir],
        }));

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToList();
        Assert.Equal(["get_metric_history", "get_overview", "get_problems", "list_instances"], tools);
        Assert.All(await client.ListToolsAsync(), t => Assert.Equal(true, t.ProtocolTool.Annotations?.ReadOnlyHint));

        // The monitor writes after the server has started: the server must see it and the write must not be blocked.
        await _writer.UpsertAlertAsync(new AlertRecord("prod|jobfail|Nightly", "prod", "Critical", "Agent job Nightly failed", "x", Now, Now, null));
        var problems = JsonDocument.Parse(Text(await client.CallToolAsync("get_problems", new Dictionary<string, object?> { ["instance"] = "prod" }))).RootElement;
        Assert.Equal(3, problems.GetProperty("count").GetInt32());

        var overview = Text(await client.CallToolAsync("get_overview", new Dictionary<string, object?> { ["instance"] = "prod" }));
        Assert.Contains("cpu_total_percent", overview);

        var bad = await client.CallToolAsync("get_overview", new Dictionary<string, object?> { ["instance"] = "nope" });
        Assert.Equal(true, bad.IsError);
        Assert.Contains("Unknown instance", Text(bad));
    }

    private static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
