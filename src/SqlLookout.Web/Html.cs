using System.Globalization;
using System.Net;
using System.Text;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Web;

/// <summary>Tiny HTML helpers. Every dynamic value goes through <see cref="E"/>; nothing is concatenated raw.</summary>
internal static class H
{
    public static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    public static string Token(string token) => $"""<input type="hidden" name="__RequestVerificationToken" value="{E(token)}">""";

    // The theme the browser chose with the header button: "light", "dark", or null for Auto (follow the browser/OS).
    private static readonly AsyncLocal<string?> CurrentTheme = new();
    public static readonly string[] Themes = ["light", "dark"];

    public static string? Theme
    {
        get => CurrentTheme.Value;
        set => CurrentTheme.Value = value is not null && Themes.Contains(value) ? value : null;
    }

    private static string NavLink(string path, string href, string label)
    {
        var current = path.Split('?')[0];
        // A server's own page belongs to the Dashboard.
        var on = href == "/" ? current == "/" || current.StartsWith("/server/", StringComparison.Ordinal) : current.StartsWith(href, StringComparison.Ordinal);
        return $"""<a href="{href}"{(on ? " class=\"on\" aria-current=\"page\"" : "")}>{E(label)}</a>""";
    }

    /// <summary>One small button that cycles Auto, Light, Dark; it shows the current choice.</summary>
    public static string ThemeForm(string token, string returnPath)
    {
        var (next, label) = Theme switch
        {
            "light" => ("dark", Loc.T("Theme: {0}", Loc.T("Light"))),
            "dark" => ("auto", Loc.T("Theme: {0}", Loc.T("Dark"))),
            _ => ("light", Loc.T("Theme: {0}", Loc.T("Auto"))),
        };
        return $"""<form method="post" action="/theme" class="inline">{Token(token)}<input type="hidden" name="theme" value="{next}"><input type="hidden" name="returnUrl" value="{E(returnPath)}"><button class="link">{E(label)}</button></form>""";
    }

    public static string Page(string title, string body, bool signedIn, string? user = null, int refreshSeconds = 0, string? token = null, string returnPath = "/")
    {
        var nav = signedIn
            ? $"""
               <nav>
                 {NavLink(returnPath, "/", Loc.T("Dashboard"))}
                 {NavLink(returnPath, "/problems", Loc.T("Problems"))}
                 {NavLink(returnPath, "/history", Loc.T("History"))}
                 {NavLink(returnPath, "/instances", Loc.T("Servers"))}
                 {NavLink(returnPath, "/settings", Loc.T("Settings"))}
                 {NavLink(returnPath, "/account", Loc.T("Account"))}
               </nav>
               """
            : "";
        var refresh = refreshSeconds > 0 ? $"""<meta http-equiv="refresh" content="{refreshSeconds}">""" : "";
        var themeAttr = Theme is { } t ? $" data-theme=\"{t}\"" : "";
        var scheme = Theme ?? "light dark";
        return $"""
            <!doctype html>
            <html lang="{Loc.HtmlLang}"{themeAttr}>
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="{scheme}">
            {refresh}
            <title>{E(title)} - SqlLookout</title>
            <link rel="stylesheet" href="/app.css">
            </head>
            <body>
            <header><div class="bar"><a class="brand" href="/">SqlLookout</a>{nav}<span class="grow"></span>{(signedIn ? $"<span class=\"who\">{E(user)}</span> {ThemeForm(token ?? "", returnPath)} {LanguageForm(token ?? "", returnPath)} {SignOutForm(token ?? "")}" : "")}</div></header>
            <main>
            {body}
            </main>
            </body>
            </html>
            """;
    }

    public static string SignOutForm(string token) =>
        $"""<form method="post" action="/logout" class="inline">{Token(token)}<button class="link">{E(Loc.T("Sign out"))}</button></form>""";

    /// <summary>One small button that switches to the other language (shown in that language's own name).</summary>
    public static string LanguageForm(string token, string returnPath)
    {
        var other = Loc.Languages.First(l => l.Code != Loc.Language);
        return $"""<form method="post" action="/language" class="inline">{Token(token)}<input type="hidden" name="language" value="{E(other.Code)}"><input type="hidden" name="returnUrl" value="{E(returnPath)}"><button class="link">{E(other.Name)}</button></form>""";
    }

