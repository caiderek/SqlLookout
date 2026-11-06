using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Web;

/// <summary>One server on its own page, split into tabs so no tab is long: problems, databases, disks, jobs, performance, queries.</summary>
internal static class ServerPage
{
    internal static readonly string[] Tabs = ["problems", "databases", "disks", "jobs", "performance", "queries"];

    // Range name -> hours. The first entry is the default for the performance tab, "7d" for the disks tab.
    internal static readonly (string Key, int Hours)[] Ranges = [("6h", 6), ("24h", 24), ("7d", 168), ("30d", 720)];

    public static void Map(WebApplication app)
    {
        app.MapGet("/server/{name}", async (HttpContext ctx, IHistoryStore history, LiveConfig live, SettingsStore store, string name, string? tab, string? range, string? all) =>
        {
            var config = live.Current;
            var instance = config.Instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
                return SqlLookoutWeb.Show(ctx, Loc.T("Server not found"), NotFound(), status: 404);

            var now = DateTime.UtcNow;
            var status = await StatusReport.BuildAsync(history, config, instance.Name, now, ctx.RequestAborted);
            var current = Tabs.Contains(tab, StringComparer.Ordinal) ? tab! : "problems";
            var defaultRange = current == "disks" ? "7d" : "24h";
            var hours = Ranges.FirstOrDefault(r => r.Key == range);
            if (hours.Key is null) hours = Ranges.First(r => r.Key == defaultRange);

            var body = new StringBuilder();
            body.Append(Header(instance, status, now));
            body.Append(TabBar(instance.Name, current, status, instance.Enabled));
            body.Append(current switch
            {
                "databases" => Databases(status),
                "disks" => await DisksAsync(history, instance.Name, status, hours, now, ctx.RequestAborted),
                "jobs" => Jobs(status, all == "1", instance.Name),
                "queries" => await QueriesAsync(store, instance.Name, ctx.RequestAborted),
                "performance" => await PerformanceAsync(history, instance.Name, hours, now, config.ThresholdsFor(instance.Name), ctx.RequestAborted),
                _ => Problems(ctx, instance, status, await Mutes(history, ctx.RequestAborted), now),
            });
            return SqlLookoutWeb.Show(ctx, instance.Name, body.ToString(), refreshSeconds: current == "queries" ? 0 : 60);
        });
    }

    internal static string Link(string name, string tab, string? extra = null) =>
        $"/server/{Uri.EscapeDataString(name)}?tab={tab}{extra}";

    private static string NotFound() =>
        $"""<h1>{H.E(Loc.T("Server not found"))}</h1><p><a href="/">{H.E(Loc.T("Back to the dashboard"))}</a></p>""";

    private static string Header(InstanceConfig instance, InstanceStatus s, DateTime now)
    {
        var state = !instance.Enabled ? DashboardPage.Badge("off", Loc.T("paused"))
            : s.Reachable is null ? DashboardPage.Badge("off", Loc.T("waiting for first data"))
            : s.Reachable == true ? DashboardPage.Badge("ok", Loc.T("reachable"))
            : DashboardPage.Badge("bad", Loc.T("UNREACHABLE"));
        return $"""
            <p class="small"><a href="/">&larr; {H.E(Loc.T("Dashboard"))}</a></p>
            <h1>{H.E(instance.Name)} {state}</h1>
            <p class="muted small">{H.E(Loc.T("Last data: {0} ({1})", Fmt.Utc(s.LastDataUtc), Fmt.Ago(s.LastDataUtc, now)))}</p>
            """;
    }

