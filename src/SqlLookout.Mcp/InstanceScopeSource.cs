namespace SqlLookout.Mcp;

/// <summary>Where a tool call gets the instances it may show: everything for stdio, the key's list for the remote endpoint.</summary>
public interface IInstanceScopeSource
{
    InstanceScope Current { get; }
}

public sealed class AllInstancesScopeSource : IInstanceScopeSource
{
    public InstanceScope Current => InstanceScope.All;
}
