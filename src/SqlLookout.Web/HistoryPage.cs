using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Web;

/// <summary>
/// Past problems (every time one was open, with when it ended) and CSV downloads: the same list, and one server's
/// performance numbers. The CSV files open in Excel (UTF-8 with a byte-order mark).
/// </summary>
internal static class HistoryPage
{
    private static readonly (string Key, int Days)[] Ranges = [("24h", 1), ("7d", 7), ("30d", 30), ("90d", 90)];

    // The performance metrics the charts use, in the order of the CSV columns.
    internal static readonly string[] PerformanceMetrics =
    [
        "cpu_total_percent", "cpu_sql_percent", "memory_used_mb", "memory_target_mb", "memory_ple_seconds",
        "connections_user", "connections_running", "blocked_sessions", "blocking_longest_wait_sec",
        "deadlocks_total", "io_read_latency_ms", "io_write_latency_ms",
    ];

    public static void Map(WebApplication app)
    {
        app.MapGet("/history", async (HttpContext ctx, IHistoryStore history, LiveConfig live, string? server, string? range) =>
        {
            var config = live.Current;
            var (key, days) = PickRange(range);
            var instance = PickInstance(config, server);
            var now = DateTime.UtcNow;
            var rows = await history.GetAlertLogAsync(now.AddDays(-days), instance, ctx.RequestAborted);
            return SqlLookoutWeb.Show(ctx, Loc.T("History"), Render(config, instance, key, rows, now));
        });

        app.MapGet("/history.csv", async (HttpContext ctx, IHistoryStore history, LiveConfig live, string? server, string? range) =>
        {
            var (_, days) = PickRange(range);
            var instance = PickInstance(live.Current, server);
            var now = DateTime.UtcNow;
            var rows = await history.GetAlertLogAsync(now.AddDays(-days), instance, ctx.RequestAborted);
            return Csv(AlertCsv(rows, now), $"sqllookout-problems-{now:yyyyMMdd-HHmm}.csv");
        });

        app.MapGet("/server/{name}/export.csv", async (HttpContext ctx, IHistoryStore history, LiveConfig live, string name, string? range) =>
        {
            var instance = live.Current.Instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (instance is null) return Results.NotFound();
            var (_, days) = PickRange(range);
            var now = DateTime.UtcNow;
            var from = now.AddDays(-days);
            var series = new List<IReadOnlyList<(DateTime Utc, double Value)>>();
            foreach (var metric in PerformanceMetrics)
                series.Add(await history.GetSeriesAsync(instance.Name, metric, null, from, ctx.RequestAborted));
            return Csv(PerformanceCsv(series), $"sqllookout-{FileSafe(instance.Name)}-performance-{now:yyyyMMdd-HHmm}.csv");
        });
    }

    internal static string Link(string? server, string range) =>
        "/history?range=" + range + (server is null ? "" : "&server=" + Uri.EscapeDataString(server));

    private static (string Key, int Days) PickRange(string? range)
    {
        var hit = Ranges.FirstOrDefault(r => r.Key == range);
        return hit.Key is null ? Ranges[1] : hit;
    }

    /// <summary>The configured server's own spelling, or null for all servers (an unknown name also means all).</summary>
    private static string? PickInstance(SqlLookoutConfig config, string? server) =>
        config.Instances.FirstOrDefault(i => string.Equals(i.Name, server, StringComparison.OrdinalIgnoreCase))?.Name;