    private static string TabBar(string name, string current, InstanceStatus s, bool enabled)
    {
        string Label(string tab) => tab switch
        {
            "problems" => Loc.T("Problems"),
            "databases" => Loc.T("Databases"),
            "disks" => Loc.T("Disks"),
            "jobs" => Loc.T("SQL Agent jobs"),
            "queries" => Loc.T("Queries"),
            _ => Loc.T("Performance"),
        };
        var sb = new StringBuilder("<nav class=\"tabs\">");
        foreach (var tab in Tabs)
        {
            var badge = "";
            if (tab == "problems" && enabled && s.Findings.Count > 0)
                badge = DashboardPage.Badge(s.Findings.Any(f => f.Severity == Severity.Critical) ? "bad" : "warn", s.Findings.Count.ToString());
            else if (tab == "jobs")
            {
                var n = JobRows(s).Count(j => j.Outcome != 1);          // a job that is merely running is not a problem
                if (n > 0) badge = DashboardPage.Badge("bad", n.ToString());
            }
            sb.Append($"""<a href="{Link(name, tab)}"{(tab == current ? " class=\"on\"" : "")}>{H.E(Label(tab))}{badge}</a>""");
        }
        return sb.Append("</nav>").ToString();
    }

    // ---------------------------------------------------------------- queries

