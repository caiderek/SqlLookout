using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SqlLookout.Alerts;

/// <summary>Where Teams alerts go. The address contains a secret key, so the web interface keeps it encrypted and it is never written to the settings JSON.</summary>
public sealed class TeamsSettings
{
    public string? UrlFile { get; set; }
    public string? UrlEnv { get; set; }

    /// <summary>Set by the web UI mode from the encrypted settings. Never read from or written to JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Url { get; set; }

    public string? ResolveUrl()
    {
        if (Url is not null) return Url;
        if (UrlFile is not null) return File.ReadAllText(UrlFile).Trim();
        if (UrlEnv is not null) return Environment.GetEnvironmentVariable(UrlEnv)?.Trim();
        return null;
    }

    /// <summary>True for an address SqlLookout is willing to post to: https only.</summary>
    public static bool IsAcceptableUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
}

/// <summary>
/// Posts alerts to a Teams webhook (a Workflows "when a Teams webhook request is received" flow, or the older
/// incoming-webhook connector; both take a message with an Adaptive Card). The address is a secret and is never put into an error message.
/// </summary>
public sealed partial class TeamsAlertSender(TeamsSettings settings, HttpMessageHandler? handler = null) : IAlertSender
{
    /// <summary>Teams refuses cards that are too large (about 28 KB); stay well below.</summary>
    internal const int MaxBodyChars = 12_000;

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var url = settings.ResolveUrl();
        if (!TeamsSettings.IsAcceptableUrl(url)) throw new InvalidOperationException("The Teams webhook address is missing or is not an https address.");

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(30);
        using var content = new StringContent(BuildPayload(subject, body), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(url, content, ct);
        }
        catch (HttpRequestException ex)
        {
            // The message of an HttpRequestException can contain the address; leave it out.
            throw new HttpRequestException($"Could not reach Teams ({ex.HttpRequestError}).");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = (await response.Content.ReadAsStringAsync(ct)).Replace(url!, "(address)");
                if (detail.Length > 200) detail = detail[..200];
                throw new HttpRequestException($"Teams answered {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim());
            }
        }
    }

    /// <summary>A Teams TextBlock renders markdown, so a login like <c>app_user_1</c> or <c>*admin*</c> would turn into italics or bold. Escapes the markdown characters; a backslash is doubled only when it sits before an escaped character, so DOMAIN\user stays as it is.</summary>
    public static string EscapeMarkdown(string text) => MarkdownChars().Replace(text, @"\$0");

    [System.Text.RegularExpressions.GeneratedRegex(@"[*_`~]|\](?=\()|\\(?=[*_`~\\]|\]\()")]
    private static partial System.Text.RegularExpressions.Regex MarkdownChars();

    internal static string BuildPayload(string subject, string body)
    {
        if (body.Length > MaxBodyChars) body = body[..MaxBodyChars] + "\n...";
        var card = new Dictionary<string, object?>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = EscapeMarkdown(subject), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
                new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = EscapeMarkdown(body), ["wrap"] = true, ["fontType"] = "Monospace" },
            },
        };
        var message = new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["attachments"] = new object[]
            {
                new Dictionary<string, object?> { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["contentUrl"] = null, ["content"] = card },
            },
        };
        return JsonSerializer.Serialize(message);
    }
}

/// <summary>Sends to every channel that is set up (email, Teams). One channel failing does not stop the others.</summary>
public sealed class CompositeAlertSender(IReadOnlyList<IAlertSender> senders, Action<string>? log = null) : IAlertSender
{
    public bool Delivers => senders.Any(s => s.Delivers);

    /// <summary>
    /// Succeeds when at least one channel took the message. If every channel fails it throws, so the problem is not recorded as
    /// "already told" and is tried again on the next pass; when only some fail, the others have told the people, and the
    /// failure is logged instead of repeating the message on the channel that worked.
    /// </summary>
    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var errors = new List<Exception>();
        var delivered = 0;
        foreach (var sender in senders.Where(s => s.Delivers))
        {
            try
            {
                await sender.SendAsync(subject, body, ct);
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
                log?.Invoke($"Alert channel {sender.GetType().Name} failed: {ex.Message}");
            }
        }
        if (delivered == 0 && errors.Count > 0)
            throw errors.Count == 1 ? errors[0] : new AggregateException(errors);
    }
}
