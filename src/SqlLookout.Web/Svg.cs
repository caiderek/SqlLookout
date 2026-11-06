using System.Globalization;
using System.Text;
using SqlLookout.Core;

namespace SqlLookout.Web;

/// <summary>
/// Server-side SVG charts. The pages carry no JavaScript and the content security policy allows no inline styles, so
/// colours come from classes in <see cref="Css"/> and every number is computed here.
/// </summary>
public static class Svg
{
    public const int MaxPoints = 200;

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Thins a series to at most <paramref name="max"/> equal time slices and keeps the highest AND the lowest point of
    /// each (so up to twice <paramref name="max"/> points), in time order. A short spike (CPU) and a short dip (free
    /// space, page life expectancy) both survive; averaging would hide either. Small series are returned as they are.
    /// </summary>
    public static IReadOnlyList<(DateTime Utc, double Value)> Downsample(IReadOnlyList<(DateTime Utc, double Value)> points, DateTime from, DateTime to, int max = MaxPoints)
    {
        if (points.Count <= max) return points;
        var span = (to - from).TotalSeconds;
        if (span <= 0) return points;
        var high = new (DateTime Utc, double Value)?[max];
        var low = new (DateTime Utc, double Value)?[max];
        foreach (var p in points)
        {
            var slot = (int)Math.Clamp((p.Utc - from).TotalSeconds / span * max, 0, max - 1);
            if (high[slot] is null || p.Value > high[slot]!.Value.Value) high[slot] = p;
            if (low[slot] is null || p.Value < low[slot]!.Value.Value) low[slot] = p;
        }
        var result = new List<(DateTime Utc, double Value)>(max * 2);
        for (var i = 0; i < max; i++)
        {
            if (high[i] is not { } h) continue;
            var l = low[i]!.Value;
            if (l.Utc == h.Utc) result.Add(h);
            else if (l.Utc < h.Utc) { result.Add(l); result.Add(h); }
            else { result.Add(h); result.Add(l); }
        }
        return result;
    }

    /// <summary>A small line without axes, for the overview table. Empty string when there is nothing to draw.</summary>
    public static string Spark(IReadOnlyList<(DateTime Utc, double Value)> points, DateTime from, DateTime to, double? fixedMax = null)
    {
        if (points.Count < 2) return "";
        points = Downsample(points, from, to, 60);
        const double w = 120, h = 28, pad = 2;
        var max = fixedMax ?? Math.Max(points.Max(p => p.Value), 1e-9);
        var span = Math.Max((to - from).TotalSeconds, 1);
        var sb = new StringBuilder();
        double lastX = 0, lastY = 0;
        foreach (var p in points)
        {
            lastX = pad + Math.Clamp((p.Utc - from).TotalSeconds / span, 0, 1) * (w - 2 * pad);
            lastY = h - pad - Math.Clamp(p.Value / max, 0, 1) * (h - 2 * pad);
            sb.Append(sb.Length == 0 ? "M" : "L").Append(N(lastX)).Append(' ').Append(N(lastY)).Append(' ');
        }
        // The dot marks the newest reading, so the current value is the first thing the eye finds on the line.
        return $"""<svg class="spark" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" aria-label="{H.E(Loc.T("trend"))}"><path class="line" d="{sb.ToString().TrimEnd()}" fill="none"/><circle class="end" cx="{N(lastX)}" cy="{N(lastY)}" r="2.4"/></svg>""";
    }

    public sealed record Line(string Label, IReadOnlyList<(DateTime Utc, double Value)> Points, int Style = 0);

