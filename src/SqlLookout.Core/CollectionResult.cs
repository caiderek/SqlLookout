namespace SqlLookout.Core;

public enum CollectionStatus
{
    Ok,
    /// <summary>This version/edition cannot provide the data (e.g. a DMV that does not exist). Not an error.</summary>
    Unsupported,
    /// <summary>SQL Server refused the query for lack of permission. Message carries the error number.</summary>
    NoPermission,
    Failed,
}

/// <summary>One measured value. Subject is the thing measured (database name, drive); null for server-wide values.</summary>
public sealed record Sample(string Metric, string? Subject, double? Value, string? Text = null);

public sealed record CollectionResult(CollectionStatus Status, IReadOnlyList<Sample> Samples, string? Message = null)
{
    public static CollectionResult Ok(IReadOnlyList<Sample> samples) => new(CollectionStatus.Ok, samples);
    public static CollectionResult Unsupported(string reason) => new(CollectionStatus.Unsupported, [], reason);
}
