using System.Text;
using SqlLookout.Core;
using SqlLookout.Storage;

namespace SqlLookout.Alerts;

public interface IAlertSender
{
    Task SendAsync(string subject, string body, CancellationToken ct);

    /// <summary>
    /// False when nothing is actually sent (no email configured). Problems are then not recorded as "already told", so
    /// when email is set up later, everything that is still wrong is mailed instead of silently skipped.
    /// </summary>
    bool Delivers => true;
}

/// <summary>Used when no email is configured in the web interface: the dashboard is the only place problems show.</summary>
public sealed class NullAlertSender : IAlertSender
{
    public bool Delivers => false;
    public Task SendAsync(string subject, string body, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Used when no email is configured: alerts still show up in the console / service log.</summary>
public sealed class ConsoleAlertSender : IAlertSender
{
    public Task SendAsync(string subject, string body, CancellationToken ct)
    {
        Console.WriteLine($"[ALERT] {subject}{Environment.NewLine}{body}");
        return Task.CompletedTask;
    }
}

public sealed record QuietHours(TimeSpan Start, TimeSpan End)
{
    /// <summary>True when the local time of day falls in the quiet window (which may wrap midnight).</summary>
    public bool Contains(TimeSpan timeOfDay) =>
        Start <= End ? timeOfDay >= Start && timeOfDay < End : timeOfDay >= Start || timeOfDay < End;
}

/// <summary>
/// Collects non-urgent problems into one mail every <paramref name="Every"/>, the first one at <paramref name="Start"/> (local time of day).
/// The series restarts at Start each day, so the times do not drift.
/// </summary>
public sealed record DigestSchedule(TimeSpan Start, TimeSpan Every)
{
    /// <summary>The latest digest time that has already passed.</summary>
    public DateTime SlotAt(DateTime localNow)
    {
        var anchor = localNow.Date + Start;
        if (localNow < anchor) anchor = anchor.AddDays(-1);
        var k = (long)Math.Floor((localNow - anchor) / Every);
        return anchor + k * Every;
    }
}

/// <summary>
/// Turns findings into mail without repeating itself: a new alert, a worse severity, or a renotify interval
/// that has passed sends; a condition that cleared sends one "resolved" mail. During quiet hours only critical
/// findings go out; warnings wait (they are not marked as sent, so they go out after the window).
/// With a digest schedule, only urgent problems (critical, of an urgent kind such as "cannot connect") go out at once;
/// everything else waits for the next digest time the same way, and a problem that comes and goes in between is never mailed.
/// The same problem on several servers is written as one entry.
/// </summary>
public sealed class AlertDispatcher(IHistoryStore store, IAlertSender sender, TimeSpan renotifyAfter, QuietHours? quiet,
    TimeSpan resolveAfter = default, bool warningsInSummaryOnly = false,
    DigestSchedule? digest = null, IReadOnlySet<string>? urgentKinds = null, bool mergeHosts = true)
{
    private const string DigestSlotState = "digest.last_slot";

    private static string KindOf(string key) => key.Split('|') is { Length: > 1 } parts ? parts[1] : "";
    private bool IsUrgent(Finding f) => f.Severity == Severity.Critical && urgentKinds is not null && urgentKinds.Contains(KindOf(f.Key));

    public async Task DispatchAsync(IReadOnlyList<Finding> findings, IReadOnlyCollection<string> instancesChecked,
        DateTime nowUtc, TimeSpan localTimeOfDay, CancellationToken ct = default)
    {
        var active = (await store.GetActiveAlertsAsync(ct)).ToDictionary(a => a.Key);
        var isQuiet = quiet?.Contains(localTimeOfDay) ?? false;

        var slot = digest?.SlotAt(nowUtc.ToLocalTime()).ToString("o");
        var digestDue = digest is not null && await store.GetStateAsync(DigestSlotState, ct) != slot;
        bool Held(Finding f) => digest is not null && !digestDue && !IsUrgent(f);

        var toSend = new List<(Finding Finding, string Why)>();
        foreach (var f in findings)
        {
            if ((isQuiet || warningsInSummaryOnly) && f.Severity == Severity.Warning) continue;
            if (Held(f)) continue;

            // Acknowledged or snoozed by a person: say nothing. A mute that has run out, or that the problem has outgrown
            // (warning turned critical), is dropped and the finding goes through the normal rules below.
            if (await AlertMutes.GetAsync(store, f.Key, ct) is { } mute)
            {
                if (mute.Silences(f, nowUtc)) continue;
                await AlertMutes.ClearAsync(store, f.Key, ct);
            }

            if (!active.TryGetValue(f.Key, out var existing))
                toSend.Add((f, Loc.T("new")));
            else if (f.Severity.ToString() != existing.Severity && f.Severity == Severity.Critical)
                toSend.Add((f, Loc.T("escalated")));
            else if (nowUtc - existing.LastNotifiedUtc >= renotifyAfter)
                toSend.Add((f, Loc.T("still active")));
        }

        // Only resolve alerts of instances that were actually evaluated this round.
        var currentKeys = findings.Select(f => f.Key).ToHashSet();
        var gone = active.Values
            .Where(a => instancesChecked.Contains(a.Instance) && !currentKeys.Contains(a.Key))
            .ToList();

        // A value hovering around its limit clears and fires again every few minutes. A problem therefore counts as resolved
        // only after it has been absent for the whole grace time; if it comes back before that, it is the same problem
        // and nothing is mailed. The time of the first absence is kept in the store so a restart does not reset it.
        foreach (var a in active.Values.Where(a => currentKeys.Contains(a.Key)))
            if (resolveAfter > TimeSpan.Zero && await store.GetStateAsync(AbsentKey(a.Key), ct) is { Length: > 0 })
                await store.SetStateAsync(AbsentKey(a.Key), "", ct);
        var resolved = new List<AlertRecord>();
        foreach (var a in gone)
        {
            if (resolveAfter <= TimeSpan.Zero) { resolved.Add(a); continue; }
            var since = await store.GetStateAsync(AbsentKey(a.Key), ct);
            if (!DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var absentSince))
            {
                await store.SetStateAsync(AbsentKey(a.Key), nowUtc.ToString("o"), ct);
                continue;
            }
            if (nowUtc - absentSince >= resolveAfter) resolved.Add(a);
        }

        // The resolved mail of a problem that is not urgent waits for the digest too (grace time above has already run).
        if (digest is not null && !digestDue)
            resolved.RemoveAll(a => !(a.Severity == nameof(Severity.Critical) && urgentKinds is not null && urgentKinds.Contains(KindOf(a.Key))));

        // A mute belongs to its problem: once the problem is gone for good, the mute goes with it, so the same problem
        // coming back later starts fresh. Within the grace time above, a problem that is only absent for a moment keeps it.
        var resolvedKeys = resolved.Select(a => a.Key).ToHashSet();
        foreach (var gonemute in await AlertMutes.ListAsync(store, ct))
            if (!currentKeys.Contains(gonemute.Key) && instancesChecked.Contains(gonemute.Instance)
                && (!active.ContainsKey(gonemute.Key) || resolvedKeys.Contains(gonemute.Key)))
                await AlertMutes.ClearAsync(store, gonemute.Key, ct);

        if (toSend.Count > 0)
        {
            var body = new StringBuilder();
            var entries = mergeHosts
                ? toSend.GroupBy(x => (x.Finding.Severity, x.Finding.Title, x.Finding.Detail, x.Why))
                    .Select(g => (g.First().Finding, g.First().Why, Hosts: g.Select(x => x.Finding.Instance).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList()))
                : toSend.Select(x => (x.Finding, x.Why, Hosts: new List<string> { x.Finding.Instance }));
            foreach (var (f, why, hosts) in entries.OrderByDescending(x => x.Finding.Severity).ThenBy(x => KindOf(x.Finding.Key), StringComparer.Ordinal))
            {
                body.AppendLine($"[{(f.Severity == Severity.Critical ? Loc.T("CRITICAL") : Loc.T("WARNING"))}] ({why}) {string.Join(", ", hosts)}: {f.Title}{Environment.NewLine}    {f.Detail.Replace("\n", Environment.NewLine + "    ")}");            }
            var worst = toSend.Max(x => x.Finding.Severity);
            await sender.SendAsync(Loc.T("[SqlLookout] {0}: {1} alert(s)", worst == Severity.Critical ? Loc.T("Critical") : Loc.T("Warning"), toSend.Count), body.ToString(), ct);

            foreach (var (f, _) in toSend)
            {
                var first = active.TryGetValue(f.Key, out var e) ? e.FirstUtc : nowUtc;
                await store.UpsertAlertAsync(new AlertRecord(f.Key, f.Instance, f.Severity.ToString(), f.Title, f.Detail, first, nowUtc, null), ct);
            }
        }

        if (resolved.Count > 0)
        {
            var body = new StringBuilder();
            foreach (var g in resolved.GroupBy(a => mergeHosts ? a.Title : a.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var hosts = g.Select(a => a.Instance).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
                body.AppendLine($"[{Loc.T("RESOLVED")}] {string.Join(", ", hosts)}: {g.First().Title}");
            }
            await sender.SendAsync(Loc.T("[SqlLookout] Resolved: {0} alert(s)", resolved.Count), body.ToString(), ct);
            foreach (var a in resolved)
            {
                await store.ResolveAlertAsync(a.Key, nowUtc, ct);
                await store.SetStateAsync(AbsentKey(a.Key), "", ct);
            }
        }

        if (digest is not null && digestDue) await store.SetStateAsync(DigestSlotState, slot!, ct);
    }

    private static string AbsentKey(string alertKey) => "alert.absent." + alertKey;
}
