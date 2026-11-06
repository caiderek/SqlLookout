using SqlLookout.Core;

namespace SqlLookout.Alerts;

/// <summary>Default limits. Every value can be overridden in the config file, and per server.</summary>
public sealed class AlertThresholds
{
    public double FullBackupWarnHours { get; set; } = 26;
    public double FullBackupCriticalHours { get; set; } = 48;

    public double DbccWarnDays { get; set; } = 8;
    public double DbccCriticalDays { get; set; } = 15;

    // A volume alerts when free space is under the percentage OR under the absolute size.
    public double DiskWarnFreePercent { get; set; } = 20;
    public double DiskCriticalFreePercent { get; set; } = 10;
    public double DiskWarnFreeGb { get; set; } = 10;
    public double DiskCriticalFreeGb { get; set; } = 5;

    /// <summary>Alert when the recent fall rate predicts a full disk within this many hours.</summary>
    public double DiskFallWarnHours { get; set; } = 4;
    public double DiskFallCriticalHours { get; set; } = 1;
    public double DiskFallWindowHours { get; set; } = 2;

    public double LogWarnPercent { get; set; } = 80;
    public double LogCriticalPercent { get; set; } = 90;
    /// <summary>Logs smaller than this never alert on percentage; 95% of a 2 MB log means nothing.</summary>
    public double LogMinSizeMb { get; set; } = 100;

    /// <summary>Alert when machine CPU stays at or above this for the whole sustained period.</summary>
    public double CpuWarnPercent { get; set; } = 90;
    public double CpuSustainedMinutes { get; set; } = 10;

    /// <summary>Alert when a blocked session has been waiting this long right now (seconds).</summary>
    public double BlockingWarnSeconds { get; set; } = 60;
    public double BlockingCriticalSeconds { get; set; } = 300;

    /// <summary>Alert when page life expectancy stays below this many seconds for the whole sustained period.</summary>
    public double MemoryPleWarnSeconds { get; set; } = 300;
    public double MemoryPleSustainedMinutes { get; set; } = 10;
    /// <summary>Alert when at least one query waited for a memory grant at every check for this long.</summary>
    public double MemoryGrantsPendingMinutes { get; set; } = 5;

    /// <summary>Alert when this many deadlocks happened within the window (hours).</summary>
    public double DeadlockWindowHours { get; set; } = 1;
    public int DeadlockWarnCount { get; set; } = 3;
    public int DeadlockCriticalCount { get; set; } = 20;

    /// <summary>Alert when user connections stay at or above these counts for the whole sustained period.</summary>
    public int ConnectionsWarnCount { get; set; } = 500;
    public int ConnectionsCriticalCount { get; set; } = 1500;
    public double ConnectionsSustainedMinutes { get; set; } = 5;

    /// <summary>Alert when average read or write latency (ms, whole instance) stays at or above these for the whole sustained period.</summary>
    public double IoLatencyWarnMs { get; set; } = 50;
    public double IoLatencyCriticalMs { get; set; } = 150;
    public double IoLatencySustainedMinutes { get; set; } = 15;

    /// <summary>Log shipping is late once its age passes the limit set up in log shipping itself; it is critical at this many times that limit.</summary>
    public double LogShippingCriticalMultiplier { get; set; } = 2;

    /// <summary>SQL Express only: percentage of the edition's database size cap at which a database is reported.</summary>
    public double ExpressLimitWarnPercent { get; set; } = 80;
    public double ExpressLimitCriticalPercent { get; set; } = 95;

    /// <summary>Percentage of the room tempdb can reach (its size now plus what it may still grow into) that is in use.</summary>
    public double TempDbWarnPercent { get; set; } = 80;
    public double TempDbCriticalPercent { get; set; } = 95;

    /// <summary>Failed logins within the collector's window (15 minutes) at which an alert is raised.</summary>
    public int FailedLoginWarnCount { get; set; } = 10;
    public int FailedLoginCriticalCount { get; set; } = 50;

