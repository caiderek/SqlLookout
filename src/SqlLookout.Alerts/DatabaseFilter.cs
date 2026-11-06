using SqlLookout.Core;

namespace SqlLookout.Alerts;

/// <summary>
/// Databases an instance should not be judged on (test and scratch databases nobody looks after). One pattern per entry:
/// * stands for any text, ? for one character, case does not matter. A pattern with no wildcard is an exact name.
/// </summary>
public static class DatabaseFilter
{
    public const int MaxPatterns = 200;
    public const int MaxPatternLength = 128;

    public static bool IsIgnored(IReadOnlyList<string>? patterns, string database)
    {
        if (patterns is null || patterns.Count == 0) return false;
        foreach (var p in patterns)
            if (Matches(p, database)) return true;
        return false;
    }

    /// <summary>Splits what a person typed (one per line; commas and semicolons also work) into clean patterns.</summary>
    public static List<string> Parse(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Any(char.IsControl)) throw new InvalidDataException(Loc.T("The ignore list can only hold database names and the wildcards * and ?."));
            if (part.Length > MaxPatternLength) throw new InvalidDataException(Loc.T("The ignore list holds at most 200 entries of up to 128 characters each."));
            if (!list.Contains(part, StringComparer.OrdinalIgnoreCase)) list.Add(part);
        }
        if (list.Count > MaxPatterns) throw new InvalidDataException(Loc.T("The ignore list holds at most 200 entries of up to 128 characters each."));
        return list;
    }

    internal static bool Matches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
