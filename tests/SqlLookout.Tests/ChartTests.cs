using SqlLookout.Storage;
using SqlLookout.Web;

namespace SqlLookout.Tests;

public class ChartTests
{
    private static readonly DateTime From = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private static List<(DateTime Utc, double Value)> Minutes(int count, Func<int, double> value) =>
        Enumerable.Range(0, count).Select(i => (From.AddMinutes(i), value(i))).ToList();

    [Fact]
    public void Downsample_keeps_a_short_spike_instead_of_averaging_it_away()
    {
        var series = Minutes(1440, i => i == 700 ? 99 : 1);          // one bad minute in a day

        var reduced = Svg.Downsample(series, From, From.AddMinutes(1440));

        Assert.True(reduced.Count <= 2 * Svg.MaxPoints);
        Assert.Contains(reduced, p => p.Value == 99);
    }

    [Fact]
    public void Downsample_keeps_a_short_dip_too_because_low_is_the_bad_direction_for_free_space()
    {
        var series = Minutes(43200, i => i == 20000 ? 3 : 500);       // 30 days of free GB with one short drop

        var reduced = Svg.Downsample(series, From, From.AddMinutes(43200));

        Assert.True(reduced.Count <= 2 * Svg.MaxPoints);
        Assert.Contains(reduced, p => p.Value == 3);
        Assert.Equal(reduced.OrderBy(p => p.Utc).ToList(), reduced.ToList());
    }

    [Fact]
    public void Downsample_leaves_small_series_alone_and_keeps_time_order()
    {
        var small = Minutes(50, i => i);
        Assert.Same(small, Svg.Downsample(small, From, From.AddMinutes(50)));

        var big = Svg.Downsample(Minutes(5000, i => i % 7), From, From.AddMinutes(5000));
        Assert.Equal(big.OrderBy(p => p.Utc).ToList(), big.ToList());
    }

    [Fact]
    public void Chart_without_two_points_says_there_is_no_history_instead_of_drawing_an_empty_graph()
    {
        var html = Svg.Chart("CPU", [new Svg.Line("x", Minutes(1, _ => 5))], From, From.AddHours(1), Svg.Percent);

        Assert.Contains("No history for this yet.", html);
        Assert.DoesNotContain("<svg", html);
    }

    [Fact]
    public void Chart_encodes_titles_and_labels_and_never_emits_scripts_or_inline_styles()
    {
        var html = Svg.Chart("<script>t()</script>", [new Svg.Line("<img src=x>", Minutes(10, i => i))], From, From.AddMinutes(10), Svg.Percent, threshold: 5);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("class=\"threshold\"", html);
    }

    [Fact]
    public void Chart_scales_to_the_data_and_to_a_fixed_maximum()
    {
        var data = Minutes(10, i => i);

        var fixedScale = Svg.Chart("CPU", [new Svg.Line("x", data)], From, From.AddMinutes(10), Svg.Percent, fixedMax: 100);
        Assert.Contains(">100%<", fixedScale);

        var auto = Svg.Chart("Free", [new Svg.Line("x", Minutes(10, i => 1000 + i))], From, From.AddMinutes(10), v => v.ToString("0"));
        Assert.DoesNotContain(">100%<", auto);
    }

    [Fact]
    public void Count_axes_use_whole_numbers_even_when_every_value_is_zero()
    {
        var zeros = Svg.Chart("Blocked", [new Svg.Line("x", Minutes(10, _ => 0))], From, From.AddMinutes(10), v => v.ToString("0.#"), minMax: 4);

        Assert.Contains(">4<", zeros);
        Assert.Contains(">2<", zeros);
        Assert.DoesNotContain(">0.3<", zeros);
        Assert.DoesNotContain(">0.8<", zeros);

        var big = Svg.Chart("PLE", [new Svg.Line("x", Minutes(10, i => 4269.8 * i / 9))], From, From.AddMinutes(10), v => v.ToString("0.#"));
        Assert.DoesNotContain(">4269.8</text>", big);                // axis labels are round numbers (the legend may show the raw last value)
    }

    [Fact]
    public void Free_space_axis_can_zoom_to_the_data_so_a_small_drop_is_visible()
    {
        var series = Minutes(20, i => 100 - i * 0.2);                 // 100 GB falling to 96.2 GB

        var zero = Svg.Chart("Free", [new Svg.Line("x", series)], From, From.AddMinutes(20), v => v.ToString("0.#"));
        var zoom = Svg.Chart("Free", [new Svg.Line("x", series)], From, From.AddMinutes(20), v => v.ToString("0.#"), zeroBased: false);

        Assert.Contains(">0<", zero);
        Assert.DoesNotContain(">0<", zoom);
    }

    [Fact]
    public void Spark_needs_two_points()
    {
        Assert.Equal("", Svg.Spark(Minutes(1, _ => 1), From, From.AddHours(1)));
        Assert.StartsWith("<svg class=\"spark\"", Svg.Spark(Minutes(5, i => i), From, From.AddHours(1), 100));
    }

    [Fact]
    public void Spark_marks_the_newest_reading_with_a_dot_on_the_end_of_the_line()
    {
        var svg = Svg.Spark(Minutes(5, i => i * 10), From, From.AddHours(1), 100);

        var path = System.Text.RegularExpressions.Regex.Match(svg, @"d=""[^""]*L([\d.]+) ([\d.]+)""");
        var dot = System.Text.RegularExpressions.Regex.Match(svg, @"<circle class=""end"" cx=""([\d.]+)"" cy=""([\d.]+)""");
        Assert.True(path.Success && dot.Success, svg);
        Assert.Equal(path.Groups[1].Value, dot.Groups[1].Value);
        Assert.Equal(path.Groups[2].Value, dot.Groups[2].Value);
    }

    [Fact]
    public async Task Series_query_matches_a_null_subject_and_a_named_one_separately()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sqllookout-series-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = new SqliteHistoryStore(file))
            {
                await store.InitializeAsync();
                await store.AppendAsync("i", "c", From, TimeSpan.Zero, SqlLookout.Core.CollectionResult.Ok(
                    [new SqlLookout.Core.Sample("m", null, 1), new SqlLookout.Core.Sample("m", "D:\\", 2)]));

                var none = await store.GetSeriesAsync("i", "m", null, From.AddHours(-1));
                var named = await store.GetSeriesAsync("i", "m", "D:\\", From.AddHours(-1));

                Assert.Equal([1.0], none.Select(p => p.Value));
                Assert.Equal([2.0], named.Select(p => p.Value));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + "*")) File.Delete(f);
        }
    }
}
