using Microsoft.Data.Sqlite;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Tests;

/// <summary>Thresholds for one server only: parsing what a person types, laying it over the common values, storing it, and the alert rules using it.</summary>
public sealed class ThresholdOverrideTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"sqllookout-ovr-{Guid.NewGuid():N}.db");
    private SettingsStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new SettingsStore(_file, new FakeSecretProtector());
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file)!, Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private static InstanceRecord Sql(string overrides = "") =>
        new("prod", "PROD01", AuthModes.Sql, "monitor", EncryptModes.Trust, true, false, "", overrides);

    [Fact]
    public void Parse_reads_lines_semicolons_and_ignores_case_and_blank_lines()
    {
        var o = AlertThresholds.ParseOverrides("diskwarnfreepercent = 15\n\n FailedLoginWarnCount=20 ;CpuWarnPercent = 85.5\r\n");

        Assert.Equal(3, o.Count);
        Assert.Equal(15, o["DiskWarnFreePercent"]);
        Assert.Equal(20, o["FailedLoginWarnCount"]);
        Assert.Equal(85.5, o["CpuWarnPercent"]);
        Assert.Empty(AlertThresholds.ParseOverrides(null));
        Assert.Empty(AlertThresholds.ParseOverrides("  "));
    }

    [Theory]
    [InlineData("NoSuchThreshold = 5", "not a threshold name")]
    [InlineData("DiskWarnFreePercent", "not like Name = value")]
    [InlineData("DiskWarnFreePercent = lots", "not a number")]
    [InlineData("DiskWarnFreePercent = NaN", "not a number")]
    [InlineData("FailedLoginWarnCount = 2.5", "whole number")]
    public void Parse_refuses_bad_lines_with_a_readable_message(string text, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() => AlertThresholds.ParseOverrides(text));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void With_replaces_only_the_listed_values_and_leaves_the_common_object_alone()
    {
        var common = new AlertThresholds();
        var own = common.With(AlertThresholds.ParseOverrides("FailedLoginWarnCount = 100\nFailedLoginCriticalCount = 500"));

        Assert.Equal(100, own.FailedLoginWarnCount);
        Assert.Equal(500, own.FailedLoginCriticalCount);
        Assert.Equal(common.DiskWarnFreePercent, own.DiskWarnFreePercent);   // untouched values carry over
        Assert.Equal(10, common.FailedLoginWarnCount);                       // the common object is not changed
    }

    [Fact]
    public void With_refuses_a_set_that_breaks_a_rule()
    {
        // warning above the (common) critical level of 50
        Assert.Throws<InvalidDataException>(() => new AlertThresholds().With(AlertThresholds.ParseOverrides("FailedLoginWarnCount = 80")));
    }

    [Fact]
    public void Evaluate_uses_the_values_of_the_server_it_judges()
    {
        var config = new SqlLookoutConfig
        {
            Instances =
            [
                new InstanceConfig { Name = "busy", ConnectionString = "x", ThresholdOverrides = new() { ["FailedLoginWarnCount"] = 100, ["FailedLoginCriticalCount"] = 500 } },
                new InstanceConfig { Name = "quiet", ConnectionString = "x" },
            ],
        };
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        StoredSample[] Latest(string i) => [new(i, "failed_logins", now, new Sample("failed_logins", null, 30))];

        Assert.Null(FailedLoginAlertRules.Evaluate("busy", Latest("busy"), config.ThresholdsFor("busy")));
        Assert.Equal(Severity.Warning, FailedLoginAlertRules.Evaluate("quiet", Latest("quiet"), config.ThresholdsFor("quiet"))!.Severity);
        Assert.Same(config.Alerts.Thresholds, config.ThresholdsFor("quiet"));
        Assert.Same(config.Alerts.Thresholds, config.ThresholdsFor("not configured"));
        Assert.Equal(100, config.ThresholdsFor("BUSY").FailedLoginWarnCount);   // name match ignores case
    }

    [Fact]
    public void Config_with_a_bad_override_is_refused_and_names_the_instance()
    {
        var config = new SqlLookoutConfig
        {
            Instances = [new InstanceConfig { Name = "busy", ConnectionString = "x", ThresholdOverrides = new() { ["FailedLoginWarnCount"] = 80 } }],
        };
        var e = Assert.Throws<InvalidDataException>(config.Validate);
        Assert.Contains("busy", e.Message);

        config.Instances[0].ThresholdOverrides = new() { ["Bogus"] = 1 };
        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void If_the_common_values_later_stop_fitting_the_server_falls_back_to_the_common_ones()
    {
        var config = new SqlLookoutConfig
        {
            Instances = [new InstanceConfig { Name = "busy", ConnectionString = "x", ThresholdOverrides = new() { ["FailedLoginWarnCount"] = 30 } }],
        };
        config.Alerts.Thresholds.FailedLoginCriticalCount = 20;   // critical now below the server's own warning

        Assert.Same(config.Alerts.Thresholds, config.ThresholdsFor("busy"));
    }

    [Fact]
    public async Task Store_saves_cleans_and_returns_overrides_and_they_reach_the_built_config()
    {
        await _store.SaveInstanceAsync(Sql(" failedloginwarncount = 100 ;FailedLoginCriticalCount=500\nDiskWarnFreePercent=15"), "pw", isNew: true);

        var saved = (await _store.GetInstanceAsync("prod"))!.ThresholdOverrides;
        Assert.Equal("DiskWarnFreePercent = 15\nFailedLoginCriticalCount = 500\nFailedLoginWarnCount = 100", saved);   // cleaned, ordered, names fixed
        var config = await _store.BuildConfigAsync();
        Assert.Equal(100, config.ThresholdsFor("prod").FailedLoginWarnCount);
        Assert.Equal(15, config.ThresholdsFor("prod").DiskWarnFreePercent);

        await _store.SaveInstanceAsync(Sql(""), "", isNew: false);   // clearing it works
        Assert.Equal("", (await _store.GetInstanceAsync("prod"))!.ThresholdOverrides);
        Assert.Equal(10, (await _store.BuildConfigAsync()).ThresholdsFor("prod").FailedLoginWarnCount);
    }

    [Fact]
    public void Parse_accepts_commas_between_entries()
    {
        var o = AlertThresholds.ParseOverrides("DiskWarnFreePercent = 15, CpuWarnPercent = 85");

        Assert.Equal(15, o["DiskWarnFreePercent"]);
        Assert.Equal(85, o["CpuWarnPercent"]);
    }

    [Fact]
    public async Task Saving_common_settings_that_would_break_a_servers_own_thresholds_is_refused()
    {
        await _store.SaveInstanceAsync(Sql("DiskWarnFreePercent = 25"), "pw", isNew: true);
        var settings = await _store.GetAlertSettingsAsync();
        settings.Thresholds.DiskWarnFreePercent = 40;
        settings.Thresholds.DiskCriticalFreePercent = 30;   // fine on its own, but above the server's own warning of 25

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAlertSettingsAsync(settings, null, false));
        Assert.Contains("prod", e.Message);

        settings.Thresholds.DiskCriticalFreePercent = 10;   // still fits
        settings.Thresholds.DiskWarnFreePercent = 30;
        await _store.SaveAlertSettingsAsync(settings, null, false);
    }

    [Fact]
    public async Task Store_refuses_unknown_names_and_sets_that_break_a_rule_and_keeps_what_was_saved()
    {
        await _store.SaveInstanceAsync(Sql("FailedLoginWarnCount = 100\nFailedLoginCriticalCount = 500"), "pw", isNew: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql("Bogus = 1"), "", isNew: false));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveInstanceAsync(Sql("FailedLoginWarnCount = 80"), "", isNew: false));

        Assert.Contains("FailedLoginWarnCount = 100", (await _store.GetInstanceAsync("prod"))!.ThresholdOverrides);
    }

    [Fact]
    public async Task Evaluator_judges_each_server_on_its_own_thresholds()
    {
        var historyFile = Path.Combine(Path.GetTempPath(), $"sqllookout-ovrh-{Guid.NewGuid():N}.db");
        var store = new SqliteHistoryStore(historyFile);
        await store.InitializeAsync();
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        foreach (var name in new[] { "busy", "quiet" })
            await store.AppendAsync(name, "failed_logins", now, TimeSpan.Zero, CollectionResult.Ok([new Sample("failed_logins", null, 30)]));
        var config = new SqlLookoutConfig
        {
            Instances =
            [
                new InstanceConfig { Name = "busy", ConnectionString = "x", ThresholdOverrides = new() { ["FailedLoginWarnCount"] = 100, ["FailedLoginCriticalCount"] = 500 } },
                new InstanceConfig { Name = "quiet", ConnectionString = "x" },
            ],
        };
        var evaluator = new AlertEvaluator(store, config.Alerts.Thresholds, new HashSet<string>(), config.IgnoredFor, config.ThresholdsFor);

        Assert.DoesNotContain(await evaluator.EvaluateAsync("busy", now), f => f.Key == "busy|failed_logins|");
        Assert.Contains(await evaluator.EvaluateAsync("quiet", now), f => f.Key == "quiet|failed_logins|");

        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(historyFile)!, Path.GetFileName(historyFile) + "*")) File.Delete(f);
    }
}
