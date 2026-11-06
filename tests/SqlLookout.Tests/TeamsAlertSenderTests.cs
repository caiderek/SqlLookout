using System.Net;
using System.Text.Json;
using SqlLookout.Alerts;

namespace SqlLookout.Tests;

public sealed class TeamsAlertSenderTests
{
    private const string Secret = "https://prod-12.westus.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?sig=TOP-SECRET-KEY";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return reply(request);
        }
    }

    private static TeamsSettings Settings(string? url = Secret) => new() { Url = url };

    [Theory]
    [InlineData("login app_user_1 failed", @"login app\_user\_1 failed")]
    [InlineData("login *admin* failed", @"login \*admin\* failed")]
    [InlineData(@"CORP\svc_sql", @"CORP\svc\_sql")]
    [InlineData(@"CORP\user", @"CORP\user")]
    [InlineData("[SqlLookout] Critical", "[SqlLookout] Critical")]
    [InlineData("[click](http://evil)", @"[click\](http://evil)")]
    [InlineData(@"a\_b", @"a\\\_b")]
    public void Markdown_characters_from_names_are_escaped_so_they_show_as_typed(string input, string expected) =>
        Assert.Equal(expected, TeamsAlertSender.EscapeMarkdown(input));

    [Fact]
    public async Task It_posts_an_adaptive_card_message_to_the_address()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        await new TeamsAlertSender(Settings(), handler).SendAsync("[SqlLookout] Critical: 1 alert(s)", "[CRITICAL] (new) prod: Disk D: is full\n    detail", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(Secret), request.Uri);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("message", json.RootElement.GetProperty("type").GetString());
        var attachment = json.RootElement.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("[SqlLookout] Critical: 1 alert(s)", card.GetProperty("body")[0].GetProperty("text").GetString());
        Assert.Contains("Disk D: is full", card.GetProperty("body")[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_very_long_body_is_cut_so_that_teams_does_not_refuse_the_card()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await new TeamsAlertSender(Settings(), handler).SendAsync("s", new string('x', 100_000), default);
        Assert.True(handler.Requests[0].Body.Length < 20_000);
    }

    [Fact]
    public async Task A_refusal_throws_with_the_status_and_never_shows_the_secret_address()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad request for " + Secret) });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new TeamsAlertSender(Settings(), handler).SendAsync("s", "b", default));

        Assert.Contains("400", ex.Message);
        Assert.DoesNotContain("TOP-SECRET-KEY", ex.Message);
    }

    [Fact]
    public async Task A_network_failure_does_not_leak_the_address_either()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("No such host is known: " + Secret));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new TeamsAlertSender(Settings(), handler).SendAsync("s", "b", default));
        Assert.DoesNotContain("TOP-SECRET-KEY", ex.ToString().Split("--->")[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.com/hook")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("https://user:pw@example.com/hook")]
    [InlineData("not a url")]
    public async Task Only_an_https_address_is_ever_posted_to(string? url)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TeamsAlertSender(Settings(url), handler).SendAsync("s", "b", default));
        Assert.Empty(handler.Requests);
        Assert.False(TeamsSettings.IsAcceptableUrl(url));
    }

    [Fact]
    public void The_address_can_come_from_a_file_or_an_environment_variable_but_not_from_json()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, Secret + "\n");
        try
        {
            Assert.Equal(Secret, new TeamsSettings { UrlFile = file }.ResolveUrl());
            var env = "SQLLOOKOUT_TEST_TEAMS_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(env, Secret);
            Assert.Equal(Secret, new TeamsSettings { UrlEnv = env }.ResolveUrl());
            Assert.DoesNotContain("TOP-SECRET", JsonSerializer.Serialize(new TeamsSettings { Url = Secret }));
        }
        finally { File.Delete(file); }
    }

    // ---- several channels

    private sealed class Channel(bool fails = false, bool delivers = true) : IAlertSender
    {
        public int Sent;
        public bool Delivers => delivers;
        public Task SendAsync(string subject, string body, CancellationToken ct)
        {
            if (fails) throw new InvalidOperationException("boom");
            Sent++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Both_channels_get_the_message()
    {
        var a = new Channel(); var b = new Channel();
        await new CompositeAlertSender([a, b]).SendAsync("s", "b", default);
        Assert.Equal((1, 1), (a.Sent, b.Sent));
    }

    [Fact]
    public async Task One_failing_channel_is_logged_and_the_other_still_counts_as_told()
    {
        var log = new List<string>();
        var ok = new Channel();
        await new CompositeAlertSender([new Channel(fails: true), ok], log.Add).SendAsync("s", "b", default);
        Assert.Equal(1, ok.Sent);
        Assert.Contains("boom", Assert.Single(log));
    }

    [Fact]
    public async Task When_every_channel_fails_it_throws_so_the_problem_is_tried_again()
    {
        await Assert.ThrowsAsync<AggregateException>(() => new CompositeAlertSender([new Channel(fails: true), new Channel(fails: true)]).SendAsync("s", "b", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CompositeAlertSender([new Channel(fails: true)]).SendAsync("s", "b", default));
    }

    [Fact]
    public void It_delivers_when_any_channel_does_and_a_channel_that_cannot_deliver_is_skipped()
    {
        Assert.True(new CompositeAlertSender([new Channel(delivers: false), new Channel()]).Delivers);
        Assert.False(new CompositeAlertSender([new Channel(delivers: false)]).Delivers);
        Assert.False(new CompositeAlertSender([]).Delivers);
    }
}