    private static string Render(SqlLookoutConfig config, string? instance, string range, IReadOnlyList<AlertRecord> rows, DateTime now)
    {
        var sb = new StringBuilder($"<h1>{H.E(Loc.T("History"))}</h1>");
        sb.Append($"<p class=\"muted small\">{H.E(Loc.T("Every time a problem was found, and when it went away. Starts from the day this version was installed."))}</p>");

        sb.Append("<form method=\"get\" action=\"/history\" class=\"inline\">");
        sb.Append($"<label class=\"small\">{H.E(Loc.T("Server"))} <select name=\"server\"><option value=\"\">{H.E(Loc.T("All servers"))}</option>");
        foreach (var i in config.Instances)
            sb.Append($"<option value=\"{H.E(i.Name)}\"{(i.Name == instance ? " selected" : "")}>{H.E(i.Name)}</option>");
        sb.Append("</select></label> ");
        sb.Append($"<label class=\"small\">{H.E(Loc.T("Time range"))} <select name=\"range\">");
        foreach (var (key, _) in Ranges)
            sb.Append($"<option value=\"{key}\"{(key == range ? " selected" : "")}>{key}</option>");
        sb.Append($"</select></label> <button>{H.E(Loc.T("Show"))}</button></form>");

        var csv = "/history.csv?range=" + range + (instance is null ? "" : "&server=" + Uri.EscapeDataString(instance));
        sb.Append($"<p class=\"small\"><a href=\"{H.E(csv)}\">{H.E(Loc.T("Download CSV"))}</a></p>");

        if (rows.Count == 0)
        {
            sb.Append($"<p class=\"muted\">{H.E(Loc.T("No problems recorded in this period."))}</p>");
            return sb.ToString();
        }
        sb.Append($"<div class=\"card tablewrap\"><table><thead><tr><th>{H.E(Loc.T("Server"))}</th><th>{H.E(Loc.T("Severity"))}</th><th>{H.E(Loc.T("Problem"))}</th><th>{H.E(Loc.T("Started"))}</th><th>{H.E(Loc.T("Ended"))}</th><th class=\"num\">{H.E(Loc.T("Lasted"))}</th></tr></thead><tbody>");
        foreach (var r in rows)
        {
            var end = r.ResolvedUtc;
            var lasted = Fmt.Age(((end ?? now) - r.FirstUtc).TotalMinutes);
            var badge = DashboardPage.Badge(r.Severity == "Critical" ? "bad" : "warn", Loc.T(r.Severity == "Critical" ? "Critical" : "Warning"));
            sb.Append("<tr><td>" + H.E(r.Instance) + "</td><td>" + badge + "</td><td class=\"small\">" + H.E(r.Title) + "</td><td>" + H.E(Fmt.Utc(r.FirstUtc))
                + "</td><td>" + (end is null ? DashboardPage.Badge("warn", Loc.T("still open")) : H.E(Fmt.Utc(end))) + "</td><td class=\"num\">" + H.E(lasted) + "</td></tr>");
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    // ---------------------------------------------------------------- CSV

    internal static string AlertCsv(IReadOnlyList<AlertRecord> rows, DateTime now)
    {
        var sb = new StringBuilder("server,severity,problem,detail,started_utc,ended_utc,minutes\r\n");
        foreach (var r in rows)
        {
            sb.Append(Cell(r.Instance)).Append(',').Append(Cell(r.Severity)).Append(',').Append(Cell(r.Title)).Append(',').Append(Cell(r.Detail)).Append(',')
              .Append(Time(r.FirstUtc)).Append(',').Append(r.ResolvedUtc is { } e ? Time(e) : "").Append(',')
              .Append(((r.ResolvedUtc ?? now) - r.FirstUtc).TotalMinutes.ToString("0", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>One row per collection time, one column per metric; a metric not measured at that moment is left empty.</summary>
    internal static string PerformanceCsv(IReadOnlyList<IReadOnlyList<(DateTime Utc, double Value)>> series)
    {
        var sb = new StringBuilder("time_utc," + string.Join(',', PerformanceMetrics) + "\r\n");
        var byTime = new SortedDictionary<DateTime, string?[]>();
        for (var c = 0; c < series.Count; c++)
            foreach (var (utc, value) in series[c])
            {
                if (!byTime.TryGetValue(utc, out var row)) byTime[utc] = row = new string?[PerformanceMetrics.Length];
                row[c] = value.ToString("0.##", CultureInfo.InvariantCulture);
            }
        foreach (var (utc, row) in byTime)
            sb.Append(Time(utc)).Append(',').Append(string.Join(',', row.Select(v => v ?? ""))).Append("\r\n");
        return sb.ToString();
    }

    private static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Quoted when needed. Text that a spreadsheet would run as a formula gets a leading apostrophe-free guard (a single quote).</summary>
    internal static string Cell(string? text)
    {
        text ??= "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static string FileSafe(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static IResult Csv(string text, string fileName)
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", fileName);
    }
}