    public static string Field(string label, string name, string? value = null, string type = "text", string? help = null, bool required = false, string extra = "")
    {
        var id = "f_" + name;
        return $"""
            <div class="field">
              <label for="{id}">{E(label)}</label>
              <input id="{id}" name="{E(name)}" type="{type}" value="{E(value)}" {(required ? "required" : "")} {extra}>
              {(help is null ? "" : $"<small>{E(help)}</small>")}
            </div>
            """;
    }

    public static string TextArea(string label, string name, string? value = null, string? help = null, int rows = 4) =>
        $"""
        <div class="field">
          <label for="f_{name}">{E(label)}</label>
          <textarea id="f_{name}" name="{E(name)}" rows="{rows}" spellcheck="false">{E(value)}</textarea>
          {(help is null ? "" : $"<small>{E(help)}</small>")}
        </div>
        """;

    public static string Select(string label, string name, IEnumerable<(string Value, string Text)> options, string selected, string? help = null)
    {
        var sb = new StringBuilder();
        foreach (var (v, t) in options)
            sb.Append($"""<option value="{E(v)}"{(v == selected ? " selected" : "")}>{E(t)}</option>""");
        return $"""
            <div class="field">
              <label for="f_{name}">{E(label)}</label>
              <select id="f_{name}" name="{E(name)}">{sb}</select>
              {(help is null ? "" : $"<small>{E(help)}</small>")}
            </div>
            """;
    }

    public static string Check(string label, string name, bool isChecked, string? help = null) =>
        $"""
         <div class="field check">
           <label><input type="checkbox" name="{E(name)}" value="1"{(isChecked ? " checked" : "")}> {E(label)}</label>
           {(help is null ? "" : $"<small>{E(help)}</small>")}
         </div>
         """;

    public static string Notice(string? text, string kind = "bad") =>
        string.IsNullOrEmpty(text) ? "" : $"""<p class="notice {kind}" role="alert">{E(text)}</p>""";
}

internal static class Fmt
{
    public static string Age(double? minutes)
    {
        if (minutes is null) return "-";
        var m = minutes.Value;
        if (m < 120) return Loc.T("{0:0} min", m);
        if (m < 48 * 60) return Loc.T("{0:0.#} h", m / 60);
        return Loc.T("{0:0.#} d", m / 1440);
    }

