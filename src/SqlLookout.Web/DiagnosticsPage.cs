using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Web;

/// <summary>Shows the optional diagnostic log, lets you download or clear it. Turned on in Settings; plain forms, no script.</summary>
internal static class DiagnosticsPage
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/settings/diagnostics", (HttpContext ctx, DiagnosticLog log, LiveConfig live, string? msg) =>
        {
            var notice = msg == "cleared" ? H.Notice(Loc.T("Log cleared."), "ok") : "";
            return SqlLookoutWeb.Show(ctx, Loc.T("Diagnostic log"), Render(ctx, log, live.Current.Alerts.ClaudeLinks, notice));
        });

        app.MapGet("/settings/diagnostics/download", (DiagnosticLog log) =>
            Results.File(log.ReadAll(), "text/plain; charset=utf-8", "sqllookout-diagnostic.log"));

        app.MapPost("/settings/diagnostics/clear", (DiagnosticLog log) =>
        {
            log.Clear();
            return Results.Redirect("/settings/diagnostics?msg=cleared");
        });
    }

    private static string Render(HttpContext ctx, DiagnosticLog log, bool claudeLinks, string notice)
    {
        var state = log.Enabled
            ? H.Notice(Loc.T("The diagnostic log is on."), "ok")
            : H.Notice(Loc.T("The diagnostic log is off. Turn it on in Settings, then repeat the problem and come back here."), "warn");
        var tail = log.Tail();
        var userAgent = ctx.Request.Headers.UserAgent.ToString();
        return $"""
            <h1>{H.E(Loc.T("Diagnostic log"))}</h1>
            {notice}{state}
            <section class="card">
              <p>{H.E(Loc.T("Warnings, errors and Claude Code link events are written to this file. It holds server names but no passwords or keys. Look at it before you send it to anyone."))}</p>
              <p class="muted small">{H.E(log.Path)}</p>
              <p><a href="/settings/diagnostics/download">{H.E(Loc.T("Download the log"))}</a></p>
              <form method="post" action="/settings/diagnostics/clear">{H.Token(SqlLookoutWeb.Token(ctx))}<button type="submit">{H.E(Loc.T("Clear the log"))}</button></form>
            </section>
            <section class="card">
              <h2 style="margin-top:0">{H.E(Loc.T("This browser"))}</h2>
              <p class="muted small">{H.E(userAgent)}</p>
              <p>{H.E(Loc.T("Claude Code links are"))} <strong>{H.E(claudeLinks ? Loc.T("on") : Loc.T("off"))}</strong>.</p>
            </section>
            <section class="card">
              <h2 style="margin-top:0">{H.E(Loc.T("Latest entries"))}</h2>
              {(tail.Length == 0 ? $"<p class=\"muted\">{H.E(Loc.T("Nothing logged yet."))}</p>" : $"<pre style=\"white-space:pre-wrap;overflow-x:auto\">{H.E(tail)}</pre>")}
            </section>
            """;
    }
}
