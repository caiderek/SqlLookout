using System.Net;
using System.Text.RegularExpressions;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

public sealed class MuteWebTests : IAsyncLifetime
{
    private WebFixture _web = null!;

    public async Task InitializeAsync() => _web = await WebFixture.StartAsync();
    public async Task DisposeAsync() => await _web.DisposeAsync();

    private async Task AddServerWithBrokenDatabaseAsync(string name)
    {
        await _web.PostAsync("/instances/new", "/instances/new",
            ("name", name), ("server", name.ToUpperInvariant()), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));
        var t = DateTime.UtcNow;
        await _web.History.AppendAsync(name, CollectorCatalog.Connectivity, t, TimeSpan.Zero, CollectionResult.Ok([]));
        await _web.History.AppendAsync(name, "database_state", t, TimeSpan.Zero, CollectionResult.Ok(
            [new Sample("db_state", "Broken", 4, "SUSPECT")]));
    }

    private async Task<string> KeyOfBrokenAsync(string server)
    {
        var page = await _web.GetAsync($"/server/{server}");
        var m = Regex.Match(page.Body, @"name=""key"" value=""([^""]+)""");
        Assert.True(m.Success, "the problem row has a silence form");
        return WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    private Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> MuteAsync(string server, string key, string action) =>
        _web.PostAsync($"/server/{server}", "/alerts/mute", ("instance", server), ("key", key), ("action", action));

    [Fact]
    public async Task A_problem_can_be_acknowledged_and_turned_back_on()
    {
        await _web.SignInAsAdminAsync();
        await AddServerWithBrokenDatabaseAsync("prod");
        var key = await KeyOfBrokenAsync("prod");

        var done = await MuteAsync("prod", key, "ack");
        Assert.Equal(HttpStatusCode.Redirect, done.Status);
        Assert.Equal("/server/prod?tab=problems", WebFixture.Location(done.Response));

        var mute = Assert.Single(await AlertMutes.ListAsync(_web.History));
        Assert.Equal(MuteKind.Acknowledged, mute.Kind);
        Assert.Equal(Severity.Critical, mute.Severity);
        var page = await _web.GetAsync("/server/prod");
        Assert.Contains("Acknowledged: no more mail", page.Body);
        Assert.Contains("Turn alerts back on", page.Body);
        Assert.Contains("acknowledged", (await _web.GetAsync("/problems")).Body);

        await MuteAsync("prod", key, "clear");
        Assert.Empty(await AlertMutes.ListAsync(_web.History));
        Assert.Contains("Silence this problem", (await _web.GetAsync("/server/prod")).Body);
    }

    [Fact]
    public async Task A_snooze_runs_for_the_chosen_time_and_says_until_when()
    {
        await _web.SignInAsAdminAsync();
        await AddServerWithBrokenDatabaseAsync("prod");
        var key = await KeyOfBrokenAsync("prod");

        var before = DateTime.UtcNow;
        await MuteAsync("prod", key, "4h");
        var mute = Assert.Single(await AlertMutes.ListAsync(_web.History));
        Assert.Equal(MuteKind.Snoozed, mute.Kind);
        Assert.InRange(mute.UntilUtc!.Value, before.AddHours(4), DateTime.UtcNow.AddHours(4));
        Assert.Contains($"Snoozed until {mute.UntilUtc:yyyy-MM-dd HH:mm} UTC", (await _web.GetAsync("/server/prod")).Body);
    }

    [Fact]
    public async Task Only_a_problem_that_exists_right_now_can_be_muted()
    {
        await _web.SignInAsAdminAsync();
        await AddServerWithBrokenDatabaseAsync("prod");
        var key = await KeyOfBrokenAsync("prod");

        await MuteAsync("prod", "made|up|key", "ack");                       // not a current finding
        await MuteAsync("prod", key, "forever");                              // not one of the choices
        await _web.PostAsync("/server/prod", "/alerts/mute", ("instance", "nope"), ("key", key), ("action", "ack"));   // no such server
        await _web.PostAsync("/server/prod", "/alerts/mute", ("instance", "prod"), ("action", "ack"));                 // no key
        Assert.Empty(await AlertMutes.ListAsync(_web.History));
    }

    [Fact]
    public async Task One_server_cannot_turn_off_the_mute_of_another()
    {
        await _web.SignInAsAdminAsync();
        await AddServerWithBrokenDatabaseAsync("prod");
        await AddServerWithBrokenDatabaseAsync("test");
        await MuteAsync("prod", await KeyOfBrokenAsync("prod"), "ack");

        await MuteAsync("test", await KeyOfBrokenAsync("prod"), "clear");
        Assert.Single(await AlertMutes.ListAsync(_web.History));
    }

    [Fact]
    public async Task Without_signing_in_nothing_can_be_muted()
    {
        await _web.SignInAsAdminAsync();
        await AddServerWithBrokenDatabaseAsync("prod");
        var key = await KeyOfBrokenAsync("prod");
        _web.ForgetCookies();

        var r = await _web.PostRawAsync("/alerts/mute", ("instance", "prod"), ("key", key), ("action", "ack"));
        Assert.NotEqual(HttpStatusCode.OK, r.Status);
        Assert.Empty(await AlertMutes.ListAsync(_web.History));
    }

    [Fact]
    public async Task The_daily_summary_marks_a_muted_problem()
    {
        var f = new Finding("prod|x", "prod", Severity.Warning, "Something", "detail");
        var now = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
        await AlertMutes.SetAsync(_web.History, f, MuteKind.Acknowledged, null, now);
        var ack = await DailySummary.BuildAsync(_web.History, [], now, [f]);
        Assert.Contains("acknowledged by a person", ack);

        await AlertMutes.SetAsync(_web.History, f, MuteKind.Snoozed, now.AddHours(5), now);
        Assert.Contains("snoozed until 2026-10-23 13:00 UTC", await DailySummary.BuildAsync(_web.History, [], now, [f]));
        Assert.DoesNotContain("snoozed until", await DailySummary.BuildAsync(_web.History, [], now.AddHours(6), [f]));
    }

    [Fact]
    public async Task Deleting_a_server_forgets_its_mutes_so_a_server_added_later_under_the_name_starts_clean()
    {
        await _web.SignInAsAdminAsync();
        await AddServerWithBrokenDatabaseAsync("prod");
        await AddServerWithBrokenDatabaseAsync("other");
        await MuteAsync("prod", await KeyOfBrokenAsync("prod"), "ack");
        await MuteAsync("other", await KeyOfBrokenAsync("other"), "ack");

        await _web.PostAsync("/instances/prod/delete", "/instances/prod/delete");
        var left = Assert.Single(await AlertMutes.ListAsync(_web.History));
        Assert.Equal("other", left.Instance);

        await AddServerWithBrokenDatabaseAsync("prod");
        var page = await _web.GetAsync("/server/prod");
        Assert.Contains("Silence this problem", page.Body);
        Assert.DoesNotContain("Acknowledged:", page.Body);
    }

    [Fact]
    public async Task A_paused_server_cannot_have_a_problem_muted()
    {
        await _web.SignInAsAdminAsync();
        await AddServerWithBrokenDatabaseAsync("prod");
        var key = await KeyOfBrokenAsync("prod");
        await _web.PostAsync("/instances/prod/edit", "/instances/prod/edit",
            ("name", "prod"), ("server", "PROD"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"));   // no "enabled" field = paused

        await MuteAsync("prod", key, "1h");
        Assert.Empty(await AlertMutes.ListAsync(_web.History));
    }
}
