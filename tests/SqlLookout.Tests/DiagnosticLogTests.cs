using SqlLookout.Service;
using Xunit;

namespace SqlLookout.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqllookout-dl-" + Guid.NewGuid().ToString("N"));
    private DiagnosticLog Make() => new(Path.Combine(_dir, "logs", "d.log"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Off_writes_nothing()
    {
        var log = Make();
        log.Write("x", "hello");
        Assert.False(File.Exists(log.Path));
    }

    [Fact]
    public void On_appends_one_line_per_event_and_keeps_line_breaks_out()
    {
        var log = Make();
        log.Enabled = true;
        log.Write("problems", "a\nb");
        log.Write("settings", "c");
        var lines = log.Tail().Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Contains("[problems] a | b", lines[0]);
    }

    [Fact]
    public void Clear_removes_the_file_and_a_big_file_is_cut_to_its_newer_half()
    {
        var log = Make();
        log.Enabled = true;
        var chunk = new string('x', 1000);
        for (var i = 0; i < 1100; i++) log.Write("t", i + chunk);
        Assert.True(new FileInfo(log.Path).Length < DiagnosticLog.MaxBytes * 1.2);
        Assert.Contains("1099", log.Tail(1));
        log.Clear();
        Assert.False(File.Exists(log.Path));
    }
}
