using System.Net;

namespace SqlLookout.Tests;

public class WebThemeTests : IAsyncLifetime
{
    private WebFixture _web = null!;

    public async Task InitializeAsync()
    {
        _web = await WebFixture.StartAsync();
        await _web.SignInAsAdminAsync();
    }

    public async Task DisposeAsync() => await _web.DisposeAsync();

    [Fact]
    public async Task Default_is_auto_which_follows_the_browser_and_the_button_offers_light_next()
    {
        var home = (await _web.GetAsync("/")).Body;

        Assert.DoesNotContain("data-theme", home);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", home);
        Assert.Contains("Theme: Auto", home);
        Assert.Contains("name=\"theme\" value=\"light\"", home);
    }

    [Fact]
    public async Task The_button_cycles_light_then_dark_then_back_to_auto_and_the_choice_is_remembered()
    {
        var light = await _web.PostAsync("/", "/theme", ("theme", "light"), ("returnUrl", "/instances"));
        Assert.Equal(HttpStatusCode.Redirect, light.Status);
        Assert.Equal("/instances", WebFixture.Location(light.Response));
        var page = (await _web.GetAsync("/instances")).Body;
        Assert.Contains("<html lang=\"en\" data-theme=\"light\">", page);
        Assert.Contains("<meta name=\"color-scheme\" content=\"light\">", page);
        Assert.Contains("Theme: Light", page);
        Assert.Contains("name=\"theme\" value=\"dark\"", page);

        await _web.PostAsync("/", "/theme", ("theme", "dark"), ("returnUrl", "/"));
        var dark = (await _web.GetAsync("/")).Body;
        Assert.Contains("data-theme=\"dark\"", dark);
        Assert.Contains("Theme: Dark", dark);
        Assert.Contains("name=\"theme\" value=\"auto\"", dark);

        await _web.PostAsync("/", "/theme", ("theme", "auto"), ("returnUrl", "/"));
        var auto = (await _web.GetAsync("/")).Body;
        Assert.DoesNotContain("data-theme", auto);
        Assert.Null(_web.Cookie("sqllookout.theme"));
    }

    [Fact]
    public async Task An_unknown_theme_value_is_ignored_and_an_outside_return_address_is_not_followed()
    {
        var r = await _web.PostAsync("/", "/theme", ("theme", "\"><script>x()</script>"), ("returnUrl", "https://evil.example/"));

        Assert.Equal("/", WebFixture.Location(r.Response));
        var home = (await _web.GetAsync("/")).Body;
        Assert.DoesNotContain("data-theme", home);
        Assert.DoesNotContain("<script>x()", home);
        Assert.Null(_web.Cookie("sqllookout.theme"));                       // nothing unknown is stored either
    }

    [Fact]
    public async Task The_stylesheet_has_a_light_set_a_dark_set_for_auto_and_a_forced_dark_set()
    {
        var css = (await _web.GetAsync("/app.css")).Body;

        Assert.Contains(":root { --bg:#f4f6fa;", css);
        Assert.Contains("@media (prefers-color-scheme: dark) { :root:not([data-theme=\"light\"]) { --bg:#11141a;", css);
        Assert.Contains(":root[data-theme=\"dark\"] { --bg:#11141a;", css);
    }

    [Fact]
    public async Task The_current_page_is_marked_in_the_navigation_and_a_server_page_counts_as_the_dashboard()
    {
        await _web.PostAsync("/instances/new", "/instances/new", ("name", "box"), ("server", "BOX01"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));

        Assert.Contains("<a href=\"/instances\" class=\"on\" aria-current=\"page\">", (await _web.GetAsync("/instances")).Body);
        Assert.Contains("<a href=\"/settings\" class=\"on\" aria-current=\"page\">", (await _web.GetAsync("/settings")).Body);

        var server = (await _web.GetAsync("/server/box")).Body;
        Assert.Contains("<a href=\"/\" class=\"on\" aria-current=\"page\">", server);
        Assert.DoesNotContain("<a href=\"/instances\" class=\"on\"", server);
    }

    [Fact]
    public async Task The_sign_in_page_also_follows_the_chosen_theme()
    {
        await _web.PostAsync("/", "/theme", ("theme", "dark"), ("returnUrl", "/"));
        await _web.PostAsync("/", "/logout");

        var login = (await _web.GetAsync("/login")).Body;

        Assert.Contains("data-theme=\"dark\"", login);
    }

    [Fact]
    public async Task The_theme_and_new_dashboard_text_are_translated()
    {
        await _web.PostAsync("/", "/language", ("language", "zh-TW"), ("returnUrl", "/"));
        await _web.PostAsync("/", "/theme", ("theme", "light"), ("returnUrl", "/"));
        await _web.PostAsync("/instances/new", "/instances/new", ("name", "box"), ("server", "BOX01"), ("auth", "windows"), ("user", ""), ("password", ""), ("encrypt", "trust"), ("enabled", "1"));

        var home = (await _web.GetAsync("/")).Body;

        Assert.Contains("外觀:淺色", home);
        Assert.Contains("沒有問題的伺服器", home);
        Assert.Contains("嚴重問題", home);
    }
}