    public int UnreachableWarnAfter { get; set; } = 2;
    public int UnreachableCriticalAfter { get; set; } = 3;

    public double JobRunningMultiplier { get; set; } = 2;
    public double JobRunningMinMinutes { get; set; } = 10;

    public int CollectorFailingAfter { get; set; } = 3;

    /// <summary>Rejects limits that cannot work together (a warning above its critical level, a percentage over 100...).</summary>
    private static readonly System.Reflection.PropertyInfo[] Settable =
        typeof(AlertThresholds).GetProperties().Where(p => p.CanWrite && p.PropertyType == typeof(double) || p.CanWrite && p.PropertyType == typeof(int)).ToArray();

    /// <summary>Names a per-server override may use: every threshold, as written in the config file.</summary>
    public static IEnumerable<string> OverridableNames => Settable.Select(p => p.Name);

    /// <summary>
    /// Reads what a person typed for one server: one "Name = value" per line (commas and semicolons also work). Names are
    /// the threshold names of the config file, case does not matter. Throws a readable error for an unknown name or a bad number.
    /// </summary>
    public static Dictionary<string, double> ParseOverrides(string? text)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var line in text.Split([(char)10, (char)13, ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new InvalidDataException(Loc.T("'{0}' is not like Name = value.", line));
            var name = line[..eq].Trim();
            var property = Settable.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException(Loc.T("'{0}' is not a threshold name.", name));
            if (!double.TryParse(line[(eq + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException(Loc.T("The value of {0} is not a number.", property.Name));
            if (property.PropertyType == typeof(int) && value != Math.Floor(value))
                throw new InvalidDataException(Loc.T("{0} must be a whole number.", property.Name));
            result[property.Name] = value;
        }
        return result;
    }

    /// <summary>The overrides as text, in a stable order, ready to be stored and shown again.</summary>
    public static string FormatOverrides(IReadOnlyDictionary<string, double> overrides) =>
        string.Join("\n", overrides.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(o => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{o.Key} = {o.Value}")));

    /// <summary>A copy with the given values replaced, checked as a whole. Throws if the result breaks a rule (for example warning above critical).</summary>
    public AlertThresholds With(IReadOnlyDictionary<string, double>? overrides)
    {
        var copy = (AlertThresholds)MemberwiseClone();
        if (overrides is not null)
            foreach (var (name, value) in overrides)
            {
                var property = Settable.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                               ?? throw new InvalidDataException(Loc.T("'{0}' is not a threshold name.", name));
                property.SetValue(copy, property.PropertyType == typeof(int) ? (object)(int)value : value);
            }
        copy.Validate();
        return copy;
    }

    public void Validate()
    {
        static void Order(double warn, double critical, string what)
        {
            if (!(warn > 0 && critical > 0 && warn < critical))
                throw new InvalidDataException(Loc.T("For {0}, the warning level must be above zero and lower than the critical level.", what));
        }

        Order(FullBackupWarnHours, FullBackupCriticalHours, Loc.T("backup age"));
        Order(DbccWarnDays, DbccCriticalDays, Loc.T("DBCC CHECKDB age"));
        Order(LogWarnPercent, LogCriticalPercent, Loc.T("log usage"));
        if (LogCriticalPercent > 100) throw new InvalidDataException(Loc.T("For log usage, the percentages cannot be above 100."));
        if (LogMinSizeMb < 0) throw new InvalidDataException(Loc.T("The smallest log size to watch cannot be negative."));

        if (!(DiskCriticalFreePercent > 0 && DiskWarnFreePercent > DiskCriticalFreePercent && DiskWarnFreePercent <= 100))
            throw new InvalidDataException(Loc.T("For disk free space, the critical percentage must be above zero and lower than the warning percentage, which cannot be above 100."));
        if (!(DiskCriticalFreeGb >= 0 && DiskWarnFreeGb >= DiskCriticalFreeGb))
            throw new InvalidDataException(Loc.T("For disk free space, the critical size in GB cannot be larger than the warning size."));

        // Here the warning gives MORE time than the critical level: it fires earlier.
        if (!(DiskFallCriticalHours > 0 && DiskFallWarnHours > DiskFallCriticalHours))
            throw new InvalidDataException(Loc.T("For the disk fall rate, the warning hours must be more than the critical hours, and both above zero."));
        if (DiskFallWindowHours is < 0.5 or > 48)
            throw new InvalidDataException(Loc.T("The disk trend window must be between 0.5 and 48 hours."));

        if (CpuWarnPercent is <= 0 or > 100) throw new InvalidDataException(Loc.T("For CPU, the warning percentage must be above 0 and at most 100."));
        if (CpuSustainedMinutes is < 2 or > 240) throw new InvalidDataException(Loc.T("For CPU, the sustained period must be between 2 and 240 minutes."));

        Order(BlockingWarnSeconds, BlockingCriticalSeconds, Loc.T("blocking"));
        if (MemoryPleWarnSeconds <= 0) throw new InvalidDataException(Loc.T("For page life expectancy, the limit must be above zero."));
        if (MemoryPleSustainedMinutes is < 2 or > 240) throw new InvalidDataException(Loc.T("For page life expectancy, the sustained period must be between 2 and 240 minutes."));
        if (MemoryGrantsPendingMinutes is < 2 or > 240) throw new InvalidDataException(Loc.T("For memory grants, the sustained period must be between 2 and 240 minutes."));
        if (DeadlockWindowHours is < 0.25 or > 48) throw new InvalidDataException(Loc.T("The deadlock window must be between 0.25 and 48 hours."));
        Order(DeadlockWarnCount, DeadlockCriticalCount, Loc.T("deadlocks"));

        Order(ConnectionsWarnCount, ConnectionsCriticalCount, Loc.T("connections"));
        if (ConnectionsSustainedMinutes is < 2 or > 240) throw new InvalidDataException(Loc.T("For connections, the sustained period must be between 2 and 240 minutes."));
        Order(IoLatencyWarnMs, IoLatencyCriticalMs, Loc.T("disk I/O latency"));
        if (IoLatencySustainedMinutes is < 5 or > 240) throw new InvalidDataException(Loc.T("For disk I/O latency, the sustained period must be between 5 and 240 minutes."));

        if (LogShippingCriticalMultiplier <= 1) throw new InvalidDataException(Loc.T("For log shipping, the critical level must be more than 1 times the limit set up in log shipping."));
        Order(ExpressLimitWarnPercent, ExpressLimitCriticalPercent, Loc.T("the Express size cap"));
        if (ExpressLimitCriticalPercent > 100) throw new InvalidDataException(Loc.T("For the Express size cap, the percentages cannot be above 100."));
        Order(TempDbWarnPercent, TempDbCriticalPercent, Loc.T("tempdb usage"));
        if (TempDbCriticalPercent > 100) throw new InvalidDataException(Loc.T("For tempdb usage, the percentages cannot be above 100."));
        Order(FailedLoginWarnCount, FailedLoginCriticalCount, Loc.T("failed logins"));
        if (!(UnreachableWarnAfter >= 1 && UnreachableCriticalAfter >= UnreachableWarnAfter))
            throw new InvalidDataException(Loc.T("For unreachable servers, the critical count must be at least the warning count, and both at least 1."));
        if (JobRunningMultiplier < 1) throw new InvalidDataException(Loc.T("A job counts as running long only at 1 times its usual duration or more."));
        if (JobRunningMinMinutes < 0) throw new InvalidDataException(Loc.T("The minimum job running time cannot be negative."));
        if (CollectorFailingAfter < 1) throw new InvalidDataException(Loc.T("A check must fail at least 1 time in a row to be reported."));
    }
}
