using System.Text.Json;
using System.Text.Json.Serialization;
using SqlLookout.Alerts;
using SqlLookout.Core;

namespace SqlLookout.Service;

/// <summary>The two looks of the web pages. Classic is the default, so an update never changes how an existing install looks.</summary>
public static class Looks
{
    public const string Classic = "classic";
    public const string Terminal = "terminal";
    public static readonly string[] All = [Classic, Terminal];

    public static bool IsSupported(string? look) => look is Classic or Terminal;
}

public sealed class SqlLookoutConfig
{
    public string Database { get; set; } = "sqllookout.db";
    public int RetentionDays { get; set; } = 90;

    /// <summary>Language of the web pages and mails: "en" or "zh-TW".</summary>
    public string Language { get; set; } = Loc.English;

    /// <summary>Look of the web pages: "classic" (the original) or "terminal" (dense monospace). Same for everyone.</summary>
    public string Look { get; set; } = Looks.Classic;
    public List<InstanceConfig> Instances { get; set; } = [];

    /// <summary>The ignore list of one instance (empty when it has none or is not configured).</summary>
    public IReadOnlyList<string> IgnoredFor(string instance) =>
        Instances.FirstOrDefault(i => string.Equals(i.Name, instance, StringComparison.OrdinalIgnoreCase))?.IgnoredDatabases ?? [];

    /// <summary>The thresholds of one instance: the common ones with that instance's own values laid over them.</summary>
    public AlertThresholds ThresholdsFor(string instance)
    {
        if (Instances.FirstOrDefault(i => string.Equals(i.Name, instance, StringComparison.OrdinalIgnoreCase)) is not { ThresholdOverrides.Count: > 0 } found)
            return Alerts.Thresholds;
        // The common thresholds may have been changed since the overrides were saved; if they no longer fit together, the common ones apply.
        try { return Alerts.Thresholds.With(found.ThresholdOverrides); }
        catch (InvalidDataException) { return Alerts.Thresholds; }
    }

    /// <summary>Seconds between runs per collector name; anything not listed uses the collector's default.</summary>
    public Dictionary<string, int> IntervalsSeconds { get; set; } = [];
    public List<string> DisabledCollectors { get; set; } = [];

    public AlertSettings Alerts { get; set; } = new();

    public static SqlLookoutConfig Load(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var config = JsonSerializer.Deserialize<SqlLookoutConfig>(File.ReadAllText(path), options)
                     ?? throw new InvalidDataException("Config file is empty.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (!Loc.IsSupported(Language)) throw new InvalidDataException($"language must be one of: {string.Join(", ", Loc.Languages.Select(l => l.Code))}.");
        if (!Looks.IsSupported(Look)) throw new InvalidDataException($"look must be one of: {string.Join(", ", Looks.All)}.");
        if (Instances.Count == 0) throw new InvalidDataException("Config needs at least one entry in \"instances\".");
        var dup = Instances.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidDataException($"Instance name '{dup.Key}' appears twice.");
        foreach (var i in Instances) i.Validate();
        Alerts.Validate();
        foreach (var i in Instances.Where(i => i.ThresholdOverrides.Count > 0))
        {
            try { Alerts.Thresholds.With(i.ThresholdOverrides); }
            catch (InvalidDataException e) { throw new InvalidDataException($"Instance '{i.Name}': {e.Message}"); }
        }
    }
}

public sealed class InstanceConfig
{
    public string Name { get; set; } = "";

