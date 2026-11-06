using System.Net;
using System.Text;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public sealed class HistoryStoreTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-test-{Guid.NewGuid():N}.db");
    private SqliteHistoryStore _store = null!;
    private static readonly DateTime T0 = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

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

    private static AlertRecord Alert(string key, DateTime first, string title = "t") =>
        new(key, "srv", "Critical", title, "d", first, first, null);

    [Fact]
    public async Task A_problem_that_comes_back_is_logged_twice()
    {
        await _store.UpsertAlertAsync(Alert("k", T0));
        await _store.UpsertAlertAsync(Alert("k", T0, "t2"));                  // still open: same row, newest text
        await _store.ResolveAlertAsync("k", T0.AddHours(1));
        await _store.UpsertAlertAsync(Alert("k", T0.AddHours(3)));            // back again

        var log = await _store.GetAlertLogAsync(T0.AddDays(-1), null);

        Assert.Equal(2, log.Count);
        Assert.Equal(T0.AddHours(3), log[0].FirstUtc);                        // newest first
        Assert.Null(log[0].ResolvedUtc);
        Assert.Equal(T0, log[1].FirstUtc);
        Assert.Equal(T0.AddHours(1), log[1].ResolvedUtc);
        Assert.Equal("t2", log[1].Title);
    }

    [Fact]
    public async Task The_log_can_be_cut_by_server_and_by_time_and_is_purged_with_the_samples()
    {
        await _store.UpsertAlertAsync(Alert("a", T0));
        await _store.ResolveAlertAsync("a", T0.AddHours(1));
        await _store.UpsertAlertAsync(new AlertRecord("b", "other", "Warning", "t", "d", T0.AddDays(5), T0.AddDays(5), null));

        Assert.Single(await _store.GetAlertLogAsync(T0.AddDays(-1), "SRV"));                     // name match ignores case
        Assert.Equal(2, (await _store.GetAlertLogAsync(T0.AddDays(-1), null)).Count);
        var late = Assert.Single(await _store.GetAlertLogAsync(T0.AddDays(2), null));             // ended before the cutoff is left out; open stays
        Assert.Equal("b", late.Key);

        await _store.PurgeOlderThanAsync(T0.AddDays(1));
        var left = Assert.Single(await _store.GetAlertLogAsync(T0.AddDays(-30), null));
        Assert.Equal("b", left.Key);                                                              // the open problem is never purged
    }

    [Fact]
    public async Task A_problem_open_before_the_log_existed_gets_a_row_when_it_is_next_refreshed()
    {
        await _store.UpsertAlertAsync(Alert("old", T0));
        // Simulate a data file made by an earlier build: the problem is in Alerts but has no log row.
        await using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_file}"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM AlertLog;";
            await cmd.ExecuteNonQueryAsync();
        }
        await _store.UpsertAlertAsync(Alert("old", T0));
        var row = Assert.Single(await _store.GetAlertLogAsync(T0.AddDays(-1), null));
        Assert.Equal(T0, row.FirstUtc);
    }
}

public sealed class HistoryWebTests : IAsyncLifetime
{
    private WebFixture _web = null!;

    public async Task InitializeAsync() => _web = await WebFixture.StartAsync();
    public async Task DisposeAsync() => await _web.DisposeAsync();