    /// <summary>
    /// A line chart with a value axis, a time axis and an optional dashed threshold. <paramref name="format"/> turns a
    /// value into axis and legend text. Shows a plain notice instead when no line has two points yet.
    /// </summary>
    public static string Chart(string title, IReadOnlyList<Line> lines, DateTime from, DateTime to, Func<double, string> format,
        double? threshold = null, double? fixedMax = null, double fixedMin = 0, double minMax = 0, bool zeroBased = true)
    {
        var drawable = lines.Where(l => l.Points.Count >= 2).ToList();
        var head = $"<h4>{H.E(title)}</h4>";
        if (drawable.Count == 0)
            return $"""<div class="chart">{head}<p class="muted small">{H.E(Loc.T("No history for this yet."))}</p></div>""";

        const double w = 640, h = 190, left = 56, right = 12, top = 10, bottom = 24;
        var lo = fixedMin;
        var hi = fixedMax ?? drawable.Max(l => l.Points.Max(p => p.Value));
        if (threshold is { } t && fixedMax is null) hi = Math.Max(hi, t);
        hi = Math.Max(hi, minMax);                                  // counts: never a 0-1 axis with decimal labels
        if (!zeroBased && fixedMax is null)
        {
            // A value that moves little (free disk space) would be a flat line on a 0-based axis: zoom to the data.
            var minV = drawable.Min(l => l.Points.Min(p => p.Value));
            var range = Math.Max(hi - minV, Math.Abs(hi) * 0.02);
            if (range <= 0) range = 1;
            var zoomStep = NiceStep(range * 1.25 / 4);
            lo = Math.Floor(minV / zoomStep - 0.25) * zoomStep;
            hi = lo + 4 * NiceStep(Math.Max(hi - lo, zoomStep) / 4);
        }
        else if (hi <= lo) hi = lo + 1;
        else if (fixedMax is null) hi = lo + 4 * NiceStep((hi - lo) / 4);
        var span = Math.Max((to - from).TotalSeconds, 1);

        double X(DateTime u) => left + Math.Clamp((u - from).TotalSeconds / span, 0, 1) * (w - left - right);
        double Y(double v) => top + (1 - Math.Clamp((v - lo) / (hi - lo), 0, 1)) * (h - top - bottom);

        var sb = new StringBuilder();
        sb.Append($"""<svg class="chartsvg" viewBox="0 0 {w} {h}" role="img" aria-label="{H.E(title)}">""");
        for (var i = 0; i <= 4; i++)
        {
            var v = lo + (hi - lo) * i / 4;
            var y = Y(v);
            sb.Append($"""<line class="grid" x1="{N(left)}" x2="{N(w - right)}" y1="{N(y)}" y2="{N(y)}"/>""");
            sb.Append($"""<text class="tick" x="{N(left - 6)}" y="{N(y + 4)}" text-anchor="end">{H.E(format(v))}</text>""");
        }
        sb.Append($"""<text class="tick" x="{N(left)}" y="{N(h - 6)}" text-anchor="start">{H.E(from.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))}</text>""");
        sb.Append($"""<text class="tick" x="{N(w - right)}" y="{N(h - 6)}" text-anchor="end">{H.E(to.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))} UTC</text>""");
        if (threshold is { } th && th >= lo && th <= hi)
            sb.Append($"""<line class="threshold" x1="{N(left)}" x2="{N(w - right)}" y1="{N(Y(th))}" y2="{N(Y(th))}"/>""");

        foreach (var line in drawable)
        {
            var pts = Downsample(line.Points, from, to);
            var path = new StringBuilder();
            foreach (var p in pts) path.Append(path.Length == 0 ? "M" : "L").Append(N(X(p.Utc))).Append(' ').Append(N(Y(p.Value))).Append(' ');
            sb.Append($"""<path class="line s{line.Style % 4}" d="{path.ToString().TrimEnd()}" fill="none"/>""");
        }
        sb.Append("</svg>");

        var legend = new StringBuilder();
        foreach (var line in drawable)
            legend.Append($"""<span class="key"><i class="sw s{line.Style % 4}"></i>{H.E(line.Label)}: <strong>{H.E(format(line.Points[^1].Value))}</strong></span> """);

        return $"""<div class="chart">{head}{sb}<p class="legend small">{legend}</p></div>""";
    }

    public static string Percent(double v) => N(v) + "%";

    /// <summary>The smallest round step (1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8 times a power of ten) that is at least <paramref name="raw"/>, so the axis labels are clean numbers.</summary>
    internal static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var f in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (f * magnitude >= raw * 0.999999) return f * magnitude;
        return 10 * magnitude;
    }
}
