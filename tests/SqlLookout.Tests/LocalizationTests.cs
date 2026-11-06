using System.Globalization;
using System.Text.RegularExpressions;
using SqlLookout.Core;

namespace SqlLookout.Tests;

public partial class LocalizationTests
{
    // Sentences that are looked up by a variable (a stored reason) rather than written as Loc.T("..."), so the scan below cannot see them.
    private static readonly string[] RuntimeKeys =
    [
        "sys.dm_os_volume_stats needs SQL Server 2008 R2 SP1 or later",
        "SQL Server Express has no SQL Server Agent",
        "SQL Server Express does not support availability groups",
        "Availability groups need SQL Server 2012 or later",
        "SQL Server Express does not support log shipping",
        "SQL Server Express cannot hold a mirrored database",
        "Only SQL Server Express has a database size cap",
    ];

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlLookout.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Solution folder not found."), "src");
    }

    /// <summary>Every English sentence passed to Loc.T("...") in the program source.</summary>
    private static HashSet<string> SentencesInSource()
    {
        var found = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            if (name is "Zh.cs" or "Loc.cs") continue;
            foreach (Match m in TCall().Matches(File.ReadAllText(file)))
                found.Add(m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n"));
        }
        return found;
    }

    [GeneratedRegex("Loc\\.T\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex TCall();

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void Every_sentence_in_the_source_has_a_translation()
    {
        var sentences = SentencesInSource();
        Assert.True(sentences.Count > 250, $"expected to find the program's sentences, found {sentences.Count}");

        var missing = sentences.Concat(RuntimeKeys).Where(s => !Zh.Map.ContainsKey(s)).OrderBy(s => s).ToList();
        Assert.True(missing.Count == 0, "No Traditional Chinese translation for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void The_dictionary_has_no_leftover_entries()
    {
        var used = SentencesInSource().Concat(RuntimeKeys).ToHashSet();
        var unused = Zh.Map.Keys.Where(k => !used.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(unused.Count == 0, "Translations that no code uses (renamed or removed sentence?):\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Placeholders_in_each_translation_match_the_english_sentence()
    {
        var wrong = new List<string>();
        foreach (var (english, chinese) in Zh.Map)
        {
            // Same placeholders with the same formats, in any order (a translation may reorder them).
            var a = Placeholder().Matches(english).Select(m => m.Value).OrderBy(x => x).ToList();
            var b = Placeholder().Matches(chinese).Select(m => m.Value).OrderBy(x => x).ToList();
            if (!a.SequenceEqual(b)) wrong.Add($"{english}\n   -> {chinese}");
        }
        Assert.True(wrong.Count == 0, "Placeholders differ:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void Every_translation_actually_formats_with_sample_values()
    {
        using var _ = Loc.Use(Loc.TraditionalChinese);
        foreach (var (english, _) in Zh.Map)
        {
            var count = Placeholder().Matches(english).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
            var args = Enumerable.Range(0, count).Select(i => (object?)(1234.5 + i)).ToArray();
            var text = Loc.T(english, args);                // must not throw FormatException
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public void English_is_the_default_and_the_key_is_returned_as_is()
    {
        Assert.Equal("Sign in", Loc.T("Sign in"));
        Assert.Equal("Disk C:\\ is almost full", Loc.T("Disk {0} is almost full", "C:\\"));
    }

    [Fact]
    public void Chinese_is_used_inside_a_scope_and_english_comes_back_after_it()
    {
        using (Loc.Use(Loc.TraditionalChinese))
        {
            Assert.Equal("登入", Loc.T("Sign in"));
            Assert.Equal("磁碟 C:\\ 即將用完", Loc.T("Disk {0} is almost full", "C:\\"));
            Assert.Equal("zh-Hant", Loc.HtmlLang);
        }
        Assert.Equal("Sign in", Loc.T("Sign in"));
        Assert.Equal("en", Loc.HtmlLang);
    }

    [Fact]
    public void A_sentence_without_a_translation_falls_back_to_english()
    {
        using var _ = Loc.Use(Loc.TraditionalChinese);
        Assert.Equal("A sentence nobody translated 7", Loc.T("A sentence nobody translated {0}", 7));
    }

    [Fact]
    public void Unknown_language_codes_mean_english_and_scopes_nest()
    {
        using (Loc.Use("fr"))
            Assert.Equal("Sign in", Loc.T("Sign in"));

        using (Loc.Use(Loc.TraditionalChinese))
        {
            using (Loc.Use(Loc.English)) Assert.Equal("Sign in", Loc.T("Sign in"));
            Assert.Equal("登入", Loc.T("Sign in"));
        }
    }

    [Fact]
    public async Task Language_follows_each_async_flow_and_does_not_leak_between_them()
    {
        // Two requests handled at the same time, one per language, must each see their own.
        async Task<string> Read(string language)
        {
            using var _ = Loc.Use(language);
            await Task.Delay(50);
            return Loc.T("Sign in");
        }

        var results = await Task.WhenAll(Read(Loc.TraditionalChinese), Read(Loc.English), Read(Loc.TraditionalChinese), Read(Loc.English));
        Assert.Equal(["登入", "Sign in", "登入", "Sign in"], results);
        Assert.Equal("Sign in", Loc.T("Sign in"));
    }

    [Fact]
    public void Numbers_use_a_dot_whatever_the_machine_culture_is()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");            // a comma-decimal culture
            Assert.Equal("Full backup of X is 115.5 h old", Loc.T("Full backup of {0} is {1:0.#} h old", "X", 115.5));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}
