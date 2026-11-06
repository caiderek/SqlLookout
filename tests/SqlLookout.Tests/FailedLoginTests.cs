using Microsoft.Data.SqlClient;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Core.Collectors;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>Failed logins: error-log line parsing, per-login summary, levels, newest run only, rule switch.</summary>
public sealed class FailedLoginTests
{
    private static readonly AlertThresholds T = new();
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static StoredSample S(string metric, string? subject, double? value, string? text = null, int minutesAgo = 0) =>
        new("srv", FailedLoginAlertRules.Collector, Now.AddMinutes(-minutesAgo), new Sample(metric, subject, value, text));

    private static Finding? Eval(params StoredSample[] latest) => FailedLoginAlertRules.Evaluate("srv", latest, T);

    [Theory]
    [InlineData(9, null)]
    [InlineData(10, Severity.Warning)]
    [InlineData(49, Severity.Warning)]
    [InlineData(50, Severity.Critical)]
    public void Levels_follow_the_count(double count, Severity? expected)
    {
        Assert.Equal(expected, Eval(S("failed_logins", null, count))?.Severity);
    }

    [Fact]
    public void Detail_lists_the_busiest_logins_with_their_clients()
    {
        var f = Eval(S("failed_logins", null, 30), S("failed_login_user", "sa", 25, "10.0.0.5, 10.0.0.6"), S("failed_login_user", "bob", 5))!;

        Assert.Equal("srv|failed_logins|", f.Key);
        Assert.Equal("Many failed logins", f.Title);
        Assert.Contains("30 failed login(s) in the last 15 minutes", f.Detail);
        Assert.Contains("sa: 25 failure(s) from 10.0.0.5, 10.0.0.6", f.Detail);
        Assert.Contains("bob: 5 failure(s)", f.Detail);
        Assert.True(f.Detail.IndexOf("sa:") < f.Detail.IndexOf("bob:"));
    }

    [Fact]
    public void Only_the_newest_run_counts()
    {
        // The newest run saw 2; the burst of 80 from the run before is already out of the window.
        Assert.Null(Eval(S("failed_logins", null, 80, minutesAgo: 5), S("failed_logins", null, 2)));

        // And the logins of the run before do not leak into the detail of the newest one.
        var f = Eval(S("failed_login_user", "gone", 70, minutesAgo: 5), S("failed_logins", null, 12), S("failed_login_user", "sa", 12))!;
        Assert.DoesNotContain("gone", f.Detail);
        Assert.Contains("sa: 12 failure(s)", f.Detail);
    }

    [Fact]
    public void The_rule_switch_applies_and_thresholds_must_be_ordered()
    {
        var samples = new[] { S("failed_logins", null, 60) };

        Assert.Empty(AlertRules.Evaluate("srv", samples, T, new HashSet<string> { AlertRules.RuleFailedLogins }));
        Assert.Single(AlertRules.Evaluate("srv", samples, T, new HashSet<string>()));
        Assert.Throws<InvalidDataException>(() => new AlertThresholds { FailedLoginWarnCount = 60 }.Validate());
    }

    [Fact]
    public void Parse_reads_login_and_client_from_the_error_log_line()
    {
        Assert.Equal(("sa", "10.1.2.3"),
            FailedLoginsCollector.Parse("Login failed for user 'sa'. Reason: Password did not match that for the login provided. [CLIENT: 10.1.2.3]"));
        Assert.Equal(("CORP\bob", "<local machine>"),
            FailedLoginsCollector.Parse("Login failed for user 'CORP\bob'. Reason: Could not find a login matching the name provided. [CLIENT: <local machine>]"));
        Assert.Equal(("x", ""), FailedLoginsCollector.Parse("Login failed for user 'x'."));
        Assert.Null(FailedLoginsCollector.Parse("Error: 18456, Severity: 14, State: 8."));
        Assert.Equal(("lookout", "<local machine>"), FailedLoginsCollector.Parse("Login failed for user 'lookout'. 原因: 找不到符合所提供名稱的登入。 [用戶端: <local machine>]"));
    }

    [Fact]
    public void Summarize_counts_per_login_and_ranks_clients()
    {
        string L(string u, string c) => $"Login failed for user '{u}'. Reason: Password did not match that for the login provided. [CLIENT: {c}]";
        var r = FailedLoginsCollector.Summarize([L("sa", "1.1.1.1"), L("bob", "2.2.2.2"), L("SA", "3.3.3.3"), L("sa", "3.3.3.3"), L("sa", "1.1.1.1"), "noise"]);

        Assert.Equal(2, r.Count);
        Assert.Equal(("sa", 4, "1.1.1.1, 3.3.3.3"), r[0]);
        Assert.Equal(("bob", 1, "2.2.2.2"), r[1]);
    }

    [Fact]
    public async Task Query_runs_on_a_real_server_and_counts_a_failed_login()
    {
        var cs = Environment.GetEnvironmentVariable("SQLLOOKOUT_TEST_CONNECTION")
                 ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        var server = await ServerInfo.ReadAsync(connection);
        var before = (await new FailedLoginsCollector().CollectAsync(connection, server, CancellationToken.None))
            .Samples.Single(s => s.Metric == "failed_logins").Value;

        // A real failed login: a SQL login that does not exist.
        var bad = new SqlConnectionStringBuilder(cs) { IntegratedSecurity = false, UserID = "lookout_no_such_login", Password = "x", Pooling = false, ConnectTimeout = 5 };
        await Assert.ThrowsAsync<SqlException>(async () => { await using var c = new SqlConnection(bad.ConnectionString); await c.OpenAsync(); });

        var after = await new FailedLoginsCollector().CollectAsync(connection, server, CancellationToken.None);
        Assert.Equal(before + 1, after.Samples.Single(s => s.Metric == "failed_logins").Value);
        Assert.Contains(after.Samples, s => s.Metric == "failed_login_user" && s.Subject == "lookout_no_such_login" && s.Value >= 1);
    }
}
