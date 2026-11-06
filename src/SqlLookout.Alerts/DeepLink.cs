using System.Text;
using System.Text.RegularExpressions;

namespace SqlLookout.Alerts;

/// <summary>Builds a deep link that pre-fills an investigation prompt from a fixed template; only the server name and the problem kind go in.</summary>
public static partial class DeepLink
{
    public const int MaxServerNameLength = 100;

    public static string? For(Finding f) => PromptFor(f) is { } prompt ? "claude-cli://open?q=" + Uri.EscapeDataString(prompt) : null;

    /// <summary>The same question as plain text, to paste into a Claude Code that is already open.</summary>
    public static string? PromptFor(Finding f)
    {
        var server = CleanServerName(f.Instance);
        var parts = f.Key.Split('|');
        var kind = parts.Length > 1 && KindPattern().IsMatch(parts[1]) ? parts[1] : null;
        if (server is null || kind is null) return null;

        return $"Use the sqllookout MCP tools to investigate a SqlLookout problem on server '{server}' (kind: {kind}). " +
               "Read only: call get_problems for that server, then get_metric_history for the related metrics, " +
               "and explain the likely cause and what to check first. Do not change anything.";
    }

    /// <summary>Keeps letters, digits and the punctuation a server name uses; quotes, line breaks and control characters are dropped.</summary>
    internal static string? CleanServerName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '\\' or ':' or ',' or ' ') sb.Append(c);
            if (sb.Length >= MaxServerNameLength) break;
        }
        var clean = sb.ToString().Trim();
        return clean.Length == 0 ? null : clean;
    }

    [GeneratedRegex("^[a-z0-9_]{1,30}$")]
    private static partial Regex KindPattern();
}
