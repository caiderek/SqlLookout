namespace SqlLookout.Mcp;

/// <summary>
/// Which instances a caller may see. The stdio server is local and sees everything; a key for the remote endpoint can be
/// limited to some instances. Names are compared without regard to case, like the instance names in the settings.
/// </summary>
public sealed class InstanceScope
{
    private readonly HashSet<string>? _allowed;

    public InstanceScope(IEnumerable<string>? allowed) =>
        _allowed = allowed is null ? null : new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

    public static InstanceScope All { get; } = new(null);

    public bool IsAll => _allowed is null;

    public bool Allows(string instance) => _allowed is null || _allowed.Contains(instance);
}
