using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Web;

using Row = (InstanceConfig Instance, InstanceStatus Status, IReadOnlyList<(DateTime Utc, double Value)> Cpu);

/// <summary>
/// The home page: one line per server, worst first, with a short list of current problems underneath.
/// Everything else about a server is on its own page (<see cref="ServerPage"/>).
/// </summary>
internal static class DashboardPage
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/", async (HttpContext ctx, IHistoryStore history, LiveConfig live) =>
        {
            var rows = await BuildRows(ctx, history, live.Current, DateTime.UtcNow);
            return SqlLookoutWeb.Show(ctx, Loc.T("Dashboard"), Render(rows.Rows, rows.Now), refreshSeconds: 60);
        });

        app.MapGet("/problems", async (HttpContext ctx, IHistoryStore history, LiveConfig live, DiagnosticLog diag, string? kind, string? sev) =>
        {
            var rows = await BuildRows(ctx, history, live.Current, DateTime.UtcNow);
            var mutes = await ServerPage.Mutes(history, ctx.RequestAborted);
            if (diag.Enabled)
            {
                var links = live.Current.Alerts.ClaudeLinks
                    ? rows.Rows.Where(r => r.Instance.Enabled).SelectMany(r => r.Status.Findings).Select(DeepLink.For).Where(l => l is not null).ToList()
                    : [];
                diag.Write("problems", $"Page served. Claude Code links {(live.Current.Alerts.ClaudeLinks ? "on" : "off")}, {links.Count} link(s); client: {ctx.Request.Headers.UserAgent}" +
                    (links.Count > 0 ? $"; first link: {links[0]}" : ""));
            }
            return SqlLookoutWeb.Show(ctx, Loc.T("Problems"), ProblemsPage(rows.Rows, rows.Now, mutes, PickKind(kind), sev == "critical", live.Current.Alerts.ClaudeLinks), refreshSeconds: 60);
        });
    }

    private static async Task<(List<Row> Rows, DateTime Now)> BuildRows(HttpContext ctx, IHistoryStore history, SqlLookoutConfig config, DateTime now)
    {
        var rows = new List<Row>();
        foreach (var instance in config.Instances)
        {
            var status = await StatusReport.BuildAsync(history, config, instance.Name, now, ctx.RequestAborted);
            var cpu = await history.GetSeriesAsync(instance.Name, "cpu_total_percent", null, now.AddHours(-6), ctx.RequestAborted);
            rows.Add((instance, status, cpu));
        }
        return (rows, now);
    }

    private static string Render(List<Row> rows, DateTime now)
    {
        if (rows.Count == 0)
        {
            return $"""
                <h1>{H.E(Loc.T("Dashboard"))}</h1>
                <div class="card">
                  <p><strong>{H.E(Loc.T("No SQL Server is being watched yet."))}</strong></p>
                  <p class="muted">{H.E(Loc.T("Add your first server and SqlLookout starts collecting within a minute."))}</p>
                  <div class="actions"><a class="btn" href="/instances/new">{H.E(Loc.T("Add a server"))}</a></div>
                </div>
                """;
        }

        var critical = rows.Where(s => s.Instance.Enabled).Sum(s => s.Status.Findings.Count(f => f.Severity == Severity.Critical));
        var warning = rows.Where(s => s.Instance.Enabled).Sum(s => s.Status.Findings.Count(f => f.Severity == Severity.Warning));
        var down = rows.Count(s => s.Instance.Enabled && s.Status.Reachable == false);

        var sb = new StringBuilder();
        sb.Append($"<h1>{H.E(Loc.T("Dashboard"))}</h1>");
        var healthy = rows.Count(s => s.Instance.Enabled && Rank(s.Instance, s.Status) == 3);
        var watched = rows.Count(s => s.Instance.Enabled);
        sb.Append($"""
            <div class="tiles">
              {Tile(critical == 0 ? "ok" : "bad", critical.ToString(), Loc.T("Critical problems"))}
              {Tile(warning == 0 ? "ok" : "warn", warning.ToString(), Loc.T("Warnings"))}
              {Tile(down == 0 ? "ok" : "bad", down.ToString(), Loc.T("Unreachable servers"))}
              {Tile(healthy == watched ? "ok" : "", $"{healthy} / {watched}", Loc.T("Servers without problems"))}
            </div>
            <p class="muted small">{H.E(Loc.T("Refreshes every minute. Times are UTC."))}</p>
            """);
        sb.Append(Categories(rows, null, false));
        sb.Append(Overview(rows, now));
        return sb.ToString();
    }

    /// <summary>All current problems on their own page, worst first, with the kind chips as filters.</summary>
    private static string ProblemsPage(List<Row> rows, DateTime now, Dictionary<string, AlertMute> mutes, string? kind, bool onlyCritical, bool claudeLinks) =>
        $"<h1>{H.E(Loc.T("Problems"))}</h1>" + Categories(rows, kind, onlyCritical) + ProblemList(rows, mutes, now, kind, onlyCritical, claudeLinks);

    /// <summary>Worst first: unreachable, then critical, then warning, then healthy; paused servers last.</summary>
    internal static int Rank(InstanceConfig instance, InstanceStatus s) =>
        !instance.Enabled ? 4
        : s.Reachable == false ? 0
        : s.Findings.Any(f => f.Severity == Severity.Critical) ? 1
        : s.Findings.Count > 0 ? 2
        : 3;

    private static string Overview(List<Row> rows, DateTime now)
    {
        var sb = new StringBuilder($"""
            <div class="card tablewrap"><table class="overview"><thead><tr>
            <th>{H.E(Loc.T("Server"))}</th><th>{H.E(Loc.T("Status"))}</th>
            <th class="num">{H.E(Loc.T("Critical"))}</th><th class="num">{H.E(Loc.T("Warning"))}</th>
            <th>{H.E(Loc.T("CPU"))}</th><th class="num">{H.E(Loc.T("Lowest disk free"))}</th>
            <th class="num">{H.E(Loc.T("SQL Server memory"))}</th><th class="num">{H.E(Loc.T("Page life"))}</th>
            <th class="num">{H.E(Loc.T("Oldest full backup"))}</th><th class="num">{H.E(Loc.T("Failed jobs"))}</th>
            <th class="num">{H.E(Loc.T("Last data"))}</th></tr></thead><tbody>
            """);
        var from = now.AddHours(-6);
        foreach (var (instance, s, cpu) in rows.OrderBy(x => Rank(x.Instance, x.Status)).ThenBy(x => x.Instance.Name, StringComparer.OrdinalIgnoreCase))
        {
            var state = !instance.Enabled ? Badge("off", Loc.T("paused"))
                : s.Reachable is null ? Badge("off", Loc.T("waiting for first data"))
                : s.Reachable == true ? Badge("ok", Loc.T("reachable"))
                : Badge("bad", Loc.T("UNREACHABLE"));
            // A paused server is not being checked: its old findings are not counted here, as in the totals above.
            var crit = !instance.Enabled ? 0 : s.Findings.Count(f => f.Severity == Severity.Critical);
            var warn = !instance.Enabled ? 0 : s.Findings.Count(f => f.Severity == Severity.Warning);
            var rowClass = !instance.Enabled ? " class=\"rowoff\"" : Rank(instance, s) switch
            {
                0 or 1 => " class=\"rowbad\"",
                2 => " class=\"rowwarn\"",
                _ => s.Reachable == true ? " class=\"rowok\"" : " class=\"rowoff\"",
            };
            var cpuNow = cpu.Count > 0 ? $"{cpu[^1].Value:0}%" : "";
            sb.Append($"""
                <tr{rowClass}>
                <td><a href="{ServerPage.Link(instance.Name, "problems")}"><strong>{H.E(instance.Name)}</strong></a></td><td>{state}</td>
                <td class="num">{(crit == 0 ? "-" : Badge("bad", crit.ToString()))}</td><td class="num">{(warn == 0 ? "-" : Badge("warn", warn.ToString()))}</td>
                <td>{Svg.Spark(cpu, from, now, 100)} <span class="muted small">{H.E(cpuNow)}</span></td>
                <td class="num">{Flag(s, instance, Disk, LowestDiskFree(s))}</td>
                <td class="num">{MemoryState(s, instance)} <span class="muted small">{H.E(SqlMemory(s))}</span></td><td class="num">{Flag(s, instance, Memory, PageLife(s))}</td>
                <td class="num">{H.E(OldestFullBackup(instance, s))}</td>
                <td class="num">{FailedJobs(instance, s)}</td>
                <td class="num small muted">{H.E(Fmt.Ago(s.LastDataUtc, now))}</td></tr>
                """);
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    // What a problem is about, from the middle part of its key (instance|kind|subject).
    private static readonly string[] Disk = ["disk", "diskfall"], Memory = ["ple", "grants"];
    private static readonly (string Id, string[] Kinds)[] Kinds =
    [
        ("down", ["unreachable", "collector"]),
        ("disk", Disk),
        ("memory", Memory),
        ("backup", ["backup", "dbcc"]),
        ("jobs", ["jobfail", "jobslow"]),
        ("databases", ["dbstate", "suspect", "log"]),
        ("load", ["cpu", "blocking", "deadlocks", "connections", "ioread", "iowrite"]),
    ];

    private static string KindLabel(string id) => id switch
    {
        "down" => Loc.T("Server down"),
        "disk" => Loc.T("Disk space"),
        "memory" => Loc.T("Memory"),
        "backup" => Loc.T("Backup and checks"),
        "jobs" => Loc.T("Agent jobs"),
        "databases" => Loc.T("Databases and log"),
        _ => Loc.T("CPU, blocking, I/O"),
    };

    private static string KindOf(Finding f)
    {
        var parts = f.Key.Split('|');
        return parts.Length > 1 ? parts[1] : "";
    }

    private static string? PickKind(string? kind) => Kinds.Any(c => c.Id == kind) ? kind : null;

    private static bool InCategory(Finding f, string id) => Kinds.First(c => c.Id == id).Kinds.Contains(KindOf(f));

    /// <summary>The value, as a badge when this server has an open problem of that kind (red if any is critical).</summary>
    private static string Flag(InstanceStatus s, InstanceConfig instance, string[] kinds, string text)
    {
        if (text == "-" || !instance.Enabled) return H.E(text);
        var hit = s.Findings.Where(f => kinds.Contains(KindOf(f))).ToList();
        return hit.Count == 0 ? H.E(text) : Badge(hit.Any(f => f.Severity == Severity.Critical) ? "bad" : "warn", text);
    }

    private static double? LatestValue(InstanceStatus s, string metric) =>
        s.Latest.FirstOrDefault(x => x.Sample.Metric == metric && x.Sample.Subject is null)?.Sample.Value;

    private static string SqlMemory(InstanceStatus s)
    {
        var used = LatestValue(s, "memory_used_mb");
        if (used is null) return "-";
        var target = LatestValue(s, "memory_target_mb");
        return target is > 0 ? $"{used / 1024:0.#} / {target / 1024:0.#} GB" : $"{used / 1024:0.#} GB";
    }

    /// <summary>Enough / Steady / Short, as SQL Server itself reports the memory state (sys.dm_os_sys_memory).</summary>
    private static string MemoryState(InstanceStatus s, InstanceConfig instance)
    {
        if (!instance.Enabled || LatestValue(s, "memory_os_state") is not { } code) return "-";
        return code switch
        {
            >= 3 => Badge("bad", Loc.T("Memory short")),
            >= 2 => Badge("ok", Loc.T("Memory steady")),
            _ => Badge("ok", Loc.T("Memory enough")),
        };
    }

    private static string PageLife(InstanceStatus s) =>
        LatestValue(s, "memory_ple_seconds") is { } ple ? $"{ple:0} s" : "-";

    /// <summary>One chip per kind of problem with how many there are, so "which are the disk ones" is one click. Click again to clear.</summary>
    private static string Categories(List<Row> rows, string? kind, bool onlyCritical)
    {
        var found = rows.Where(r => r.Instance.Enabled).SelectMany(r => r.Status.Findings).ToList();
        if (found.Count == 0) return "";
        static string Url(string? k, bool crit) =>
            k is null && !crit ? "/problems" : "/problems?" + string.Join('&', new[] { k is null ? null : "kind=" + k, crit ? "sev=critical" : null }.OfType<string>());
        var sb = new StringBuilder("<p class=\"chips\">");
        sb.Append($"<a class=\"chip{(kind is null && !onlyCritical ? " on" : "")}\" href=\"/problems\">{H.E(Loc.T("All problems"))} {found.Count}</a>");
        sb.Append($"<a class=\"chip{(onlyCritical && kind is null ? " on" : "")}\" href=\"{Url(null, true)}\">{H.E(Loc.T("Critical only"))} {found.Count(f => f.Severity == Severity.Critical)}</a>");
        foreach (var (id, _) in Kinds)
        {
            var mine = found.Where(f => InCategory(f, id)).ToList();
            if (mine.Count == 0) continue;
            var crit = mine.Count(f => f.Severity == Severity.Critical);
            sb.Append($"<a class=\"chip {(crit > 0 ? "bad" : "warn")}{(kind == id ? " on" : "")}\" href=\"{Url(kind == id ? null : id, onlyCritical)}\">{H.E(KindLabel(id))} {mine.Count}</a>");
        }
        return sb.Append("</p>").ToString();
    }

    private static string LowestDiskFree(InstanceStatus s)
    {
        double? lowest = null;
        foreach (var free in s.Latest.Where(x => x.Sample.Metric == "disk_free_bytes"))
        {
            var total = s.Latest.FirstOrDefault(x => x.Sample.Metric == "disk_total_bytes" && x.Sample.Subject == free.Sample.Subject)?.Sample.Value;
            if (total is > 0 && free.Sample.Value is not null)
                lowest = Math.Min(lowest ?? double.MaxValue, free.Sample.Value.Value / total.Value * 100);
        }
        return lowest is null ? "-" : $"{lowest:0.#}%";
    }

    private static string OldestFullBackup(InstanceConfig instance, InstanceStatus s)
    {
        var ages = s.Latest.Where(x => x.Sample.Metric == "last_full_backup_age_min" && !SqlLookout.Alerts.DatabaseFilter.IsIgnored(instance.IgnoredDatabases, x.Sample.Subject ?? ""))
            .Select(x => x.Sample.Value).ToList();
        if (ages.Count == 0) return "-";
        return ages.Any(a => a is null) ? Loc.T("none") : Fmt.Age(ages.Max());
    }

    private static string FailedJobs(InstanceConfig instance, InstanceStatus s)
    {
        if (!instance.Enabled) return "-";
        var failed = ServerPage.JobRows(s).Count(j => j.Outcome == 0);
        return failed == 0 ? "-" : Badge("bad", failed.ToString());
    }

    private static string ClaudeLink(Finding f, bool enabled) =>
        enabled && DeepLink.For(f) is { } link && DeepLink.PromptFor(f) is { } prompt
            ? $""" <a class="small" href="{H.E(link)}">{H.E(Loc.T("Investigate with Claude Code"))}</a>""" +
              $""" <details class="small"><summary>{H.E(Loc.T("Question as text"))}</summary><code>{H.E(prompt)}</code></details>"""
            : "";

    private static string ProblemList(List<Row> rows, Dictionary<string, AlertMute> mutes, DateTime now, string? kind, bool onlyCritical, bool claudeLinks)
    {
        var problems = rows.Where(s => s.Instance.Enabled)
            .SelectMany(s => s.Status.Findings.Select(f => (s.Instance, Finding: f)))
            .Where(x => kind is null || InCategory(x.Finding, kind))
            .Where(x => !onlyCritical || x.Finding.Severity == Severity.Critical)
            .OrderByDescending(x => x.Finding.Severity).ThenBy(x => x.Instance.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Finding.Title)
            .ToList();
        if (problems.Count == 0) return $"""<p class="notice ok">{H.E(Loc.T("No problems found."))}</p>""";

        var sb = new StringBuilder();
        foreach (var (instance, f) in problems)
        {
            var cls = f.Severity == Severity.Critical ? "bad" : "warn";
            var word = f.Severity == Severity.Critical ? Loc.T("Critical") : Loc.T("Warning");
            sb.Append($"""<div class="problem {cls}"><span class="sev">{H.E(word)}</span><div><a href="{ServerPage.Link(instance.Name, "problems")}">{H.E(instance.Name)}</a>: <strong>{H.E(f.Title)}</strong>{(mutes.GetValueOrDefault(f.Key) is { } m && m.Silences(f, now) ? " " + MutePage.Badge(m) : "")}<br><span class="muted small detail">{H.E(f.Detail)}</span>{ClaudeLink(f, claudeLinks)}</div></div>""");
        }
        return sb.ToString();
    }

    private static string Tile(string kind, string number, string label) =>
        $"""<div class="tile {kind}"><div class="n">{H.E(number)}</div><div class="l">{H.E(label)}</div></div>""";

    internal static string Badge(string kind, string text) => $"""<span class="badge {kind}">{H.E(text)}</span>""";
}