    private async Task AddServerAsync(string name) =>
        await _web.PostAsync("/instances/new", "/instances/new",
            ("name", name), ("server", name.ToUpperInvariant()), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));

    [Fact]
    public async Task The_pages_need_a_sign_in()
    {
        Assert.Equal(HttpStatusCode.Redirect, (await _web.GetAsync("/history")).Status);
        Assert.Equal(HttpStatusCode.Redirect, (await _web.GetAsync("/history.csv")).Status);
        Assert.Equal(HttpStatusCode.Redirect, (await _web.GetAsync("/server/prod/export.csv")).Status);
    }

    [Fact]
    public async Task History_lists_past_problems_and_downloads_them_as_csv()
    {
        await _web.SignInAsAdminAsync();
        await AddServerAsync("prod");
        await AddServerAsync("other");
        var start = DateTime.UtcNow.AddHours(-5);
        await _web.History.UpsertAlertAsync(new AlertRecord("k1", "prod", "Critical", "Database Sales is SUSPECT", "line1\nline \"2\", x", start, start, null));
        await _web.History.ResolveAlertAsync("k1", start.AddHours(2));
        await _web.History.UpsertAlertAsync(new AlertRecord("k2", "prod", "Warning", "=HYPERLINK(\"http://x\")", "d", start.AddHours(3), start.AddHours(3), null));

        var page = await _web.GetAsync("/history");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("Database Sales is SUSPECT", page.Body);
        Assert.Contains("still open", page.Body);
        Assert.Contains("/history.csv?range=7d", page.Body);
        Assert.Contains("History</a>", page.Body);                                    // in the top menu

        var csv = await _web.GetAsync("/history.csv");
        Assert.Equal("text/csv", csv.Response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sqllookout-problems-", csv.Response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await csv.Response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), "UTF-8 byte-order mark so Excel reads Chinese text");
        Assert.Contains("server,severity,problem,detail,started_utc,ended_utc,minutes", csv.Body);
        Assert.Contains("\"line1\nline \"\"2\"\", x\"", csv.Body);                      // quoting
        Assert.Contains("'=HYPERLINK(", csv.Body);                                     // not run as a formula
        Assert.Contains(",120\r\n", csv.Body);                                         // k1 lasted 120 minutes

        // Filtering by another server hides them; an unknown name means all servers.
        Assert.DoesNotContain("Database Sales is SUSPECT", (await _web.GetAsync("/history?server=other")).Body);
        Assert.Contains("No problems recorded", (await _web.GetAsync("/history?server=other")).Body);
        Assert.Contains("Database Sales is SUSPECT", (await _web.GetAsync("/history?server=nope")).Body);
        Assert.DoesNotContain("Database Sales is SUSPECT", (await _web.GetAsync("/history.csv?server=other")).Body);
    }

    [Fact]
    public async Task Performance_numbers_download_as_one_row_per_time()
    {
        await _web.SignInAsAdminAsync();
        await AddServerAsync("prod");
        var t = DateTime.UtcNow.AddMinutes(-10);
        await _web.History.AppendAsync("prod", "cpu", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("cpu_total_percent", null, 41.5), new Sample("cpu_sql_percent", null, 30)]));
        await _web.History.AppendAsync("prod", "cpu", t.AddMinutes(1), TimeSpan.Zero, CollectionResult.Ok([new Sample("cpu_total_percent", null, 50)]));

        var csv = await _web.GetAsync("/server/prod/export.csv?range=24h");
        var lines = csv.Body.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("time_utc,cpu_total_percent,cpu_sql_percent,", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains(",41.5,30,", lines[1]);
        Assert.Contains(",50,,", lines[2]);                                             // second run has no SQL CPU: empty cell
        Assert.Equal(HttpStatusCode.NotFound, (await _web.GetAsync("/server/nope/export.csv")).Status);
        Assert.Contains("/server/prod/export.csv?range=24h", (await _web.GetAsync("/server/prod?tab=performance")).Body);
    }

    [Fact]
    public async Task Dashboard_groups_problems_by_kind_and_can_show_only_one_kind_or_only_critical()
    {
        await _web.SignInAsAdminAsync();
        await AddServerAsync("prod");
        var t = DateTime.UtcNow;
        await _web.History.AppendAsync("prod", CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync("prod", "database_state", t, TimeSpan.Zero, CollectionResult.Ok([new Sample("db_state", "Broken", 4, "SUSPECT")]));
        await _web.History.AppendAsync("prod", "backup_age", t, TimeSpan.Zero, CollectionResult.Ok([new Sample("last_full_backup_age_min", "Sales", 60 * 60)]));
        await _web.History.AppendAsync("prod", "disk_space", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("disk_total_bytes", "D:\\", 1000L * 1024 * 1024 * 1024), new Sample("disk_free_bytes", "D:\\", 20L * 1024 * 1024 * 1024)]));
        for (var m = 9; m >= 0; m -= 3)   // page life stays low for the whole 10-minute window
            await _web.History.AppendAsync("prod", "memory", t.AddMinutes(-m), TimeSpan.Zero, CollectionResult.Ok(
                [new Sample("memory_ple_seconds", null, 12), new Sample("memory_os_state", null, 3, "Available physical memory is low"), new Sample("memory_used_mb", null, 3584), new Sample("memory_target_mb", null, 4096)]));

        var all = (await _web.GetAsync("/")).Body;
        Assert.DoesNotContain("Database Broken is SUSPECT", all);                  // the list is on its own page now
        Assert.Contains("Database Broken is SUSPECT", (await _web.GetAsync("/problems")).Body);
        Assert.Contains("href=\"/problems?kind=disk\"", all);
        Assert.Contains("href=\"/problems?kind=memory\"", all);
        Assert.Contains("href=\"/problems?sev=critical\"", all);
        Assert.Contains("3.5 / 4 GB", all);                                       // SQL Server memory column
        Assert.Contains("Memory short", all);                                     // page life 12 s: not enough memory
        Assert.Contains("12 s", all);                                             // page life column

        var disk = (await _web.GetAsync("/problems?kind=disk")).Body;
        Assert.Contains("is almost full", disk);
        Assert.DoesNotContain("Database Broken is SUSPECT", disk);
        Assert.DoesNotContain("Full backup of Sales", disk);
        var memory = (await _web.GetAsync("/problems?kind=memory")).Body;
        Assert.Contains("Page life expectancy is low", memory);
        Assert.DoesNotContain("almost full", memory);

        var critical = (await _web.GetAsync("/problems?sev=critical")).Body;
        Assert.Contains("Database Broken is SUSPECT", critical);
        Assert.DoesNotContain("Page life expectancy is low", critical);            // a warning
        Assert.Contains("is almost full", (await _web.GetAsync("/problems?kind=nonsense")).Body);   // unknown kind = no filter
    }
}

public sealed class MemoryStateTextTests
{
    [Theory]
    [InlineData("Available physical memory is high", 1)]
    [InlineData("Physical memory usage is steady", 2)]
    [InlineData("Physical memory state is transitioning", 2)]
    [InlineData("Available physical memory is low", 3)]
    public void SQL_Servers_own_memory_wording_maps_to_enough_steady_short(string text, double code) =>
        Assert.Equal(code, SqlLookout.Core.Collectors.MemoryCollector.OsStateCode(text));

    [Fact]
    public void Unknown_wording_gives_no_verdict() =>
        Assert.Null(SqlLookout.Core.Collectors.MemoryCollector.OsStateCode("something new"));
}