    /// <summary>
    /// What the server is running now and the costliest cached statements, read live when the tab is opened (no automatic
    /// refresh, so text being read does not vanish). The text comes from the monitored server and is only ever shown escaped.
    /// </summary>
    private static async Task<string> QueriesAsync(SettingsStore store, string name, CancellationToken ct)
    {
        var (read, error) = await LiveConnection.RunAsync(store, name, async (connection, token) =>
            (Running: await LiveQueries.ReadRunningAsync(connection, token), Heavy: await LiveQueries.ReadHeaviestAsync(connection, token)), ct);
        if (error is not null) return $"""<p class="notice bad">{H.E(error)}</p>""";
        var running = read.Running;
        var heavy = read.Heavy;

        var sb = new StringBuilder();
        sb.Append($"""<p class="small"><a href="{Link(name, "queries")}">{H.E(Loc.T("Refresh"))}</a> <span class="muted">{H.E(Loc.T("Read just now from the server; query text can contain data."))}</span></p>""");

        sb.Append($"<h2>{H.E(Loc.T("Running now"))}</h2>");
        if (running.Count == 0)
            sb.Append($"""<p class="muted">{H.E(Loc.T("No user query is running at this moment."))}</p>""");
        else
        {
            if (running.Count > LiveQueries.RunningLimit)
            {
                running = running.Take(LiveQueries.RunningLimit).ToList();
                sb.Append($"""<p class="muted small">{H.E(Loc.T("Showing the {0} longest-running queries.", LiveQueries.RunningLimit))}</p>""");
            }
            sb.Append($"""<div class="card tablewrap"><table><thead><tr><th class="num">{H.E(Loc.T("Session"))}</th><th>{H.E(Loc.T("User"))}</th><th>{H.E(Loc.T("Database"))}</th><th class="num">{H.E(Loc.T("Running for"))}</th><th>{H.E(Loc.T("Waiting on"))}</th><th class="num">{H.E(Loc.T("CPU"))}</th><th class="num">{H.E(Loc.T("Reads"))}</th><th>{H.E(Loc.T("Statement"))}</th></tr></thead><tbody>""");
            foreach (var q in running)
            {
                var wait = q.BlockedBy > 0 ? Loc.T("session {0}", q.BlockedBy) : q.WaitType ?? "";
                var user = string.IsNullOrEmpty(q.Host) ? q.Login : $"{q.Login}@{q.Host}";
                var waitCell = q.BlockedBy > 0 ? DashboardPage.Badge("bad", wait) : H.E(wait);
                sb.Append("<tr><td class=\"num\">" + q.SessionId + "</td><td>" + H.E(user) + "</td><td>" + H.E(q.Database) + "</td><td class=\"num\">" + H.E(LiveQueries.Duration(q.ElapsedMs)) + "</td>"
                    + "<td>" + waitCell + "</td><td class=\"num\">" + H.E(LiveQueries.Duration(q.CpuMs)) + "</td><td class=\"num\">" + q.Reads.ToString("N0") + "</td>"
                    + "<td class=\"small\">" + H.E(q.Text) + "</td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }

        sb.Append($"<h2>{H.E(Loc.T("Costliest cached statements"))}</h2>");
        sb.Append($"""<p class="muted small">{H.E(Loc.T("Ranked by total time since each statement entered the plan cache; a restart or cache clear resets this. Times are the server's own clock."))}</p>""");
        if (heavy.Count == 0)
            sb.Append($"""<p class="muted">{H.E(Loc.T("Nothing in the plan cache yet."))}</p>""");
        else
        {
            sb.Append($"""<div class="card tablewrap"><table><thead><tr><th class="num">{H.E(Loc.T("Runs"))}</th><th class="num">{H.E(Loc.T("Total time"))}</th><th class="num">{H.E(Loc.T("Average time"))}</th><th class="num">{H.E(Loc.T("Average CPU"))}</th><th class="num">{H.E(Loc.T("Average reads"))}</th><th>{H.E(Loc.T("Last run"))}</th><th>{H.E(Loc.T("Database"))}</th><th>{H.E(Loc.T("Statement"))}</th></tr></thead><tbody>""");
            foreach (var q in heavy)
            {
                var last = q.LastRunServerTime?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
                sb.Append("<tr><td class=\"num\">" + q.Executions.ToString("N0") + "</td><td class=\"num\">" + H.E(LiveQueries.Duration(q.TotalElapsedMs)) + "</td><td class=\"num\">" + H.E(LiveQueries.Duration(q.AvgElapsedMs)) + "</td>"
                    + "<td class=\"num\">" + H.E(LiveQueries.Duration(q.AvgCpuMs)) + "</td><td class=\"num\">" + q.AvgReads.ToString("N0") + "</td>"
                    + "<td>" + H.E(last) + "</td><td>" + H.E(q.Database) + "</td><td class=\"small\">" + H.E(q.Text) + "</td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }
        return sb.ToString();
    }

// ---------------------------------------------------------------- problems

    internal static async Task<Dictionary<string, AlertMute>> Mutes(IHistoryStore history, CancellationToken ct) =>
        (await AlertMutes.ListAsync(history, ct)).ToDictionary(m => m.Key);

    private static string Problems(HttpContext ctx, InstanceConfig instance, InstanceStatus s, Dictionary<string, AlertMute> mutes, DateTime now)
    {
        var sb = new StringBuilder();
        if (s.Reachable == false)
            sb.Append($"""<p class="notice bad">{H.E(Loc.T("Could not connect: {0}.", s.ConnectMessage))} <a href="/instances">{H.E(Loc.T("Check the server settings"))}</a>.</p>""");

        if (instance.Enabled && s.Findings.Count > 0)
        {
            foreach (var f in s.Findings)
            {
                var cls = f.Severity == Severity.Critical ? "bad" : "warn";
                var word = f.Severity == Severity.Critical ? Loc.T("Critical") : Loc.T("Warning");
                sb.Append($"""<div class="problem {cls}"><span class="sev">{H.E(word)}</span><div><strong>{H.E(f.Title)}</strong><br><span class="muted small detail">{H.E(f.Detail)}</span><br>{MutePage.Controls(ctx, instance.Name, f, mutes.GetValueOrDefault(f.Key), now)}</div></div>""");
            }
        }
        else if (instance.Enabled && s.Reachable == true)
        {
            sb.Append($"""<p class="notice ok">{H.E(Loc.T("No problems found."))}</p>""");
        }

        var skipped = s.Collectors.Where(c => c.Status != CollectionStatus.Ok).ToList();
        if (skipped.Count > 0)
        {
            sb.Append($"<h3>{H.E(Loc.T("Checks that did not run"))}</h3><ul class=\"small\">");
            foreach (var c in skipped)
            {
                // The stored reason is the English sentence the check wrote; it is translated when shown.
                var why = c.Status switch
                {
                    CollectionStatus.Unsupported => Loc.T("not available on this server ({0})", Loc.T(c.Message ?? "")),
                    CollectionStatus.NoPermission => Loc.T("the monitoring login is not allowed to read it ({0}); run sql/create-readonly-login.sql", c.Message),
                    _ => Loc.T("failed ({0})", c.Message),
                };
                sb.Append($"<li><strong>{H.E(c.Name)}</strong>: {H.E(why)}</li>");
            }
            sb.Append("</ul>");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- databases

    private static string Databases(InstanceStatus s)
    {
        var map = new Dictionary<(string Metric, string Subject), Sample>();
        foreach (var x in s.Latest) map.TryAdd((x.Sample.Metric, x.Sample.Subject ?? ""), x.Sample);
        Sample? Get(string metric, string db) => map.TryGetValue((metric, db), out var v) ? v : null;

        var names = s.Latest.Where(x => x.Sample.Metric == "db_state").Select(x => x.Sample.Subject!).Distinct().ToList();
        if (names.Count == 0) return $"""<p class="muted">{H.E(Loc.T("No data yet."))}</p>""";

        // Databases that need a look come first, then the rest by name.
        bool Attention(string db)
        {
            var state = Get("db_state", db);
            var backup = Get("last_full_backup_age_min", db);
            var logPct = Get("log_used_percent", db)?.Value;
            return state?.Value != 0 || (backup is not null && (backup.Value is null || backup.Value > 26 * 60)) || logPct >= 80;
        }
        var dbs = names.OrderByDescending(Attention).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder($"<div class=\"card tablewrap\"><table><thead><tr>"
            + $"<th>{H.E(Loc.T("Database"))}</th><th>{H.E(Loc.T("State"))}</th><th class=\"num\">{H.E(Loc.T("Last full backup"))}</th>"
            + $"<th class=\"num\">{H.E(Loc.T("Log used"))}</th><th class=\"num\">{H.E(Loc.T("Data size"))}</th><th class=\"num\">{H.E(Loc.T("Log size"))}</th>"
            + $"<th class=\"num\">{H.E(Loc.T("Last CHECKDB"))}</th></tr></thead><tbody>");
        foreach (var db in dbs)
        {
            var state = Get("db_state", db);
            var stateBadge = state?.Value == 0 ? DashboardPage.Badge("ok", state.Text ?? "ONLINE") : DashboardPage.Badge(state?.Value is 1 or 2 ? "warn" : "bad", state?.Text ?? "?");
            var backup = Get("last_full_backup_age_min", db);
            var backupText = backup is null ? "-" : backup.Value is null ? Loc.T("none") : Fmt.Age(backup.Value);
            var logPct = Get("log_used_percent", db)?.Value;
            var dbcc = Get("dbcc_last_good_age_min", db);
            var dbccText = dbcc is null ? "-" : dbcc.Value is null ? Loc.T(dbcc.Text ?? "unknown") : Fmt.Age(dbcc.Value);
            sb.Append($"""
                <tr><td>{H.E(db)}</td><td>{stateBadge}</td><td class="num">{H.E(backupText)}</td>
                <td class="num">{(logPct is null ? "-" : H.E($"{logPct:0}%"))}</td>
                <td class="num">{H.E(Fmt.Mb(Get("db_data_mb", db)?.Value))}</td><td class="num">{H.E(Fmt.Mb(Get("db_log_mb", db)?.Value))}</td>
                <td class="num">{H.E(dbccText)}</td></tr>
                """);
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    // ---------------------------------------------------------------- disks

    private static async Task<string> DisksAsync(IHistoryStore history, string instance, InstanceStatus s, (string Key, int Hours) range, DateTime now, CancellationToken ct)
    {
        var volumes = s.Latest.Where(x => x.Sample.Metric == "disk_free_bytes").Select(x => x.Sample.Subject!).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (volumes.Count == 0) return $"""<p class="muted">{H.E(Loc.T("No data yet."))}</p>""";

        var sb = new StringBuilder($"<div class=\"card tablewrap\"><table><thead><tr><th>{H.E(Loc.T("Volume"))}</th>"
            + $"<th class=\"num\">{H.E(Loc.T("Free"))}</th><th class=\"num\">{H.E(Loc.T("Total"))}</th><th class=\"num\">{H.E(Loc.T("Free %"))}</th></tr></thead><tbody>");
        foreach (var v in volumes)
        {
            var free = s.Latest.First(x => x.Sample.Metric == "disk_free_bytes" && x.Sample.Subject == v).Sample.Value;
            var total = s.Latest.FirstOrDefault(x => x.Sample.Metric == "disk_total_bytes" && x.Sample.Subject == v)?.Sample.Value;
            var pct = total is > 0 && free is not null ? $"{free / total * 100:0.#}%" : "-";
            sb.Append($"<tr><td>{H.E(v)}</td><td class=\"num\">{H.E(Fmt.Gb(free))}</td><td class=\"num\">{H.E(Fmt.Gb(total))}</td><td class=\"num\">{H.E(pct)}</td></tr>");
        }
        sb.Append("</tbody></table></div>");

        var from = now.AddHours(-range.Hours);
        sb.Append(RangeBar(instance, "disks", range.Key));
        sb.Append("<div class=\"chartgrid\">");
        foreach (var v in volumes)
        {
            var series = await history.GetSeriesAsync(instance, "disk_free_bytes", v, from, ct);
            sb.Append(Svg.Chart(Loc.T("Free space on {0}", v), [new Svg.Line(Loc.T("Free"), series)], from, now, x => Fmt.Gb(x), zeroBased: false));
        }
        return sb.Append("</div>").ToString();
    }

    // ---------------------------------------------------------------- jobs

    internal sealed record JobRow(string Name, int? Outcome, double? RunningMin, double? AvgMin)
    {
        // Anything that did not simply succeed last time, or is running right now.
        public bool NeedsAttention => Outcome != 1 || RunningMin is not null;
    }

    internal static List<JobRow> JobRows(InstanceStatus s)
    {
        var running = s.Latest.Where(x => x.Sample.Metric == "job_running_min").ToDictionary(x => x.Sample.Subject ?? "", x => x.Sample.Value, StringComparer.OrdinalIgnoreCase);
        var avg = s.Latest.Where(x => x.Sample.Metric == "job_avg_duration_min").ToDictionary(x => x.Sample.Subject ?? "", x => x.Sample.Value, StringComparer.OrdinalIgnoreCase);
        return s.Latest.Where(x => x.Sample.Metric == "job_last_outcome")
            .Select(x => x.Sample)
            .OrderBy(x => x.Subject, StringComparer.OrdinalIgnoreCase)
            .Select(j => new JobRow(j.Subject ?? "", j.Value is null ? null : (int)j.Value,
                running.GetValueOrDefault(j.Subject ?? ""), avg.GetValueOrDefault(j.Subject ?? "")))
            .ToList();
    }

    private static string Jobs(InstanceStatus s, bool showAll, string name)
    {
        var jobs = JobRows(s);
        if (jobs.Count == 0) return $"""<p class="muted">{H.E(Loc.T("No data yet."))}</p>""";

        var attention = jobs.Where(j => j.NeedsAttention).ToList();
        var shown = showAll ? jobs : attention;
        var hidden = jobs.Count - attention.Count;

        var sb = new StringBuilder();
        if (!showAll && attention.Count == 0)
            sb.Append($"""<p class="notice ok">{H.E(Loc.T("All {0} jobs succeeded on their last run.", jobs.Count))}</p>""");
        if (shown.Count > 0)
        {
            sb.Append($"<div class=\"card tablewrap\"><table><thead><tr><th>{H.E(Loc.T("Job"))}</th>"
                + $"<th>{H.E(Loc.T("Last run"))}</th><th class=\"num\">{H.E(Loc.T("Running for"))}</th><th class=\"num\">{H.E(Loc.T("Usual duration"))}</th></tr></thead><tbody>");
            // Needs-attention rows first when everything is shown, so the long list does not bury them.
            foreach (var j in shown.OrderByDescending(j => j.NeedsAttention).ThenBy(j => j.Name, StringComparer.OrdinalIgnoreCase))
            {
                var outcome = j.Outcome switch
                {
                    1 => DashboardPage.Badge("ok", Loc.T("succeeded")),
                    0 => DashboardPage.Badge("bad", Loc.T("FAILED")),
                    3 => DashboardPage.Badge("warn", Loc.T("canceled")),
                    _ => DashboardPage.Badge("off", Loc.T("unknown")),
                };
                sb.Append($"<tr><td>{H.E(j.Name)}</td><td>{outcome}</td><td class=\"num\">{H.E(j.RunningMin is null ? "-" : Fmt.Age(j.RunningMin))}</td><td class=\"num\">{H.E(j.AvgMin is null ? "-" : Fmt.Age(j.AvgMin))}</td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }
        if (showAll)
            sb.Append($"""<p class="more small"><a href="{Link(name, "jobs")}">{H.E(Loc.T("Show only jobs that need attention"))}</a></p>""");
        else if (hidden > 0)
            sb.Append($"""<p class="more small muted">{H.E(Loc.T("{0} more jobs succeeded on their last run.", hidden))} <a href="{Link(name, "jobs", "&all=1")}">{H.E(Loc.T("Show all {0} jobs", jobs.Count))}</a></p>""");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- performance

    private static async Task<string> PerformanceAsync(IHistoryStore history, string instance, (string Key, int Hours) range, DateTime now, AlertThresholds thresholds, CancellationToken ct)
    {
        var from = now.AddHours(-range.Hours);
        async Task<IReadOnlyList<(DateTime Utc, double Value)>> Series(string metric) => await history.GetSeriesAsync(instance, metric, null, from, ct);

        var cpuTotal = await Series("cpu_total_percent");
        var cpuSql = await Series("cpu_sql_percent");
        var memUsed = await Series("memory_used_mb");
        var memTarget = await Series("memory_target_mb");
        var ple = await Series("memory_ple_seconds");
        var conns = await Series("connections_user");
        var running = await Series("connections_running");
        var blocked = await Series("blocked_sessions");
        var blockWait = await Series("blocking_longest_wait_sec");
        var deadlocks = await Series("deadlocks_total");
        var readLat = await Series("io_read_latency_ms");
        var writeLat = await Series("io_write_latency_ms");

        string Count(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        string Ms(double v) => Loc.T("{0:0.#} ms", v);
        string Sec(double v) => Loc.T("{0:0.#} s", v);

        var sb = new StringBuilder(RangeBar(instance, "performance", range.Key));
        sb.Append($"""<p class="small"><a href="/server/{Uri.EscapeDataString(instance)}/export.csv?range={range.Key}">{H.E(Loc.T("Download CSV"))}</a></p>""");
        sb.Append("<div class=\"chartgrid\">");
        sb.Append(Svg.Chart(Loc.T("CPU"), [new Svg.Line(Loc.T("Machine"), cpuTotal), new Svg.Line(Loc.T("SQL Server"), cpuSql, 1)],
            from, now, Svg.Percent, threshold: thresholds.CpuWarnPercent, fixedMax: 100));
        sb.Append(Svg.Chart(Loc.T("Memory"), [new Svg.Line(Loc.T("Used"), memUsed), new Svg.Line(Loc.T("Target"), memTarget, 1)], from, now, v => Fmt.Mb(v)));
        sb.Append(Svg.Chart(Loc.T("Page life expectancy"), [new Svg.Line(Loc.T("Seconds"), ple)], from, now, Count));
        sb.Append(Svg.Chart(Loc.T("Connections"), [new Svg.Line(Loc.T("User sessions"), conns), new Svg.Line(Loc.T("Running"), running, 1)], from, now, Count));
        sb.Append(Svg.Chart(Loc.T("Blocked sessions"), [new Svg.Line(Loc.T("Blocked"), blocked, 3)], from, now, Count, minMax: 4));
        sb.Append(Svg.Chart(Loc.T("Longest blocking wait"), [new Svg.Line(Loc.T("Wait"), blockWait, 3)], from, now, Sec, minMax: 4));
        sb.Append(Svg.Chart(Loc.T("Disk I/O latency"), [new Svg.Line(Loc.T("Read"), readLat), new Svg.Line(Loc.T("Write"), writeLat, 1)], from, now, Ms));
        sb.Append(Svg.Chart(Loc.T("Deadlocks"), [new Svg.Line(Loc.T("Running total"), deadlocks, 3)], from, now, Count, minMax: 4));
        return sb.Append("</div>").ToString();
    }

    private static string RangeBar(string name, string tab, string current)
    {
        var sb = new StringBuilder($"<div class=\"ranges small\"><span class=\"muted\">{H.E(Loc.T("Time range"))}:</span>");
        foreach (var (key, _) in Ranges)
            sb.Append($"""<a href="{Link(name, tab, "&range=" + key)}"{(key == current ? " class=\"on\"" : "")}>{key}</a>""");
        return sb.Append("</div>").ToString();
    }
}
