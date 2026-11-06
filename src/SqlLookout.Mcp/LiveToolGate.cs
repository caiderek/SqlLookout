using ModelContextProtocol;

namespace SqlLookout.Mcp;

/// <summary>
/// Keeps the live tools from loading a monitored server. Per instance (not per key, so many keys cannot add up): one read
/// at a time, a caller that cannot get in within a couple of seconds is told the server is busy, and an answer is reused
/// for a few seconds, so a client asking in a loop costs the server one read per window.
/// </summary>
public sealed class LiveToolGate
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);
    public const string Busy = "Another live read of this server is still running. Try again in a few seconds.";

    private readonly object _lock = new();
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime AtUtc, string Value)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;

    public LiveToolGate() : this(() => DateTime.UtcNow) { }
    public LiveToolGate(Func<DateTime> now) => _now = now;

    public async Task<string> RunAsync(string instance, string tool, Func<Task<string>> read, CancellationToken ct)
    {
        var key = instance + "\u0001" + tool;
        if (TryCached(key, out var cached)) return cached;

        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(instance, out gate!)) _gates[instance] = gate = new SemaphoreSlim(1, 1);
        }
        if (!await gate.WaitAsync(Wait, ct)) throw new McpException(Busy);
        try
        {
            // Whoever held the gate may just have read the same thing.
            if (TryCached(key, out cached)) return cached;
            var value = await read();
            lock (_lock) _cache[key] = (_now(), value);
            return value;
        }
        finally { gate.Release(); }
    }

    private bool TryCached(string key, out string value)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit) && _now() - hit.AtUtc < CacheFor) { value = hit.Value; return true; }
        }
        value = "";
        return false;
    }
}
