using System.Net;
using Microsoft.Data.SqlClient;
using SqlLookout.Core;

namespace SqlLookout.Tests;

/// <summary>The queries tab: text clean-up, the two reads against a real SQL Server, and the page around them.</summary>
public sealed class LiveQueriesTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
        ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";

    private WebFixture _web = null!;

    public async Task InitializeAsync() => _web = await WebFixture.StartAsync();
    public async Task DisposeAsync() => await _web.DisposeAsync();

    // ---------------------------------------------------------------- text

    [Fact]
    public void Clean_makes_one_line_and_cuts_long_text()
    {
        Assert.Equal("SELECT 1 FROM t WHERE a = 2", LiveQueries.Clean("  SELECT 1\r\n\tFROM   t\nWHERE a = 2  \n"));
        Assert.Equal("", LiveQueries.Clean(null));
        Assert.Equal("", LiveQueries.Clean("  \r\n "));

        var cut = LiveQueries.Clean(new string('x', 1000));
        Assert.Equal(LiveQueries.MaxTextLength + 1, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal(new string('x', 10), LiveQueries.Clean(new string('x', 10)));
        Assert.Equal(new string('y', LiveQueries.MaxTextLength), LiveQueries.Clean(new string('y', LiveQueries.MaxTextLength)));   // exactly the limit is kept whole
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1 s")]
    [InlineData(90_000, "1.5 min")]
    [InlineData(7_200_000, "2 h")]
    public void Durations_read_in_the_nearest_unit(double ms, string expected) =>
        Assert.Equal(expected, LiveQueries.Duration(ms));

    // ---------------------------------------------------------------- real server

    private static async Task<SqlConnection> OpenAsync()
    {
        var c = new SqlConnection(ConnectionString);
        await c.OpenAsync();
        return c;
    }

    [Fact]
    public async Task A_query_that_is_running_is_listed_with_its_statement_not_the_whole_batch()
    {
        var marker = "LQ_" + Guid.NewGuid().ToString("N");
        await using var worker = await OpenAsync();
        await using var reader = await OpenAsync();

        // Two statements in one batch: only the one being executed (the wait) should be shown.
        var running = Task.Run(async () =>
        {
            await using var cmd = worker.CreateCommand();
            cmd.CommandText = $"SELECT 1 AS first_statement; WAITFOR DELAY '00:00:06' /* {marker} */";
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
        });

        RunningQuery? mine = null;
        for (var i = 0; i < 20 && mine is null; i++)
        {
            await Task.Delay(250);
            mine = (await LiveQueries.ReadRunningAsync(reader, CancellationToken.None)).FirstOrDefault(q => q.Text.Contains(marker));
        }

        Assert.NotNull(mine);
        Assert.StartsWith("WAITFOR DELAY", mine!.Text);
        Assert.DoesNotContain("first_statement", mine.Text);
        Assert.True(mine.ElapsedMs >= 0);
        Assert.Equal(0, mine.BlockedBy);
        Assert.Equal("WAITFOR", mine.WaitType);
        await using var spid = reader.CreateCommand();
        spid.CommandText = "SELECT @@SPID";
        var ownSession = Convert.ToInt32(await spid.ExecuteScalarAsync());
        Assert.DoesNotContain(await LiveQueries.ReadRunningAsync(reader, CancellationToken.None), q => q.SessionId == ownSession);   // never lists its own look

        await running;
    }

    [Fact]
    public async Task The_costliest_cached_statements_include_one_that_just_ran_with_its_run_count()
    {
        var marker = "LQ_" + Guid.NewGuid().ToString("N");
        await using var c = await OpenAsync();
        for (var i = 0; i < 3; i++)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT_BIG(*) AS {marker} FROM sys.all_objects a CROSS JOIN sys.all_objects b";
            cmd.CommandTimeout = 60;
            await cmd.ExecuteScalarAsync();
        }

        var heavy = await LiveQueries.ReadHeaviestAsync(c, CancellationToken.None, top: 100_000);   // the default list is cut to the top few, which a busy dev server fills with heavier work

        var mine = Assert.Single(heavy, q => q.Text.Contains(marker));
        Assert.Equal(3, mine.Executions);
        Assert.Equal("master", mine.Database);                                  // ad-hoc text has no database of its own; it comes from the plan
        Assert.True(mine.AvgElapsedMs > 0);
        Assert.True(mine.AvgReads > 0);
        Assert.True((await LiveQueries.ReadHeaviestAsync(c, CancellationToken.None)).Count <= LiveQueries.HeavyLimit);
        Assert.Equal(heavy.Select(q => q.TotalElapsedMs).OrderByDescending(x => x), heavy.Select(q => q.TotalElapsedMs));
        Assert.DoesNotContain(heavy, q => q.Text.Contains("dm_exec_query_stats"));
    }

    [Fact]
    public async Task The_monitors_own_sessions_are_not_listed_but_other_programs_are()
    {
        var marker = "LQ_" + Guid.NewGuid().ToString("N");
        var own = new SqlConnectionStringBuilder(ConnectionString) { ApplicationName = "SqlLookout" }.ConnectionString;
        var other = new SqlConnectionStringBuilder(ConnectionString) { ApplicationName = "SomeOtherApp" }.ConnectionString;
        await using var a = new SqlConnection(own);
        await using var b = new SqlConnection(other);
        await using var reader = await OpenAsync();
        await a.OpenAsync();
        await b.OpenAsync();

        Task Wait(SqlConnection c, string tag) => Task.Run(async () =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"WAITFOR DELAY '00:00:05' /* {tag} */";
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
        });
        var t1 = Wait(a, marker + "_own");
        var t2 = Wait(b, marker + "_other");
        await Task.Delay(1500);

        var listed = await LiveQueries.ReadRunningAsync(reader, CancellationToken.None);

        Assert.Contains(listed, q => q.Text.Contains(marker + "_other"));
        Assert.DoesNotContain(listed, q => q.Text.Contains(marker + "_own"));
        await Task.WhenAll(t1, t2);
    }

    // ---------------------------------------------------------------- the page

    private async Task AddServerAsync(string name, string server) =>
        await _web.PostAsync("/instances/new", "/instances/new",
            ("name", name), ("server", server), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));

    [Fact]
    public async Task The_queries_tab_shows_both_lists_escapes_query_text_and_does_not_refresh_itself()
    {
        await _web.SignInAsAdminAsync();
        await AddServerAsync("local", "localhost");
        await using var worker = await OpenAsync();

        var running = Task.Run(async () =>
        {
            await using var cmd = worker.CreateCommand();
            cmd.CommandText = "WAITFOR DELAY '00:00:08' /* <img src=x onerror=alert(1)> */";
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
        });
        await Task.Delay(1000);

        var page = await _web.GetAsync("/server/local?tab=queries");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("Running now", page.Body);
        Assert.Contains("Costliest cached statements", page.Body);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", page.Body);
        Assert.DoesNotContain("<img src=x", page.Body);
        Assert.DoesNotContain("http-equiv=\"refresh\"", page.Body);          // other tabs refresh every minute; this one must not
        Assert.Contains("class=\"on\"", page.Body);

        var problems = await _web.GetAsync("/server/local?tab=problems");
        Assert.Contains("http-equiv=\"refresh\"", problems.Body);
        Assert.Contains("tab=queries", problems.Body);                        // the tab is in the bar of every page

        await running;
    }

    [Fact]
    public async Task The_queries_tab_needs_a_signed_in_user_and_reports_an_unreachable_server_without_leaking_details()
    {
        var anonymous = await _web.GetAsync("/server/local?tab=queries");
        Assert.NotEqual("Running now", anonymous.Body);
        Assert.DoesNotContain("Running now", anonymous.Body);

        await _web.SignInAsAdminAsync();
        await AddServerAsync("down", "localhost,1");                            // nothing listens on port 1

        var page = await _web.GetAsync("/server/down?tab=queries");

        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("Could not read the queries (SQL error", page.Body);
        Assert.DoesNotContain("localhost,1", page.Body.Replace("/server/down", ""));   // the address is not echoed back from the error text
    }
}
