using SqlLookout.Alerts;
using SqlLookout.Service;

namespace SqlLookout.Tests;

public class ThresholdTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        new AlertThresholds().Validate();
        new AlertSettings().Validate();
    }

    public static IEnumerable<object[]> Bad() =>
    [
        [new Action<AlertThresholds>(t => t.FullBackupWarnHours = 50), "backup age"],                 // above critical 48
        [new Action<AlertThresholds>(t => t.FullBackupWarnHours = 0), "backup age"],
        [new Action<AlertThresholds>(t => t.DbccWarnDays = 20), "DBCC"],
        [new Action<AlertThresholds>(t => t.LogWarnPercent = 95), "log usage"],
        [new Action<AlertThresholds>(t => t.LogCriticalPercent = 200), "log usage"],
        [new Action<AlertThresholds>(t => t.LogMinSizeMb = -1), "smallest log"],
        [new Action<AlertThresholds>(t => t.DiskWarnFreePercent = 150), "disk free space"],
        [new Action<AlertThresholds>(t => t.DiskWarnFreePercent = 5), "disk free space"],             // below critical 10
        [new Action<AlertThresholds>(t => t.DiskCriticalFreeGb = 50), "critical size"],               // above warning 10
        [new Action<AlertThresholds>(t => t.DiskFallWarnHours = 0.5), "fall rate"],                   // warning gives less time than critical 1
        [new Action<AlertThresholds>(t => t.DiskFallWindowHours = 0), "trend window"],
        [new Action<AlertThresholds>(t => t.UnreachableCriticalAfter = 1), "unreachable"],            // below warn 2
        [new Action<AlertThresholds>(t => t.UnreachableWarnAfter = 0), "unreachable"],
        [new Action<AlertThresholds>(t => t.JobRunningMultiplier = 0.5), "usual duration"],
        [new Action<AlertThresholds>(t => t.CollectorFailingAfter = 0), "at least 1"],
        [new Action<AlertThresholds>(t => t.CpuWarnPercent = 0), "warning percentage"],
        [new Action<AlertThresholds>(t => t.CpuWarnPercent = 101), "warning percentage"],
        [new Action<AlertThresholds>(t => t.CpuSustainedMinutes = 1), "sustained period"],
        [new Action<AlertThresholds>(t => t.CpuSustainedMinutes = 500), "sustained period"],
    ];

    [Theory]
    [MemberData(nameof(Bad))]
    public void Nonsense_combinations_are_refused_with_a_message(Action<AlertThresholds> change, string expected)
    {
        var t = new AlertThresholds();
        change(t);
        var ex = Assert.Throws<InvalidDataException>(t.Validate);
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(721)]
    [InlineData(double.PositiveInfinity)]
    public void Repeat_time_must_be_sensible(double hours) =>
        Assert.Throws<InvalidDataException>(() => new AlertSettings { RenotifyHours = hours }.Validate());

    [Fact]
    public void Settings_validation_includes_the_thresholds() =>
        Assert.Throws<InvalidDataException>(() => new AlertSettings { Thresholds = { LogWarnPercent = 99 } }.Validate());
}
