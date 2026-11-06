using System.Globalization;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

public enum MuteKind
{
    /// <summary>"I know about it": quiet until the problem is gone.</summary>
    Acknowledged,
    /// <summary>Quiet until a given time.</summary>
    Snoozed,
}

/// <summary>
/// A person said "stop telling me about this one". Kept per alert key in the store's state table, so the
/// monitor and the web page share it without a schema change. The severity is remembered: a warning that
/// turns critical is a new situation and is mailed even though the warning was acknowledged.
/// </summary>
public sealed record AlertMute(string Key, string Instance, MuteKind Kind, Severity Severity, DateTime? UntilUtc, DateTime CreatedUtc)
{
    /// <summary>True when this mute still silences the finding at <paramref name="nowUtc"/>.</summary>
    public bool Silences(Finding f, DateTime nowUtc) =>
        f.Severity <= Severity && (Kind == MuteKind.Acknowledged || (UntilUtc is { } until && nowUtc < until));

    internal string Serialize() => string.Join('\t', Kind, Severity, UntilUtc?.ToString("o") ?? "", Instance, CreatedUtc.ToString("o"));

    internal static AlertMute? Parse(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var p = value.Split('\t');
        if (p.Length != 5 || !Enum.TryParse<MuteKind>(p[0], out var kind) || !Enum.TryParse<Severity>(p[1], out var sev)) return null;
        DateTime? until = null;
        if (p[2].Length > 0)
        {
            if (!DateTime.TryParse(p[2], null, DateTimeStyles.RoundtripKind, out var u)) return null;
            until = u;
        }
        if (kind == MuteKind.Snoozed && until is null) return null;
        if (!DateTime.TryParse(p[4], null, DateTimeStyles.RoundtripKind, out var created)) return null;
        return new AlertMute(key, p[3], kind, sev, until, created);
    }
}

public static class AlertMutes
{
    private const string Prefix = "alert.mute.";
    private const string IndexKey = "alert.mutes.keys";

    public static async Task<AlertMute?> GetAsync(IHistoryStore store, string key, CancellationToken ct = default) =>
        AlertMute.Parse(key, await store.GetStateAsync(Prefix + key, ct));

    /// <summary>The mute that applies to this finding right now, or null (none, expired, or the finding got worse).</summary>
    public static async Task<AlertMute?> ActiveForAsync(IHistoryStore store, Finding f, DateTime nowUtc, CancellationToken ct = default)
    {
        var mute = await GetAsync(store, f.Key, ct);
        return mute is not null && mute.Silences(f, nowUtc) ? mute : null;
    }

    public static async Task SetAsync(IHistoryStore store, Finding f, MuteKind kind, DateTime? untilUtc, DateTime nowUtc, CancellationToken ct = default)
    {
        if (kind == MuteKind.Snoozed && untilUtc is null) throw new ArgumentException("A snooze needs an end time.", nameof(untilUtc));
        var mute = new AlertMute(f.Key, f.Instance, kind, f.Severity, kind == MuteKind.Snoozed ? untilUtc : null, nowUtc);
        await store.SetStateAsync(Prefix + f.Key, mute.Serialize(), ct);
        var keys = await KeysAsync(store, ct);
        if (keys.Add(f.Key)) await store.SetStateAsync(IndexKey, string.Join('\n', keys), ct);
    }

    public static async Task ClearAsync(IHistoryStore store, string key, CancellationToken ct = default)
    {
        await store.SetStateAsync(Prefix + key, "", ct);
        var keys = await KeysAsync(store, ct);
        if (keys.Remove(key)) await store.SetStateAsync(IndexKey, string.Join('\n', keys), ct);
    }

    /// <summary>Forgets every mute of one server (it was deleted: a server added later under the same name starts clean).</summary>
    public static async Task ClearForInstanceAsync(IHistoryStore store, string instance, CancellationToken ct = default)
    {
        foreach (var m in await ListAsync(store, ct))
            if (string.Equals(m.Instance, instance, StringComparison.OrdinalIgnoreCase)) await ClearAsync(store, m.Key, ct);
    }

    /// <summary>Every mute still on record, expired snoozes included (callers decide with <see cref="AlertMute.Silences"/>).</summary>
    public static async Task<List<AlertMute>> ListAsync(IHistoryStore store, CancellationToken ct = default)
    {
        var list = new List<AlertMute>();
        foreach (var key in await KeysAsync(store, ct))
            if (await GetAsync(store, key, ct) is { } m) list.Add(m);
        return list;
    }

    private static async Task<HashSet<string>> KeysAsync(IHistoryStore store, CancellationToken ct) =>
        (await store.GetStateAsync(IndexKey, ct) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
}
