using System.Net;
using SqlLookout.Alerts;
using SqlLookout.Core;

namespace SqlLookout.Tests;

public sealed class TeamsSettingsWebTests : IAsyncLifetime
{
    private const string Url = "https://prod-1.westus.logic.azure.com/workflows/abc?sig=TEAMS-SECRET";
    private WebFixture _web = null!;

    public async Task InitializeAsync()
    {
        _web = await WebFixture.StartAsync();
        await _web.SignInAsAdminAsync();
    }

    public async Task DisposeAsync() => await _web.DisposeAsync();

    private static (string, string)[] Form(params (string, string)[] overrides)
    {
        var fields = new Dictionary<string, string> { ["renotify"] = "4", ["retention"] = "90", ["language"] = "en" };
        foreach (var rule in new[] { "backup", "dbcc", "disk", "log", "database_state", "suspect_pages", "jobs" }) fields["rule_" + rule] = "1";
        foreach (var c in CollectorCatalog.All()) fields["col_" + c.Name] = "1";
        foreach (var p in typeof(AlertThresholds).GetProperties())
            fields["th_" + p.Name] = Convert.ToString(p.GetValue(new AlertThresholds()), System.Globalization.CultureInfo.InvariantCulture)!;
        foreach (var (k, v) in overrides) fields[k] = v;
        return fields.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    [Fact]
    public async Task The_settings_page_has_a_teams_section_and_a_test_button()
    {
        var page = await _web.GetAsync("/settings");
        Assert.Contains("Teams alerts", page.Body);
        Assert.Contains("name=\"teams_on\"", page.Body);
        Assert.Contains("at least two members", page.Body);
        Assert.Contains("action=\"/settings/test-teams\"", page.Body);
    }

    [Fact]
    public async Task Saving_teams_stores_the_address_encrypted_and_never_shows_it_again()
    {
        var saved = await _web.PostAsync("/settings", "/settings", Form(("teams_on", "1"), ("teams_url", Url)));
        Assert.Equal(HttpStatusCode.Redirect, saved.Status);

        Assert.NotNull(_web.Live.Current.Alerts.Teams);
        Assert.Equal(Url, _web.Live.Current.Alerts.Teams!.ResolveUrl());
        Assert.True(await _web.Settings.HasTeamsUrlAsync());
        var page = (await _web.GetAsync("/settings")).Body;
        Assert.DoesNotContain("TEAMS-SECRET", page);
        Assert.Contains("An address is saved", page);
        Assert.Contains("name=\"teams_on\" value=\"1\" checked", page);

        // Saving again with the box ticked and the address blank keeps it.
        await _web.PostAsync("/settings", "/settings", Form(("teams_on", "1"), ("teams_url", "")));
        Assert.Equal(Url, _web.Live.Current.Alerts.Teams!.ResolveUrl());
    }

    [Fact]
    public async Task A_missing_or_insecure_address_is_refused_and_nothing_is_saved()
    {
        var none = await _web.PostAsync("/settings", "/settings", Form(("teams_on", "1"), ("renotify", "9")));
        Assert.Equal(HttpStatusCode.BadRequest, none.Status);
        Assert.Contains("Teams needs the webhook address", none.Body);

        var http = await _web.PostAsync("/settings", "/settings", Form(("teams_on", "1"), ("teams_url", "http://insecure.example/hook"), ("renotify", "9")));
        Assert.Equal(HttpStatusCode.BadRequest, http.Status);
        Assert.Contains("must start with https://", http.Body);
        Assert.DoesNotContain("insecure.example", http.Body.Replace("http://insecure.example/hook\"", ""));

        Assert.Null(_web.Live.Current.Alerts.Teams);
        Assert.False(await _web.Settings.HasTeamsUrlAsync());
        Assert.Equal(4, _web.Live.Current.Alerts.RenotifyHours);
    }

    [Fact]
    public async Task Turning_teams_off_and_removing_the_address_work()
    {
        await _web.PostAsync("/settings", "/settings", Form(("teams_on", "1"), ("teams_url", Url)));
        await _web.PostAsync("/settings", "/settings", Form());
        Assert.Null(_web.Live.Current.Alerts.Teams);
        Assert.True(await _web.Settings.HasTeamsUrlAsync());                       // kept for when it is turned on again

        await _web.PostAsync("/settings", "/settings", Form(("teams_clear_url", "1")));
        Assert.False(await _web.Settings.HasTeamsUrlAsync());
    }

    [Fact]
    public async Task The_test_button_says_when_nothing_is_saved_and_never_prints_the_address_on_failure()
    {
        var none = await _web.PostAsync("/settings", "/settings/test-teams");
        Assert.Contains("Teams is not saved yet", none.Body);

        await _web.PostAsync("/settings", "/settings", Form(("teams_on", "1"), ("teams_url", "https://127.0.0.1:1/hook?sig=TEAMS-SECRET")));
        var failed = await _web.PostAsync("/settings", "/settings/test-teams");
        Assert.Contains("Could not send", failed.Body);
        Assert.DoesNotContain("TEAMS-SECRET", failed.Body);
    }

    [Fact]
    public async Task The_teams_page_is_translated()
    {
        await _web.PostAsync("/settings", "/settings", Form(("language", "zh-TW")));
        var page = (await _web.GetAsync("/settings")).Body;
        Assert.Contains("Teams 告警", page);
        Assert.Contains("發送 Teams 測試訊息", page);
    }
}