    public static string Gb(double? bytes) =>
        bytes is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{bytes.Value / (1024.0 * 1024 * 1024):0.#} GB");

    public static string Mb(double? mb) =>
        mb is null ? "-" : mb.Value >= 1024 ? string.Create(CultureInfo.InvariantCulture, $"{mb.Value / 1024:0.#} GB") : string.Create(CultureInfo.InvariantCulture, $"{mb.Value:0} MB");

    public static string Utc(DateTime? t) => t is null ? Loc.T("never") : t.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Ago(DateTime? t, DateTime now)
    {
        if (t is null) return Loc.T("never");
        var span = now - t.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? Loc.T("just now") : Loc.T("{0} ago", Age(span.TotalMinutes));
    }
}

internal static class Css
{
    // Colours are variables so the light and dark sets can be swapped by the browser's setting (Auto) or by the
    // switch in the page header (data-theme on <html>). The dark set is written once and used twice.
    //
    // Two looks, chosen in Settings (Looks.*): Classic is the original and the default, so an update never changes an
    // existing install. Terminal is a dense monospace style: square corners, hairlines instead of shadows, and status
    // colours (red / amber / green) that always come with a word or a marker, never colour alone.
    // The markup is shared; the few elements only Terminal needs (the severity word, the spark's end dot) are hidden
    // by Classic.
    private const string ClassicLightVars = "--bg:#f4f6fa; --card:#fff; --text:#1b2333; --muted:#5b6578; --line:#e3e7ee; --accent:#2f5bea; --accentbg:#e9eefe; --hover:#f7f9fc; --ok:#18794e; --warn:#946200; --bad:#c9302c; --badbg:#fdecea; --warnbg:#fff3d1; --okbg:#e4f5ec; --shadow:0 1px 2px rgba(16,24,40,.05), 0 1px 3px rgba(16,24,40,.07);";
    private const string ClassicDarkVars = "--bg:#11141a; --card:#1a1f29; --text:#e6e9f0; --muted:#9aa4b8; --line:#2c3342; --accent:#7ba2ff; --accentbg:#232c45; --hover:#202636; --ok:#5fcf8c; --warn:#f0c14b; --bad:#ff7b72; --badbg:#3a1d1d; --warnbg:#3a3015; --okbg:#173325; --shadow:0 1px 2px rgba(0,0,0,.35);";
    private const string TerminalLightVars = "--bg:#f1ebe2; --card:#faf6ef; --text:#2b1f16; --muted:#6b5a4b; --line:#d3c6b4; --accent:#6f4320; --accentbg:#eadbc6; --onaccent:#ffffff; --hover:#ebe2d4; --ok:#2f6b3a; --warn:#8a5200; --bad:#b3261e; --badbg:#f6ddd8; --warnbg:#f3e3c4; --okbg:#e1ecd8; --off:#9a8b7a; --blue:#35609a; --shadow:none;";
    private const string TerminalDarkVars = "--bg:#0a0e1a; --card:#11172a; --text:#e8ecf4; --muted:#8e98b3; --line:#2a3050; --accent:#ffa02f; --accentbg:#2a2113; --onaccent:#0a0e1a; --hover:#1a2138; --ok:#00b96b; --warn:#ffa02f; --bad:#f23645; --badbg:#35141a; --warnbg:#33260f; --okbg:#0f2a1f; --off:#5e6680; --blue:#5ba8ff; --shadow:none;";

    private static readonly string ClassicText = Variables(ClassicLightVars, ClassicDarkVars) + ClassicRules + ClassicExtra;
    private static readonly string TerminalText = Variables(TerminalLightVars, TerminalDarkVars) + TerminalRules;

    /// <summary>The stylesheet for a look from <see cref="Looks"/>; anything unknown gives the classic one.</summary>
    public static string For(string? look) => look == Looks.Terminal ? TerminalText : ClassicText;

    private static string Variables(string light, string dark) =>
        $":root {{ {light} }}\n" +
        $"@media (prefers-color-scheme: dark) {{ :root:not([data-theme=\"light\"]) {{ {dark} }} }}\n" +
        $":root[data-theme=\"dark\"] {{ {dark} }}\n";

    private const string ClassicExtra = """
        .sev { display:none; } .spark .end { display:none; }
        textarea { width:100%; max-width:460px; padding:8px 10px; border:1px solid var(--line); border-radius:8px; background:var(--card); color:var(--text); font:inherit; }
        .detail { white-space:pre-line; }
        """;

