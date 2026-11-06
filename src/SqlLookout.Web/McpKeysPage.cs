using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Web;

/// <summary>
/// Keys for the remote MCP endpoint (/mcp). A key is shown once, when it is made; only a hash is kept. Each key has an
/// expiry date and can be limited to some servers. Plain forms, no script, like the rest of the interface.
/// </summary>
internal static class McpKeysPage
{
    private static readonly int[] Lifetimes = [30, 90, 365];

    public static void Map(WebApplication app)
    {
        app.MapGet("/settings/mcp", async (HttpContext ctx, SettingsStore store, LiveConfig live, string? msg) =>
        {
            var notice = msg == "revoked" ? H.Notice(Loc.T("Key revoked. It stops working at once."), "ok") : "";
            return SqlLookoutWeb.Show(ctx, Loc.T("MCP access keys"), await RenderAsync(ctx, store, live, notice, newKey: null));
        });

        app.MapPost("/settings/mcp/new", async (HttpContext ctx, SettingsStore store, LiveConfig live) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var days = int.TryParse(form["days"], out var d) && Lifetimes.Contains(d) ? d : 90;
            var all = form["all"] == "1";
            var picked = form["inst"].Select(v => v ?? "").Where(v => v.Length > 0).ToList();
            try
            {
                if (!all)
                {
                    // Only names that exist can be picked; anything else in the form is ignored.
                    var existing = live.Current.Instances.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    picked = picked.Where(existing.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                var (_, key) = await store.IssueApiKeyAsync(form["name"].ToString(), DateTime.UtcNow.AddDays(days), all ? null : picked, ct: ctx.RequestAborted);
                return SqlLookoutWeb.Show(ctx, Loc.T("MCP access keys"), await RenderAsync(ctx, store, live, "", newKey: key));
            }
            catch (InvalidDataException ex)
            {
                return SqlLookoutWeb.Show(ctx, Loc.T("MCP access keys"), await RenderAsync(ctx, store, live, H.Notice(ex.Message), newKey: null), status: 400);
            }
        });

        app.MapPost("/settings/mcp/revoke", async (HttpContext ctx, SettingsStore store) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (long.TryParse(form["id"], out var id)) await store.RevokeApiKeyAsync(id, ct: ctx.RequestAborted);
            return Results.Redirect("/settings/mcp?msg=revoked");
        });
    }

    private static async Task<string> RenderAsync(HttpContext ctx, SettingsStore store, LiveConfig live, string notice, string? newKey)
    {
        var keys = await store.ListApiKeysAsync(ctx.RequestAborted);
        var now = DateTime.UtcNow;
        var servers = live.Current.Instances.Select(i => i.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.Append($"<h1>{H.E(Loc.T("MCP access keys"))}</h1>{notice}");
        sb.Append($"""<p class="muted">{H.E(Loc.T("A key lets an AI assistant on another computer read what SqlLookout knows, over https at {0}. It cannot change anything. Anyone who has the key can read monitoring data and the text of running queries, so keep it secret and give each person or tool its own.", $"https://{ctx.Request.Host}/mcp"))}</p>""");

        if (newKey is not null)
        {
            var url = $"https://{ctx.Request.Host}/mcp";
            sb.Append($"""
                <section class="card">
                  <h2>{H.E(Loc.T("Your new key"))}</h2>
                  {H.Notice(Loc.T("Copy it now. It is not shown again; if you lose it, make a new one."), "warn")}
                  <p><code>{H.E(newKey)}</code></p>
                  <p class="muted">{H.E(Loc.T("Claude Code:"))}</p>
                  <pre>claude mcp add --transport http sqllookout {H.E(url)} --header "Authorization: Bearer {H.E(newKey)}"</pre>
                </section>
                """);
        }

        sb.Append($"""<section class="card"><h2>{H.E(Loc.T("Make a key"))}</h2><form method="post" action="/settings/mcp/new">{H.Token(SqlLookoutWeb.Token(ctx))}""");
        sb.Append(H.Field(Loc.T("Name"), "name", help: Loc.T("Who or what uses it, for example \"Anna's laptop\"."), required: true, extra: "maxlength=\"64\""));
        sb.Append(H.Select(Loc.T("Valid for"), "days", Lifetimes.Select(l => (l.ToString(), Loc.T("{0} days", l))), "90", Loc.T("The key stops working on its own after this time.")));
        sb.Append($"""<div class="field check"><label><input type="checkbox" name="all" value="1" checked> {H.E(Loc.T("All servers"))}</label><small>{H.E(Loc.T("Untick to pick the servers this key may see."))}</small></div>""");
        foreach (var name in servers)
            sb.Append($"""<div class="field check"><label><input type="checkbox" name="inst" value="{H.E(name)}"> {H.E(name)}</label></div>""");
        sb.Append($"""<div class="actions"><button type="submit">{H.E(Loc.T("Make key"))}</button></div></form></section>""");

        sb.Append($"<h2>{H.E(Loc.T("Keys"))}</h2>");
        if (keys.Count == 0)
        {
            sb.Append($"""<p class="muted">{H.E(Loc.T("No keys yet. Until one exists, the MCP address does not answer at all."))}</p>""");
        }
        else
        {
            sb.Append($"""<div class="card tablewrap"><table><thead><tr><th>{H.E(Loc.T("Name"))}</th><th>{H.E(Loc.T("Servers"))}</th><th>{H.E(Loc.T("Expires"))}</th><th>{H.E(Loc.T("Last used"))}</th><th>{H.E(Loc.T("State"))}</th><th></th></tr></thead><tbody>""");
            foreach (var k in keys)
            {
                var state = k.RevokedUtc is not null ? Loc.T("revoked") : k.ExpiresUtc <= now ? Loc.T("expired") : Loc.T("active");
                var scope = k.Instances is null ? Loc.T("all") : string.Join(", ", k.Instances);
                var last = k.LastUsedUtc is null ? Loc.T("never") : k.LastUsedUtc.Value.ToString("yyyy-MM-dd HH:mm") + " UTC";
                var revoke = k.IsUsable(now)
                    ? $"""<form method="post" action="/settings/mcp/revoke" class="inline">{H.Token(SqlLookoutWeb.Token(ctx))}<input type="hidden" name="id" value="{k.Id}"><button class="secondary">{H.E(Loc.T("Revoke"))}</button></form>"""
                    : "";
                sb.Append($"<tr><td>{H.E(k.Name)}</td><td>{H.E(scope)}</td><td>{H.E(k.ExpiresUtc.ToString("yyyy-MM-dd"))}</td><td>{H.E(last)}</td><td>{H.E(state)}</td><td>{revoke}</td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }
        sb.Append($"""<div class="actions"><a class="btn secondary" href="/settings">{H.E(Loc.T("Back to settings"))}</a></div>""");
        return sb.ToString();
    }
}
