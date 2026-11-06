using Microsoft.Data.SqlClient;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Core.Collectors;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>The Express database size cap: version-dependent limit, one finding per instance, newest run only.</summary>
public sealed class ExpressLimitTests
{
    private static readonly AlertThresholds T = new();
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static StoredSample S(string metric, string? subject, double? value, int minutesAgo = 0) =>
        new("srv", HaAlertRules.ExpressCollector, Now.AddMinutes(-minutesAgo), new Sample(metric, subject, value));

    private static StoredSample Limit(double mb = 10240, int minutesAgo = 0) => S("express_limit_mb", null, mb, minutesAgo);
    private static StoredSample Data(string db, double mb, int minutesAgo = 0) => S("express_data_mb", db, mb, minutesAgo);

    private static Finding? Eval(params StoredSample[] latest) => HaAlertRules.ExpressLimit("srv", latest, T, _ => false);

    [Theory]
    [InlineData(8191, null)]
    [InlineData(8192, Severity.Warning)]
    [InlineData(9727, Severity.Warning)]
    [InlineData(9728, Severity.Critical)]
    [InlineData(10240, Severity.Critical)]
    public void Levels_follow_the_percentage_of_the_cap(double mb, Severity? expected)
    {
        Assert.Equal(expected, Eval(Limit(), Data("Sales", mb))?.Severity);
    }

    [Fact]
    public void Many_databases_make_one_finding_and_the_worst_sets_the_severity()
    {
        var f = Eval(Limit(), Data("A", 8500), Data("B", 10000), Data("C", 100))!;

        Assert.Equal("srv|express|", f.Key);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Express size cap nearly reached: 2 database(s)", f.Title);
        Assert.Contains("A: 8500 MB of 10240 MB (83%)", f.Detail);
        Assert.Contains("B: 10000 MB of 10240 MB (98%)", f.Detail);
        Assert.DoesNotContain("C:", f.Detail);
    }

    [Fact]
    public void Only_the_newest_run_counts_so_a_dropped_database_stops_alerting()
    {
        // The newest run (limit row only) no longer lists "Gone"; its reading from the run before must not alert.
        Assert.Null(Eval(Limit(), Data("Gone", 10000, minutesAgo: 15)));
    }

    [Fact]
    public void Without_a_limit_reading_nothing_is_judged()
    {
        Assert.Null(Eval(Data("Sales", 10000)));
    }

    [Fact]
    public void The_ignore_list_and_the_rule_switch_apply()
    {
        var samples = new[] { Limit(), Data("Scratch", 10000) };

        Assert.Null(HaAlertRules.ExpressLimit("srv", samples, T, db => db == "Scratch"));
        Assert.Empty(AlertRules.Evaluate("srv", samples, T, new HashSet<string> { AlertRules.RuleExpressLimit }));
        Assert.Single(AlertRules.Evaluate("srv", samples, T, new HashSet<string>()));
    }

    [Fact]
    public void Limit_is_4_GB_up_to_2008_10_GB_to_2022_and_50_GB_from_2025()
    {
        Assert.Equal(4096, ExpressLimitCollector.LimitMb(new ServerInfo(10, 0, 6000, 4)));
        Assert.Equal(10240, ExpressLimitCollector.LimitMb(new ServerInfo(10, 50, 6000, 4)));
        Assert.Equal(10240, ExpressLimitCollector.LimitMb(new ServerInfo(16, 0, 1000, 4)));
        Assert.Equal(51200, ExpressLimitCollector.LimitMb(new ServerInfo(17, 0, 1000, 4)));
    }

    [Fact]
    public void Only_Express_runs_the_collector()
    {
        var c = new ExpressLimitCollector();

        Assert.Null(c.UnsupportedReason(new ServerInfo(16, 0, 1000, 4)));
        Assert.NotNull(c.UnsupportedReason(new ServerInfo(16, 0, 1000, 3)));
    }

    [Fact]
    public void Thresholds_must_be_ordered_and_at_most_100()
    {
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { ExpressLimitWarnPercent = 96 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { ExpressLimitCriticalPercent = 101 }.Validate());
    }

    [Fact]
    public async Task Query_runs_on_a_real_server_and_counts_data_files_without_tempdb()
    {
        var cs = Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
                 ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        var server = await ServerInfo.ReadAsync(connection);

        // CollectAsync directly: the runner would skip this collector on a non-Express test server.
        var result = await new ExpressLimitCollector().CollectAsync(connection, server, CancellationToken.None);

        Assert.Contains(result.Samples, s => s.Metric == "express_limit_mb");
        var data = result.Samples.Where(s => s.Metric == "express_data_mb").ToList();
        Assert.Contains(data, s => s.Subject == "master" && s.Value > 0);
        Assert.DoesNotContain(data, s => s.Subject == "tempdb");
    }
}
