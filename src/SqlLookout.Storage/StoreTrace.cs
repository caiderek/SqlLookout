using System.Diagnostics;
using System.Text;

namespace SqlLookout.Storage;

/// <summary>Per-request tally of store calls: how long each waited for the single database gate and how long it held it.</summary>
public sealed class StoreTally
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Calls, long Waited, long Held)> _byMethod = new();

    internal void Add(string method, long waitedTicks, long heldTicks)
    {
        lock (_lock)
        {
            var (calls, waited, held) = _byMethod.GetValueOrDefault(method);
            _byMethod[method] = (calls + 1, waited + waitedTicks, held + heldTicks);
        }
    }

    public string Summary()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (var (method, v) in _byMethod.OrderByDescending(x => x.Value.Held + x.Value.Waited))
                sb.Append($"{method.Replace("Async", "")} x{v.Calls} wait {Ms(v.Waited)}ms run {Ms(v.Held)}ms; ");
            return sb.ToString();
        }
    }

    private static long Ms(long ticks) => ticks * 1000 / Stopwatch.Frequency;
}

public static class StoreTrace
{
    private static readonly AsyncLocal<StoreTally?> Slot = new();

    /// <summary>Set by the web layer for the duration of one request; null elsewhere, where nothing is recorded.</summary>
    public static StoreTally? Current
    {
        get => Slot.Value;
        set => Slot.Value = value;
    }
}
