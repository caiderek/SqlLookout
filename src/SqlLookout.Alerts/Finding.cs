namespace SqlLookout.Alerts;

public enum Severity
{
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// One condition that is currently true. Key identifies it across runs, so "same problem" is recognised
/// and not mailed again (for example "srv|disk|C:\").
/// </summary>
public sealed record Finding(string Key, string Instance, Severity Severity, string Title, string Detail);