    /// <summary>Only for connection strings without a password (Windows authentication). A string with Password/Pwd is rejected.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>File whose whole content is the connection string. Keep it readable only by the service account.</summary>
    public string? ConnectionStringFile { get; set; }
    public string? ConnectionStringEnv { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Database names (wildcards * and ?) this instance is not judged on: test and scratch databases.</summary>
    public List<string> IgnoredDatabases { get; set; } = [];

    /// <summary>Threshold values for this instance only (config-file names, for example "DiskWarnFreePercent": 15). Everything not listed uses the common value.</summary>
    public Dictionary<string, double> ThresholdOverrides { get; set; } = [];

    /// <summary>
    /// Set by the web UI mode: the full connection string, built from the encrypted settings. It never comes from
    /// the JSON file, so a password cannot be smuggled into the file through this property.
    /// </summary>
    [JsonIgnore]
    public string? ResolvedConnectionString { get; set; }

    public void Validate()
    {
        if (ResolvedConnectionString is not null) return;
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("Every instance needs a \"name\".");
        if (ConnectionString is not null && ContainsPassword(ConnectionString))
            throw new InvalidDataException(
                $"Instance '{Name}': the connection string in the config file has a password. Put it in a file and use \"connectionStringFile\" (or an environment variable with \"connectionStringEnv\").");
        if (ConnectionString is null && ConnectionStringFile is null && ConnectionStringEnv is null)
            throw new InvalidDataException($"Instance '{Name}' needs connectionString, connectionStringFile or connectionStringEnv.");
    }

    public string ResolveConnectionString()
    {
        if (ResolvedConnectionString is not null) return ResolvedConnectionString;
        if (ConnectionStringFile is not null) return File.ReadAllText(ConnectionStringFile).Trim();
        if (ConnectionStringEnv is not null)
            return Environment.GetEnvironmentVariable(ConnectionStringEnv)
                   ?? throw new InvalidOperationException($"Environment variable {ConnectionStringEnv} (instance '{Name}') is not set.");
        return ConnectionString!;
    }

    private static bool ContainsPassword(string cs) =>
        cs.Contains("password", StringComparison.OrdinalIgnoreCase) || cs.Contains("pwd=", StringComparison.OrdinalIgnoreCase);
}

public sealed class AlertSettings
{
    public EmailSettings? Email { get; set; }
    /// <summary>Also post alerts to a Teams webhook. Works next to email or instead of it.</summary>
    public TeamsSettings? Teams { get; set; }
    public AlertThresholds Thresholds { get; set; } = new();
    public List<string> DisabledRules { get; set; } = [];
    public double RenotifyHours { get; set; } = 4;
    /// <summary>Warnings are not mailed at once; they appear in the daily summary. Critical problems still go out at once.</summary>
    public bool WarningsInSummaryOnly { get; set; }
    /// <summary>Adds a deep link with an investigation prompt to each mailed problem and each problem on the Problems page.</summary>
    public bool ClaudeLinks { get; set; }
    /// <summary>A problem that disappeared is only called resolved after staying away this long. 0 = resolve at once.</summary>
    public double ResolveAfterMinutes { get; set; } = 30;
    /// <summary>Local time, "HH:mm". Warnings are held back inside this window; criticals still go out. Both null = no quiet hours.</summary>
    public string? QuietStart { get; set; }
    public string? QuietEnd { get; set; }
    /// <summary>Local time of the daily summary mail, "HH:mm". Null turns the summary off.</summary>
    public string? SummaryTime { get; set; } = "08:00";

    /// <summary>
    /// 0 = every problem is mailed as soon as it is found. Above 0, non-urgent problems are collected and mailed together
    /// every this many hours, starting at <see cref="DigestStart"/>; only the urgent ones go out at once.
    /// </summary>
    public double DigestEveryHours { get; set; }
    /// <summary>Local time, "HH:mm", of the first digest each day.</summary>
    public string DigestStart { get; set; } = "08:00";
    /// <summary>Problem kinds (the middle part of the alert key) that are mailed at once, when critical, even with a digest. Cannot-connect, broken database, suspect pages.</summary>
    public List<string> UrgentKinds { get; set; } = ["unreachable", "dbstate", "suspect"];
    /// <summary>The same problem on several servers is written as one entry with the server names together.</summary>
    public bool MergeHosts { get; set; } = true;

    public void Validate()
    {
        Thresholds.Validate();
        if (DigestEveryHours is < 0 or > 24) throw new InvalidDataException(Loc.T("Send the digest every 0 to 24 hours."));
        ParseTime(DigestStart);
        if (RenotifyHours is < 0.1 or > 720) throw new InvalidDataException(Loc.T("Repeat a problem after 0.1 to 720 hours."));
        if (ResolveAfterMinutes is < 0 or > 1440) throw new InvalidDataException(Loc.T("Resolve a problem after 0 to 1440 minutes."));
        if (WarningsInSummaryOnly && SummaryTime is null)
            throw new InvalidDataException(Loc.T("Turn the daily summary on (give it a time) before sending warnings only there."));
        if ((QuietStart is null) != (QuietEnd is null)) throw new InvalidDataException(Loc.T("Quiet hours need both a start and an end, or neither."));
        if (QuietStart is not null) { ParseTime(QuietStart); ParseTime(QuietEnd!); }
        if (SummaryTime is not null) ParseTime(SummaryTime);
        if (Email is not null && (string.IsNullOrWhiteSpace(Email.Host) || Email.To.Count == 0 || string.IsNullOrWhiteSpace(Email.From)))
            throw new InvalidDataException(Loc.T("Email needs a server, a sender address and at least one recipient."));
    }

    public QuietHours? Quiet() => QuietStart is null ? null : new QuietHours(ParseTime(QuietStart), ParseTime(QuietEnd!));
    public DigestSchedule? Digest() => DigestEveryHours <= 0 ? null : new DigestSchedule(ParseTime(DigestStart), TimeSpan.FromHours(DigestEveryHours));
    public TimeSpan? Summary() => SummaryTime is null ? null : ParseTime(SummaryTime);

    private static TimeSpan ParseTime(string text) =>
        TimeSpan.TryParseExact(text, @"hh\:mm", null, out var t) ? t : throw new InvalidDataException(Loc.T("'{0}' is not a time like 08:00.", text));
}

public sealed class EmailSettings : SmtpSettings
{
    public string? PasswordFile { get; set; }
    public string? PasswordEnv { get; set; }

    /// <summary>Set by the web UI mode from the encrypted settings. Never read from or written to JSON.</summary>
    [JsonIgnore]
    public string? Password { get; set; }

    public string? ResolvePassword()
    {
        if (Password is not null) return Password;
        if (PasswordFile is not null) return File.ReadAllText(PasswordFile).Trim();
        if (PasswordEnv is not null) return Environment.GetEnvironmentVariable(PasswordEnv);
        return null;
    }
}