    private const string ClassicRules = """
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--text); font:15px/1.55 system-ui, -apple-system, "Segoe UI", "Microsoft JhengHei", sans-serif; }
        header { background:var(--card); border-bottom:1px solid var(--line); box-shadow:var(--shadow); }
        .bar { max-width:1140px; margin:0 auto; padding:10px 20px; display:flex; align-items:center; gap:10px; flex-wrap:wrap; }
        .brand { font-weight:700; color:var(--text); text-decoration:none; font-size:17px; margin-right:10px; }
        .brand::before { content:""; display:inline-block; width:11px; height:11px; border-radius:3px; background:var(--accent); margin-right:8px; }
        nav { display:flex; gap:4px; }
        nav a { padding:6px 12px; border-radius:8px; color:var(--muted); font-weight:500; }
        nav a:hover { background:var(--hover); text-decoration:none; color:var(--text); }
        nav a.on { background:var(--accentbg); color:var(--accent); }
        a { color:var(--accent); text-decoration:none; } a:hover { text-decoration:underline; }
        .grow { flex:1; } .who { color:var(--muted); font-size:13px; }
        header form.inline { margin-left:6px; } header button.link { color:var(--muted); font-size:13px; } header button.link:hover { color:var(--accent); }
        main { max-width:1140px; margin:0 auto; padding:24px 20px 60px; }
        h1 { font-size:24px; font-weight:650; margin:4px 0 16px; letter-spacing:-.01em; } h2 { font-size:17px; margin:26px 0 10px; } h3 { font-size:12px; margin:18px 0 8px; color:var(--muted); text-transform:uppercase; letter-spacing:.06em; }
        .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px 20px; margin:0 0 18px; box-shadow:var(--shadow); }
        table { width:100%; border-collapse:collapse; font-size:14px; }
        th, td { text-align:left; padding:10px 10px; border-bottom:1px solid var(--line); vertical-align:top; }
        tbody tr:last-child td { border-bottom:none; } tbody tr:hover td { background:var(--hover); }
        th { color:var(--muted); font-weight:600; font-size:11.5px; text-transform:uppercase; letter-spacing:.06em; }
        td.num, th.num { text-align:right; font-variant-numeric:tabular-nums; }
        .tablewrap { overflow-x:auto; }
        .badge { display:inline-block; padding:1px 10px; border-radius:99px; font-size:12px; font-weight:600; }
        .badge.ok { background:var(--okbg); color:var(--ok); } .badge.warn { background:var(--warnbg); color:var(--warn); } .badge.bad { background:var(--badbg); color:var(--bad); } .badge.off { background:var(--line); color:var(--muted); }
        .problem { border:1px solid var(--line); border-left:4px solid var(--line); padding:9px 14px; margin:8px 0; background:var(--card); border-radius:8px; box-shadow:var(--shadow); }
        .problem.bad { border-left-color:var(--bad); } .problem.warn { border-left-color:var(--warn); }
        .muted { color:var(--muted); } .small { font-size:13px; }
        td.small { overflow-wrap:anywhere; min-width:220px; }
        .tiles { display:grid; grid-template-columns:repeat(auto-fit, minmax(160px, 1fr)); gap:14px; margin:0 0 18px; }
        .tile { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:14px 18px; box-shadow:var(--shadow); }
        .tile .n { font-size:32px; font-weight:700; line-height:1.1; font-variant-numeric:tabular-nums; } .tile .l { color:var(--muted); font-size:13px; margin-top:2px; }
        .tile.bad { border-left:4px solid var(--bad); } .tile.bad .n { color:var(--bad); }
        .tile.warn { border-left:4px solid var(--warn); } .tile.warn .n { color:var(--warn); }
        .tile.ok .n { color:var(--ok); }
        .chips { display:flex; flex-wrap:wrap; gap:8px; margin:0 0 14px; }
        .chip { display:inline-block; padding:3px 12px; border:1px solid var(--line); border-radius:99px; font-size:13px; font-weight:600; color:var(--text); background:var(--card); text-decoration:none; }
        .chip.bad { border-color:var(--bad); color:var(--bad); } .chip.warn { border-color:var(--warn); color:var(--warn); }
        .chip.on { background:var(--text); color:var(--card); border-color:var(--text); }
        .field { margin:0 0 14px; } .field label { display:block; font-weight:600; margin-bottom:3px; }
        .field.check label { font-weight:400; }
        .field small { display:block; color:var(--muted); margin-top:3px; }
        input[type=text], input[type=password], input[type=number], input[type=email], select { width:100%; max-width:460px; padding:8px 10px; border:1px solid var(--line); border-radius:8px; background:var(--card); color:var(--text); font:inherit; }
        input:focus, select:focus, button:focus-visible, .btn:focus-visible { outline:2px solid var(--accent); outline-offset:1px; }
        button, .btn { padding:8px 16px; border:1px solid var(--accent); background:var(--accent); color:#fff; border-radius:8px; font:inherit; font-weight:500; cursor:pointer; text-decoration:none; display:inline-block; }
        .btn:hover { text-decoration:none; filter:brightness(1.07); }
        button.secondary, .btn.secondary { background:transparent; color:var(--accent); }
        button.danger { background:var(--bad); border-color:var(--bad); }
        button.link { background:none; border:none; color:var(--accent); padding:0; font-weight:400; }
        form.inline { display:inline; margin:0; }
        .actions { display:flex; gap:8px; flex-wrap:wrap; align-items:center; margin-top:12px; }
        .notice { padding:10px 14px; border-radius:8px; margin:0 0 14px; }
        .notice.bad { background:var(--badbg); color:var(--bad); } .notice.ok { background:var(--okbg); color:var(--ok); } .notice.warn { background:var(--warnbg); color:var(--warn); }
        .narrow { max-width:520px; margin:40px auto; }
        .grid2 { display:grid; grid-template-columns:repeat(auto-fit, minmax(300px, 1fr)); gap:0 28px; }
        details summary { cursor:pointer; font-weight:600; }
        .card h2.first { margin-top:0; }
        .tabs { display:flex; gap:4px; flex-wrap:wrap; border-bottom:1px solid var(--line); margin:0 0 18px; }
        .tabs a { padding:8px 16px; border:1px solid transparent; border-bottom:none; border-radius:8px 8px 0 0; color:var(--muted); font-weight:500; }
        .tabs a:hover { text-decoration:none; color:var(--text); }
        .tabs a.on { background:var(--card); border-color:var(--line); color:var(--accent); font-weight:600; margin-bottom:-1px; }
        .tabs .badge { margin-left:6px; }
        .ranges { display:flex; gap:10px; margin:0 0 12px; align-items:center; } .ranges a.on { color:var(--text); font-weight:600; text-decoration:none; }
        .overview td { vertical-align:middle; } .overview td:first-child { font-size:15px; }
        .overview tr.rowbad td:first-child { box-shadow:inset 4px 0 0 var(--bad); } .overview tr.rowwarn td:first-child { box-shadow:inset 4px 0 0 var(--warn); }
        .chartgrid { display:grid; grid-template-columns:repeat(auto-fit, minmax(420px, 1fr)); gap:16px; margin:0 0 18px; }
        .chart { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:12px 16px; box-shadow:var(--shadow); }
        .chart h4 { margin:0 0 4px; font-size:14px; }
        .chartsvg { width:100%; height:auto; display:block; } .spark { display:inline-block; vertical-align:middle; }
        .chartsvg .grid { stroke:var(--line); stroke-width:1; } .chartsvg .tick { fill:var(--muted); font-size:11px; }
        .chartsvg .threshold { stroke:var(--bad); stroke-width:1; stroke-dasharray:4 3; }
        .line { stroke-width:1.8; stroke-linejoin:round; } .line.s0, .spark .line { stroke:var(--accent); } .line.s1 { stroke:var(--warn); } .line.s2 { stroke:var(--ok); } .line.s3 { stroke:var(--bad); }
        .sw { display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:4px; background:var(--accent); } .sw.s1 { background:var(--warn); } .sw.s2 { background:var(--ok); } .sw.s3 { background:var(--bad); }
        .legend { margin:4px 0 0; } .key { margin-right:14px; }
        .more { margin:8px 0 0; }
        @media (max-width:520px) { .chartgrid { grid-template-columns:1fr; } main { padding:16px 12px 50px; } .bar { padding:10px 12px; } }
        """;

