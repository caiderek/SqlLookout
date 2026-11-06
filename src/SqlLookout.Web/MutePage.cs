using System.Text;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Web;

/// <summary>
/// Acknowledge or snooze one problem from its row. Only a problem that is found right now can be muted (the form
/// carries just the key; the server looks the finding up itself), and un-muting works for any mute on that server.
/// </summary>
internal static class MutePage
{
    // The choices of the drop-down: value, how long (null = until the problem is gone).
    private static readonly (string Value, TimeSpan? Length)[] Choices =
    [
        ("ack", null), ("1h", TimeSpan.FromHours(1)), ("4h", TimeSpan.FromHours(4)), ("1d", TimeSpan.FromDays(1)), ("7d", TimeSpan.FromDays(7)),
    ];

    public static void Map(WebApplication app)
    {
        app.MapPost("/alerts/mute", async (HttpContext ctx, IHistoryStore history, LiveConfig live) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var config = live.Current;
            var instance = config.Instances.FirstOrDefault(i => string.Equals(i.Name, form["instance"], StringComparison.OrdinalIgnoreCase));
            var key = form["key"].ToString();
            var action = form["action"].ToString();
            if (instance is null || key.Length == 0) return Results.Redirect("/");

            var back = ServerPage.Link(instance.Name, "problems");

            if (action == "clear")
            {
                if (await AlertMutes.GetAsync(history, key, ctx.RequestAborted) is { } m && string.Equals(m.Instance, instance.Name, StringComparison.OrdinalIgnoreCase))
                    await AlertMutes.ClearAsync(history, key, ctx.RequestAborted);
                return Results.Redirect(back);
            }

            var choice = Choices.FirstOrDefault(c => c.Value == action);
            if (choice.Value is null || !instance.Enabled) return Results.Redirect(back);       // a paused server lists no problems
            var now = DateTime.UtcNow;
            var status = await StatusReport.BuildAsync(history, config, instance.Name, now, ctx.RequestAborted);
            var finding = status.Findings.FirstOrDefault(f => f.Key == key);
            if (finding is null) return Results.Redirect(back);       // it went away by itself in the meantime

            await AlertMutes.SetAsync(history, finding, choice.Length is null ? MuteKind.Acknowledged : MuteKind.Snoozed,
                choice.Length is { } len ? now + len : null, now, ctx.RequestAborted);
            return Results.Redirect(back);
        });
    }

    /// <summary>The line under a problem: its mute state with a button to undo it, or the drop-down to mute it.</summary>
    public static string Controls(HttpContext ctx, string instance, Finding f, AlertMute? mute, DateTime now)
    {
        var token = H.Token(SqlLookoutWeb.Token(ctx));
        var fields = $"""{token}<input type="hidden" name="instance" value="{H.E(instance)}"><input type="hidden" name="key" value="{H.E(f.Key)}">""";
        if (mute is not null && mute.Silences(f, now))
        {
            var what = mute.Kind == MuteKind.Acknowledged
                ? Loc.T("Acknowledged: no more mail until it is gone or gets worse.")
                : Loc.T("Snoozed until {0:yyyy-MM-dd HH:mm} UTC.", mute.UntilUtc);
            return $"""<form method="post" action="/alerts/mute" class="inline">{fields}<input type="hidden" name="action" value="clear"><span class="muted small">{H.E(what)}</span> <button class="secondary">{H.E(Loc.T("Turn alerts back on"))}</button></form>""";
        }

        var options = new StringBuilder();
        foreach (var (value, length) in Choices)
            options.Append($"""<option value="{value}">{H.E(Label(length))}</option>""");
        return $"""<form method="post" action="/alerts/mute" class="inline"><label class="small">{H.E(Loc.T("Silence this problem"))} <select name="action">{options}</select></label> {fields}<button class="secondary">{H.E(Loc.T("Silence"))}</button></form>""";
    }

    /// <summary>Short text for the dashboard list, where there is no room for buttons.</summary>
    public static string Badge(AlertMute mute) =>
        DashboardPage.Badge("off", mute.Kind == MuteKind.Acknowledged ? Loc.T("acknowledged") : Loc.T("snoozed"));

    private static string Label(TimeSpan? length) => length switch
    {
        null => Loc.T("Acknowledge until it is gone"),
        { TotalHours: 1 } => Loc.T("Snooze for 1 hour"),
        { TotalHours: 4 } => Loc.T("Snooze for 4 hours"),
        { TotalDays: 1 } => Loc.T("Snooze for 1 day"),
        _ => Loc.T("Snooze for 7 days"),
    };
}
