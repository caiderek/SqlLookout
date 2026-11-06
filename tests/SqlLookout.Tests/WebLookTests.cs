using System.Net;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Tests;

/// <summary>The two looks of the pages: Classic (the original, and the default) and Terminal, chosen in Settings for the whole installation.</summary>
public class WebLookTests : IAsyncLifetime
{
    private WebFixture _web = null!;

    public async Task InitializeAsync() => _web = await WebFixture.StartAsync();
    public async Task DisposeAsync() => await _web.DisposeAsync();

    private const string ClassicOnly = "--bg:#f4f6fa;";
    private const string TerminalOnly = "--bg:#f1ebe2;";

    private static (string, string)[] SettingsFields(string? look)
    {
        var fields = new Dictionary<string, string> { ["renotify"] = "4", ["retention"] = "90", ["language"] = "en" };
        if (look is not null) fields["look"] = look;
        foreach (var rule in new[] { "backup", "dbcc", "disk", "log", "database_state", "suspect_pages", "jobs" }) fields["rule_" + rule] = "1";
        foreach (var c in CollectorCatalog.All()) fields["col_" + c.Name] = "1";
        foreach (var p in typeof(AlertThresholds).GetProperties())
            fields["th_" + p.Name] = Convert.ToString(p.GetValue(new AlertThresholds()), System.Globalization.CultureInfo.InvariantCulture)!;
        return fields.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    [Fact]
    public async Task A_new_installation_looks_classic_and_the_stylesheet_is_the_original()
    {
        var css = (await _web.GetAsync("/app.css")).Body;

        Assert.Equal(Looks.Classic, _web.Live.Current.Look);
        Assert.Contains(ClassicOnly, css);
        Assert.DoesNotContain(TerminalOnly, css);
        Assert.DoesNotContain("IBM Plex Mono", css);
        // What only Terminal shows is hidden, so Classic pages look as they always did.
        Assert.Contains(".sev { display:none; }", css);
        Assert.Contains(".spark .end { display:none; }", css);
    }

    [Fact]
    public async Task Choosing_terminal_in_settings_changes_the_stylesheet_for_everyone_even_on_the_sign_in_page()
    {
        await _web.SignInAsAdminAsync();
        var general = (await _web.GetAsync("/settings")).Body;
        Assert.Contains("<option value=\"classic\" selected>Classic</option>", general);
        Assert.Contains("<option value=\"terminal\">Terminal</option>", general);

        var saved = await _web.PostAsync("/settings", "/settings", SettingsFields(Looks.Terminal));

        Assert.Equal(HttpStatusCode.Redirect, saved.Status);
        Assert.Equal(Looks.Terminal, _web.Live.Current.Look);
        var css = (await _web.GetAsync("/app.css")).Body;
        Assert.Contains(TerminalOnly, css);
        Assert.DoesNotContain(ClassicOnly, css);
        Assert.DoesNotContain(".sev { display:none; }", css);
        Assert.Contains("<option value=\"terminal\" selected>Terminal</option>", (await _web.GetAsync("/settings")).Body);

        // The stylesheet needs no sign-in, so the sign-in page already has the chosen look.
        await _web.PostAsync("/", "/logout");
        Assert.Contains(TerminalOnly, (await _web.GetAsync("/app.css")).Body);
    }

    [Fact]
    public async Task The_choice_can_be_changed_back_to_classic()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/settings", "/settings", SettingsFields(Looks.Terminal));

        await _web.PostAsync("/settings", "/settings", SettingsFields(Looks.Classic));

        Assert.Equal(Looks.Classic, _web.Live.Current.Look);
        Assert.Contains(ClassicOnly, (await _web.GetAsync("/app.css")).Body);
    }

    [Fact]
    public async Task A_made_up_look_is_refused_and_nothing_changes()
    {
        await _web.SignInAsAdminAsync();

        var refused = await _web.PostAsync("/settings", "/settings", SettingsFields("neon"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        Assert.Contains("Choose a look from the list.", refused.Body);
        Assert.Equal(Looks.Classic, _web.Live.Current.Look);
        Assert.Contains(ClassicOnly, (await _web.GetAsync("/app.css")).Body);
    }

    [Fact]
    public async Task Saving_a_form_that_has_no_look_field_keeps_the_stored_look()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/settings", "/settings", SettingsFields(Looks.Terminal));

        var saved = await _web.PostAsync("/settings", "/settings", SettingsFields(null));       // an older form, from before the setting existed

        Assert.Equal(HttpStatusCode.Redirect, saved.Status);
        Assert.Equal(Looks.Terminal, _web.Live.Current.Look);
    }

    [Fact]
    public async Task Other_settings_that_save_by_themselves_do_not_reset_the_look()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/settings", "/settings", SettingsFields(Looks.Terminal));

        await _web.PostAsync("/", "/language", ("language", "zh-TW"), ("returnUrl", "/"));      // the language button saves the global settings too

        Assert.Equal(Looks.Terminal, _web.Live.Current.Look);
        Assert.Equal("zh-TW", _web.Live.Current.Language);
    }

    [Fact]
    public async Task The_chinese_settings_page_names_the_look_options_in_chinese()
    {
        await _web.SignInAsAdminAsync();
        await _web.PostAsync("/", "/language", ("language", "zh-TW"), ("returnUrl", "/"));

        var page = (await _web.GetAsync("/settings")).Body;

        Assert.Contains("外觀", page);
        Assert.Contains("<option value=\"classic\" selected>經典</option>", page);
        Assert.Contains("<option value=\"terminal\">終端機</option>", page);
    }
}