    private const string TerminalRules = """
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--text); font:16px/1.8 system-ui, -apple-system, "Segoe UI", "Microsoft JhengHei", sans-serif; letter-spacing:.03em; text-wrap:pretty; }
        header { background:var(--card); border-bottom:1px solid var(--line); }
        .bar { max-width:1200px; margin:0 auto; padding:8px 16px; display:flex; align-items:center; gap:16px; flex-wrap:wrap; }
        .brand { font-weight:600; color:var(--accent); text-decoration:none; font-size:15px; letter-spacing:.12em; text-transform:uppercase; margin-right:8px; }
        .brand:hover { text-decoration:none; }
        nav { display:flex; gap:16px; }
        nav a { padding:2px 0; color:var(--muted); }
        nav a:hover { text-decoration:none; color:var(--text); }
        nav a.on { color:var(--text); box-shadow:inset 0 -2px 0 var(--accent); }
        a { color:var(--accent); text-decoration:none; } a:hover { text-decoration:underline; }
        a:focus-visible { outline:2px solid var(--accent); outline-offset:2px; }
        .grow { flex:1; } .who { color:var(--muted); }
        header form.inline { margin-left:0; } header button.link { color:var(--muted); } header button.link:hover { color:var(--accent); }
        main { max-width:1200px; margin:0 auto; padding:20px 16px 64px; }
        h1 { font-size:17px; font-weight:600; margin:4px 0 12px; letter-spacing:.08em; text-transform:uppercase; }
        h2 { font-size:14px; font-weight:600; margin:28px 0 8px; letter-spacing:.1em; text-transform:uppercase; color:var(--muted); }
        h3 { font-size:13px; margin:16px 0 6px; color:var(--muted); letter-spacing:.06em; }
        .card { background:var(--card); border:1px solid var(--line); padding:16px 20px; margin:0 0 14px; }
        .card h2 { font-size:15px; color:var(--text); text-transform:none; letter-spacing:.02em; margin:0 0 8px; }
        table { width:100%; border-collapse:collapse; }
        th, td { text-align:left; padding:10px 14px; border-bottom:1px solid var(--line); vertical-align:top; }
        tbody tr:last-child td { border-bottom:none; } tbody tr:hover td { background:var(--hover); }
        th { color:var(--muted); font-weight:500; font-size:13px; letter-spacing:.04em; white-space:nowrap; vertical-align:bottom; }
        td.num, th.num { text-align:right; font-variant-numeric:tabular-nums; }
        .tablewrap { overflow-x:auto; } .card.tablewrap { padding:0; }
        .overview th, .overview td { padding:10px 12px; white-space:nowrap; }
        .badge { display:inline-block; padding:0 6px; font-size:13px; font-weight:600; }
        .badge.ok { background:var(--okbg); color:var(--ok); } .badge.warn { background:var(--warnbg); color:var(--warn); } .badge.bad { background:var(--badbg); color:var(--bad); } .badge.off { background:var(--hover); color:var(--muted); }
        .problem { display:grid; grid-template-columns:6em 1fr; gap:0 16px; border:1px solid var(--line); margin:-1px 0 0; padding:12px 16px; background:var(--card); }
        .sev { font-weight:600; }
        .problem.bad .sev { color:var(--bad); } .problem.warn .sev { color:var(--warn); }
        .problem.bad { border-left:3px solid var(--bad); } .problem.warn { border-left:3px solid var(--warn); }
        .detail { white-space:pre-line; display:inline-block; max-width:80ch; }
        .problem .muted { color:var(--muted); color:color-mix(in srgb, var(--text) 76%, var(--card)); }
        .muted { color:var(--muted); } .small { font-size:13px; }
        td.small { overflow-wrap:anywhere; min-width:220px; }
        .tiles { display:grid; grid-template-columns:repeat(auto-fit, minmax(160px, 1fr)); margin:0 0 12px; border-top:1px solid var(--line); border-left:1px solid var(--line); background:var(--card); }
        .tile { padding:10px 14px; border-right:1px solid var(--line); border-bottom:1px solid var(--line); }
        .tile .n { font-size:36px; font-weight:500; line-height:1.15; font-variant-numeric:tabular-nums; } .tile .l { color:var(--muted); font-size:13px; letter-spacing:.04em; }
        .tile.bad .n { color:var(--bad); } .tile.warn .n { color:var(--warn); } .tile.ok .n { color:var(--ok); }
        .field { margin:0 0 16px; } .field label { display:block; font-weight:600; margin-bottom:3px; }
        .field.check label { font-weight:400; }
        .field small { display:block; color:var(--muted); margin-top:3px; }
        input[type=text], input[type=password], input[type=number], input[type=email], select { width:100%; max-width:460px; padding:6px 8px; border:1px solid var(--line); border-radius:0; background:var(--card); color:var(--text); font:inherit; }
        textarea { width:100%; max-width:460px; padding:6px 8px; border:1px solid var(--line); border-radius:0; background:var(--card); color:var(--text); font:inherit; }
        input[type=checkbox], input[type=radio] { accent-color:var(--accent); }
        input:focus, select:focus, button:focus-visible, .btn:focus-visible { outline:2px solid var(--accent); outline-offset:1px; }
        button, .btn { padding:6px 14px; border:1px solid var(--accent); background:var(--accent); color:var(--onaccent); border-radius:0; font:inherit; font-weight:600; cursor:pointer; text-decoration:none; display:inline-block; }
        .btn:hover { text-decoration:none; filter:brightness(1.08); }
        button.secondary, .btn.secondary { background:transparent; color:var(--accent); }
        button.danger { background:var(--bad); border-color:var(--bad); }
        button.link { background:none; border:none; color:var(--accent); padding:0; font-weight:400; }
        form.inline { display:inline; margin:0; }
        .actions { display:flex; gap:8px; flex-wrap:wrap; align-items:center; margin-top:12px; }
        .notice { padding:10px 14px; margin:0 0 12px; border:1px solid var(--line); }
        .notice.bad { background:var(--badbg); color:var(--bad); } .notice.ok { background:var(--okbg); color:var(--ok); } .notice.warn { background:var(--warnbg); color:var(--warn); }
        .narrow { max-width:520px; margin:40px auto; }
        .grid2 { display:grid; grid-template-columns:repeat(auto-fit, minmax(300px, 1fr)); gap:0 28px; }
        details summary { cursor:pointer; font-weight:600; }
        .card h2.first { margin-top:0; }
        .tabs { display:flex; gap:16px; flex-wrap:wrap; border-bottom:1px solid var(--line); margin:0 0 16px; }
        .tabs a { padding:8px 0; color:var(--muted); }
        .tabs a:hover { text-decoration:none; color:var(--text); }
        .tabs a.on { color:var(--text); font-weight:600; box-shadow:inset 0 -2px 0 var(--accent); }
        .tabs .badge { margin-left:6px; }
        .ranges { display:flex; gap:10px; margin:0 0 12px; align-items:center; } .ranges a.on { color:var(--text); font-weight:600; text-decoration:none; }
        .overview td { vertical-align:middle; } .overview td:first-child { font-weight:600; white-space:nowrap; }
        .overview td:first-child::before { content:""; display:inline-block; width:8px; height:8px; margin-right:10px; background:var(--off); }
        .overview tr.rowok td:first-child::before { background:var(--ok); } .overview tr.rowwarn td:first-child::before { background:var(--warn); } .overview tr.rowbad td:first-child::before { background:var(--bad); }
        .overview tr.rowoff td { color:var(--muted); }
        .chartgrid { display:grid; grid-template-columns:repeat(auto-fit, minmax(420px, 1fr)); gap:12px; margin:0 0 12px; }
        .chart { background:var(--card); border:1px solid var(--line); padding:10px 14px; }
        .chart h4 { margin:0 0 4px; font-size:14px; letter-spacing:.04em; font-weight:600; }
        .chartsvg { width:100%; max-width:760px; height:auto; display:block; } .spark { display:inline-block; vertical-align:middle; }
        .chartsvg .grid { stroke:var(--line); stroke-width:1; } .chartsvg .tick { fill:var(--muted); font-size:12px; }
        .chartsvg .threshold { stroke:var(--bad); stroke-width:1; stroke-dasharray:4 3; }
        .line { stroke-width:1.5; stroke-linejoin:round; } .line.s0 { stroke:var(--accent); } .line.s1 { stroke:var(--blue); } .line.s2 { stroke:var(--ok); } .line.s3 { stroke:var(--bad); }
        .spark .line { stroke:var(--text); stroke-width:1.25; } .spark .end { fill:var(--accent); }
        .sw { display:inline-block; width:10px; height:10px; margin-right:4px; background:var(--accent); } .sw.s1 { background:var(--blue); } .sw.s2 { background:var(--ok); } .sw.s3 { background:var(--bad); }
        .legend { margin:4px 0 0; } .key { margin-right:14px; }
        .more { margin:8px 0 0; }
        @media (max-width:720px) { .tabs { gap:12px; } }
        @media (max-width:520px) { .chartgrid { grid-template-columns:1fr; } main { padding:16px 12px 50px; } .bar { padding:8px 12px; } }
        @media (prefers-reduced-motion:reduce) { * { transition:none !important; } }
        """;
}
