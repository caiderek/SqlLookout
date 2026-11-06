using System.Globalization;

namespace SqlLookout.Core;

/// <summary>
/// Text for people (web pages, alert mails, error messages). The English sentence is the key: <c>Loc.T("Sign in")</c>
/// returns the English text, or the Traditional Chinese one when that language is active and a translation exists.
/// Placeholders are positional, <c>Loc.T("{0} is unreachable", name)</c>, so a translation can reorder them.
/// The active language belongs to the current request or alert pass (an AsyncLocal), never to a global, so
/// two requests in different languages, or two tests, cannot disturb each other.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string TraditionalChinese = "zh-TW";

    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        (English, "English"),
        (TraditionalChinese, "繁體中文"),
    ];

    private static readonly AsyncLocal<string?> Current = new();

    public static string Language => Current.Value ?? English;

    public static bool IsSupported(string? code) => Languages.Any(l => l.Code == code);

    /// <summary>Uses <paramref name="code"/> until the returned scope is disposed. An unknown code means English.</summary>
    public static IDisposable Use(string? code)
    {
        var previous = Current.Value;
        Current.Value = IsSupported(code) ? code : English;
        return new Scope(previous);
    }

    public static string T(string english, params object?[] args)
    {
        var text = Language == TraditionalChinese && Zh.Map.TryGetValue(english, out var zh) ? zh : english;
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    /// <summary>The HTML lang attribute value for the active language.</summary>
    public static string HtmlLang => Language == TraditionalChinese ? "zh-Hant" : "en";

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
