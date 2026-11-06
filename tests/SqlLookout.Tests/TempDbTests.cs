using Microsoft.Data.SqlClient;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Core.Collectors;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>tempdb space: capacity from file limits and volume space, newest run only, one finding per instance.</summary>
public sealed class TempDbTests
{
    private static readonly AlertThresholds T = new();
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static StoredSample S(string metric, string? subject, double? value, string? text = null, int minutesAgo = 0) =>
        new("srv", TempDbAlertRules.Collector, Now.AddMinutes(-minutesAgo), new Sample(metric, subject, value, text));

    private static StoredSample[] Run(double used, double capacity = 1000, int minutesAgo = 0) =>
        [S("tempdb_used_mb", null, used, null, minutesAgo), S("tempdb_capacity_mb", null, capacity, null, minutesAgo),
         S("tempdb_user_mb", null, 10, null, minutesAgo), S("tempdb_internal_mb", null, 20, null, minutesAgo), S("tempdb_version_mb", null, 30, null, minutesAgo)];

    private static Finding? Eval(params StoredSample[] latest) => TempDbAlertRules.Evaluate("srv", latest, T);

    [Theory]
    [InlineData(799, null)]
    [InlineData(800, Severity.Warning)]
    [InlineData(949, Severity.Warning)]
    [InlineData(950, Severity.Critical)]
    public void Levels_follow_the_share_of_the_room_tempdb_can_reach(double used, Severity? expected)
    {
        Assert.Equal(expected, Eval(Run(used))?.Severity);
    }

    [Fact]
    public void The_finding_names_the_parts_and_the_biggest_sessions()
    {
        var f = Eval([.. Run(900), S("tempdb_session_mb", "55", 400, "app @ PC1 (Report)"), S("tempdb_session_mb", "60", 50, "x @ y (z)")])!;

        Assert.Equal("srv|tempdb|", f.Key);
        Assert.Equal("tempdb is 90% full", f.Title);
        Assert.Contains("User objects 10 MB, internal objects 20 MB, version store 30 MB", f.Detail);
        Assert.True(f.Detail.IndexOf("Session 55", StringComparison.Ordinal) < f.Detail.IndexOf("Session 60", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_newest_run_counts_and_no_capacity_means_no_judgement()
    {
        Assert.Null(Eval([.. Run(990, minutesAgo: 10), .. Run(100)]));
        Assert.Null(Eval(S("tempdb_used_mb", null, 990)));
    }

    [Fact]
    public void The_rule_switch_applies()
    {
        var samples = Run(990);

        Assert.Empty(AlertRules.Evaluate("srv", samples, T, new HashSet<string> { AlertRules.RuleTempDb }));
        Assert.Single(AlertRules.Evaluate("srv", samples, T, new HashSet<string>()));
    }

    [Fact]
    public void Thresholds_must_be_ordered_and_at_most_100()
    {
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { TempDbWarnPercent = 96 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { TempDbCriticalPercent = 101 }.Validate());
    }

    private static TempDbCollector.FileInfo F(double size, double? max, string? volume = "T:\\", double? free = 500) => new(size, max, volume, free);

    [Fact]
    public void Capacity_is_size_plus_room_limited_by_max_size_and_by_the_volume()
    {
        Assert.Equal(300, TempDbCollector.CapacityMb([F(100, 100), F(200, 200)]));           // no growth allowed
        Assert.Equal(400, TempDbCollector.CapacityMb([F(100, 400, free: 5000)]));             // max_size limits
        Assert.Equal(600, TempDbCollector.CapacityMb([F(100, 10000, free: 500)]));            // max_size is more than the volume has
        Assert.Equal(600, TempDbCollector.CapacityMb([F(100, null, free: 500)]));             // unlimited: volume limits
        Assert.Equal(700, TempDbCollector.CapacityMb([F(100, null, free: 500), F(100, null, free: 500)]));  // two files share one volume's space
        Assert.Equal(1200, TempDbCollector.CapacityMb([F(100, null, "T:\\", 500), F(100, null, "U:\\", 500)]));  // two volumes
    }

    [Fact]
    public void Unlimited_growth_with_unknown_volume_space_gives_no_capacity()
    {
        Assert.Null(TempDbCollector.CapacityMb([F(100, null, null, null)]));
        Assert.Equal(100, TempDbCollector.CapacityMb([F(100, 100, null, null)]));
    }

    [Fact]
    public async Task Query_runs_on_a_real_server_and_reads_tempdb()
    {
        var cs = Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
                 ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        var server = await ServerInfo.ReadAsync(connection);

        var result = await new TempDbCollector().CollectAsync(connection, server, CancellationToken.None);

        double V(string m) => result.Samples.First(s => s.Metric == m).Value!.Value;
        Assert.InRange(V("tempdb_used_mb"), 0.01, V("tempdb_capacity_mb"));
        Assert.True(V("tempdb_user_mb") + V("tempdb_internal_mb") + V("tempdb_version_mb") <= V("tempdb_used_mb") + 1);

        // The size must be what tempdb has now (autogrowth included), not the size it was configured with (sys.master_files).
        await using var size = connection.CreateCommand();
        size.CommandText = "SELECT SUM(CAST(size AS bigint)) * 8 / 1024.0 FROM tempdb.sys.database_files WHERE type = 0;";
        var currentMb = Convert.ToDouble(await size.ExecuteScalarAsync());
        Assert.True(V("tempdb_capacity_mb") >= currentMb - 1, $"capacity {V("tempdb_capacity_mb")} MB is below the real size {currentMb} MB");
    }
}
