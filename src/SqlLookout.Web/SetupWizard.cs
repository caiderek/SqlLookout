using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Web;

/// <summary>
/// The pages behind <c>sqllookout setup</c>: a small wizard that collects the options and then runs install, upgrade or
/// uninstall through <see cref="ISetupRunner"/>. It runs with the rights of the person who started it; only the command
/// it launches is elevated, and Windows asks for that permission then. Everything is under a random path that only the
/// opened browser tab knows, is refused for any other Host name, and every change needs a custom header a form cannot send.
/// </summary>
public sealed class SetupWizard
{
    private readonly ISetupHost host;
    private readonly ISetupRunner runner;
    private readonly string sourceDir;
    private readonly string token;
    private readonly Action stop;

    public SetupWizard(ISetupHost host, ISetupRunner runner, string sourceDir, string token, Action stop)
    {
        this.host = host;
        this.runner = runner;
        this.sourceDir = sourceDir;
        this.token = token;
        this.stop = stop;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    /// <summary>The SQL Server the history and settings go to; Auth is "sql" (a SQL login) or "windows".</summary>
    public sealed record StoreFields(string? Server, string? Database, string? Auth, string? User, string? Password, bool? Trust);

    public sealed record RunRequest(
        string? Action, string? Lang, string? Dir, string? Data, string? Port, bool? AllComputers, bool? Firewall,
        string? Account, string? Password, StoreFields? Store, bool? DeleteData);

    public sealed record CheckRequest(string? Lang, StoreFields? Store);

    /// <summary>The connection string the fields describe, or what is missing. The pieces are quoted by the builder, never joined by hand.</summary>
    public static (string? Connection, string? Error) BuildStoreConnection(StoreFields? f)
    {
        var server = f?.Server?.Trim() ?? "";
        var database = f?.Database?.Trim() ?? "";
        if (server.Length == 0) return (null, Loc.T("Enter the SQL Server name (for example DB01 or DB01\\SQL2019 or 10.0.0.5,1433)."));
        if (database.Length == 0) return (null, Loc.T("Enter the database name."));

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder { DataSource = server, InitialCatalog = database, TrustServerCertificate = f!.Trust != false };
        if (f.Auth == "windows")
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(f.User)) return (null, Loc.T("Enter the SQL login name."));
            if (string.IsNullOrEmpty(f.Password)) return (null, Loc.T("Enter the SQL login password."));
            builder.UserID = f.User.Trim();
            builder.Password = f.Password;
        }
        return (builder.ConnectionString, null);
    }

    public static void Map(WebApplication app, SetupWizard wizard)
    {
        var root = "/" + wizard.token;
        app.MapGet(root, (HttpContext ctx) => wizard.Guard(ctx) ?? Results.Content(wizard.Page(ctx.Request.Query["lang"].ToString()), "text/html; charset=utf-8"));
        app.MapPost(root + "/check-store", async (HttpContext ctx) =>
            wizard.Guard(ctx, post: true) ?? Results.Json(await wizard.CheckAsync(await ctx.Request.ReadFromJsonAsync<CheckRequest>(Json) ?? new CheckRequest(null, null)), Json));
        app.MapPost(root + "/run", async (HttpContext ctx) =>
            wizard.Guard(ctx, post: true) ?? Results.Json(await wizard.RunAsync(await ctx.Request.ReadFromJsonAsync<RunRequest>(Json) ?? new RunRequest(null, null, null, null, null, null, null, null, null, null, null)), Json));
        app.MapPost(root + "/exit", (HttpContext ctx) =>
        {
            var refused = wizard.Guard(ctx, post: true);
            if (refused is not null) return refused;
            wizard.stop();
            return Results.Json(new { ok = true });
        });
    }

    /// <summary>Null when the request may go on, else the refusal.</summary>
    private IResult? Guard(HttpContext ctx, bool post = false)
    {
        // A page on another site can make a browser send requests to this address; it cannot set this Host name or header.
        var hostName = ctx.Request.Host.Host;
        if (hostName is not ("127.0.0.1" or "localhost")) return Results.NotFound();
        if (post && (ctx.Request.Headers["X-Wizard"] != "1" || ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return null;
    }

    private static string PickLanguage(string? asked) =>
        Loc.IsSupported(asked) ? asked! : System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Loc.TraditionalChinese : Loc.English;

    public async Task<object> CheckAsync(CheckRequest request)
    {
        using var _ = Loc.Use(PickLanguage(request.Lang));
        var (text, missing) = BuildStoreConnection(request.Store);
        if (text is null) return new { ok = false, message = missing };
        if (StoreConnectionFile.Validate(text) is { } problem) return new { ok = false, message = problem };
        var check = await host.CheckStoreAsync(text, create: false);
        if (check.Failure is not null) return new { ok = false, message = check.Failure };
        return new
        {
            ok = true,
            message = check.WillCreate
                ? Loc.T("Connected. The database does not exist yet; it will be created when you install.")
                : Loc.T("Connected, and the login may create tables."),
        };
    }

    public async Task<object> RunAsync(RunRequest request)
    {
        using var _ = Loc.Use(PickLanguage(request.Lang));
        var action = request.Action;
        if (action is not ("install" or "upgrade" or "uninstall")) return Fail(Loc.T("Unknown action."));

        var args = new List<string> { action };
        var temp = Directory.CreateTempSubdirectory("SqlLookout-wizard-");
        try
        {
            if (action == "install")
            {
                var error = BuildInstallArgs(request, args, temp.FullName);
                if (error is not null) return Fail(error);
            }
            else if (action == "uninstall" && request.DeleteData == true)
            {
                args.Add("--delete-data");
            }

            // The same checks as the command line: what the command would refuse is refused here, before Windows asks anything.
            var (_, parseError) = SetupOptionsParser.Parse(action, [.. args]);
            if (parseError is not null) return Fail(parseError);

            var (exit, output) = await runner.RunAsync(args);
            var port = int.TryParse(request.Port, out var p) ? p : 8443;
            return new
            {
                ok = exit == 0,
                exitCode = exit,
                output = output.Trim(),
                message = exit == 0 ? Done(action) : exit == ElevatedSetupRunner.Declined ? Loc.T("Windows permission was declined, so nothing was changed.") : Loc.T("It did not finish. The lines below say why."),
                url = exit == 0 && action == "install" ? $"https://localhost:{port}/" : null,
            };
        }
        finally
        {
            // Holds the password and the connection string only for the length of the run.
            try { temp.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Done(string action) => action switch
    {
        "install" => Loc.T("Installed. Open the page on this computer to create the administrator account."),
        "upgrade" => Loc.T("Upgraded. Your data was not touched."),
        _ => Loc.T("Uninstalled."),
    };

    private static object Fail(string message) => new { ok = false, exitCode = 2, output = "", message, url = (string?)null };

    private string? BuildInstallArgs(RunRequest r, List<string> args, string tempDir)
    {
        void Add(string option, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) { args.Add(option); args.Add(value.Trim()); }
        }

        if (!int.TryParse(r.Port, out _)) return Loc.T("The port must be a number from 1 to 65535.");
        Add("--dir", r.Dir);
        Add("--data", r.Data);
        Add("--port", r.Port);
        args.Add("--listen"); args.Add(r.AllComputers == false ? "127.0.0.1" : "0.0.0.0");
        if (r.Firewall == false) args.Add("--no-firewall");

        if (!string.IsNullOrWhiteSpace(r.Account))
        {
            Add("--account", r.Account);
            if (!string.IsNullOrEmpty(r.Password))
            {
                var file = Path.Combine(tempDir, "account-password.txt");
                File.WriteAllText(file, r.Password);
                Add("--password-file", file);
            }
        }

        if (r.Store is not null)
        {
            var (store, missing) = BuildStoreConnection(r.Store);
            if (store is null) return missing;
            if (StoreConnectionFile.Validate(store) is { } storeProblem) return storeProblem;
            var file = Path.Combine(tempDir, "store.txt");
            File.WriteAllText(file, store);
            Add("--store-connection-file", file);
        }
        return null;
    }

    // ---- the page ----

    private string Page(string? askedLanguage)
    {
        var lang = PickLanguage(askedLanguage);
        using var _ = Loc.Use(lang);

        var image = host.ServiceExists() ? host.ServiceImagePath() : null;
        var installed = image is null ? null : ServiceCommandLine.Read(image);
        var newVersion = host.ProgramVersion(sourceDir);
        var oldVersion = installed is null ? null : host.ProgramVersion(Path.GetDirectoryName(installed.Value.Exe)!);

        var body = installed is null ? InstallForm() : InstalledPanel(installed.Value, oldVersion, newVersion);
        var langLinks = string.Join(" · ", Loc.Languages.Select(l => l.Code == lang ? $"<b>{H.E(l.Name)}</b>" : $"""<a href="/{token}?lang={l.Code}">{H.E(l.Name)}</a>"""));

        return $$"""
            <!doctype html>
            <html lang="{{Loc.HtmlLang}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{H.E(Loc.T("SqlLookout setup"))}}</title>
            <style>
            :root { color-scheme: light dark; --bg:#f6f7f9; --card:#fff; --ink:#1b1f24; --muted:#5b6570; --line:#d5dae0; --accent:#0b5fff; --bad:#b42318; --good:#087443; }
            @media (prefers-color-scheme: dark) { :root { --bg:#14171a; --card:#1d2125; --ink:#e8ebee; --muted:#9aa4af; --line:#333a41; --accent:#6ea0ff; --bad:#ff8a80; --good:#5fd39a; } }
            body { margin:0; background:var(--bg); color:var(--ink); font:15px/1.5 system-ui, "Segoe UI", sans-serif; }
            main { max-width:720px; margin:0 auto; padding:24px 16px 64px; }
            h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:16px; margin:0 0 8px; }
            .top { display:flex; justify-content:space-between; align-items:baseline; gap:12px; flex-wrap:wrap; }
            .card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:16px; margin:16px 0; }
            label { display:block; margin:12px 0 4px; font-weight:600; } .hint { color:var(--muted); font-size:13px; margin:2px 0 0; }
            input[type=text], input[type=password], textarea { width:100%; box-sizing:border-box; padding:8px; border:1px solid var(--line); border-radius:6px; background:var(--bg); color:var(--ink); font:inherit; }
            textarea { min-height:70px; font-family:ui-monospace, Consolas, monospace; font-size:13px; }
            .row { display:flex; gap:8px; align-items:center; flex-wrap:wrap; } .opt { display:flex; gap:8px; align-items:flex-start; margin:6px 0; font-weight:400; }
            button { padding:9px 18px; border-radius:6px; border:1px solid var(--accent); background:var(--accent); color:#fff; font:inherit; cursor:pointer; }
            button.plain { background:transparent; color:var(--accent); } button:disabled { opacity:.5; cursor:default; }
            a { color:var(--accent); } .bad { color:var(--bad); } .good { color:var(--good); }
            pre { white-space:pre-wrap; word-break:break-word; background:var(--bg); border:1px solid var(--line); border-radius:6px; padding:10px; font-size:13px; }
            [hidden] { display:none !important; }
            </style>
            </head>
            <body>
            <main>
            <div class="top"><div><h1>{{H.E(Loc.T("SqlLookout setup"))}}</h1><div class="hint">{{H.E(Loc.T("Version {0} is in this folder.", newVersion?.ToString() ?? "?"))}}</div></div><div>{{langLinks}}</div></div>
            {{body}}
            <div class="card" id="result" hidden>
              <h2 id="resultTitle"></h2>
              <p id="resultLink" hidden><a id="resultHref" href="#" target="_blank" rel="noopener"></a></p>
              <pre id="resultOutput" hidden></pre>
            </div>
            <div class="row"><button class="plain" id="close" type="button">{{H.E(Loc.T("Close setup"))}}</button><span class="hint">{{H.E(Loc.T("Closing stops this wizard; the installed service keeps running."))}}</span></div>
            </main>
            <script>
            const base = "/{{token}}";
            const lang = "{{lang}}";
            const text = { waiting: {{JsonSerializer.Serialize(Loc.T("Waiting for Windows permission, then working…"))}}, checking: {{JsonSerializer.Serialize(Loc.T("Checking…"))}}, open: {{JsonSerializer.Serialize(Loc.T("Open SqlLookout"))}}, closed: {{JsonSerializer.Serialize(Loc.T("The wizard is closed. You can close this tab."))}} };
            const $ = id => document.getElementById(id);
            async function post(path, body) {
              const r = await fetch(base + path, { method: "POST", headers: { "Content-Type": "application/json", "X-Wizard": "1" }, body: JSON.stringify({ ...body, lang }) });
              return r.json();
            }
            function show(res) {
              $("result").hidden = false;
              $("resultTitle").textContent = res.message;
              $("resultTitle").className = res.ok ? "good" : "bad";
              $("resultOutput").hidden = !res.output; $("resultOutput").textContent = res.output || "";
              $("resultLink").hidden = !res.url; if (res.url) { $("resultHref").href = res.url; $("resultHref").textContent = text.open + " — " + res.url; }
              $("result").scrollIntoView({ behavior: "smooth" });
            }
            async function run(action, extra, button) {
              button.disabled = true; $("result").hidden = false; $("resultTitle").className = ""; $("resultTitle").textContent = text.waiting; $("resultOutput").hidden = true; $("resultLink").hidden = true;
              try { show(await post("/run", { action, ...extra })); } catch (e) { show({ ok: false, message: String(e), output: "" }); }
              button.disabled = false;
            }
            const val = id => $(id) ? $(id).value : undefined;
            const checked = id => $(id) ? $(id).checked : undefined;
            if ($("install")) {
              document.querySelectorAll("input[name=store]").forEach(r => r.addEventListener("change", () => { $("storeBox").hidden = document.querySelector("input[name=store]:checked").value !== "sql"; }));
              document.querySelectorAll("input[name=who]").forEach(r => r.addEventListener("change", () => { $("accountBox").hidden = document.querySelector("input[name=who]:checked").value !== "account"; }));
              document.querySelectorAll("input[name=listen]").forEach(r => r.addEventListener("change", () => { $("fwRow").hidden = document.querySelector("input[name=listen]:checked").value !== "all"; }));
              const storeFields = () => ({
                server: val("sqlServer"), database: val("sqlDb"), auth: document.querySelector("input[name=sqlauth]:checked").value,
                user: val("sqlUser"), password: val("sqlPass"), trust: checked("sqlTrust")
              });
              document.querySelectorAll("input[name=sqlauth]").forEach(r => r.addEventListener("change", () => {
                const windows = document.querySelector("input[name=sqlauth]:checked").value === "windows";
                $("sqlLoginBox").hidden = windows; $("sqlWinHint").hidden = !windows;
              }));
              $("test").addEventListener("click", async () => {
                $("testResult").className = "hint"; $("testResult").textContent = text.checking;
                const res = await post("/check-store", { store: storeFields() });
                $("testResult").className = res.ok ? "good" : "bad"; $("testResult").textContent = res.message;
              });
              $("install").addEventListener("click", () => run("install", {
                dir: val("dir"), data: val("data"), port: val("port"),
                allComputers: document.querySelector("input[name=listen]:checked").value === "all", firewall: checked("fw"),
                account: document.querySelector("input[name=who]:checked").value === "account" ? val("account") : "", password: val("password"),
                store: document.querySelector("input[name=store]:checked").value === "sql" ? storeFields() : null
              }, $("install")));
            }
            if ($("upgrade")) $("upgrade").addEventListener("click", () => run("upgrade", {}, $("upgrade")));
            if ($("uninstall")) $("uninstall").addEventListener("click", () => run("uninstall", { deleteData: checked("deleteData") }, $("uninstall")));
            $("close").addEventListener("click", async () => { try { await post("/exit", {}); } catch (e) { } document.body.innerHTML = "<main><p>" + text.closed + "</p></main>"; });
            </script>
            </body>
            </html>
            """;
    }

    private string InstallForm()
    {
        var dir = Path.Combine(host.ProgramFilesDir, "SqlLookout");
        var data = Path.Combine(host.ProgramDataDir, "SqlLookout");
        return $"""
            <div class="card">
              <h2>{H.E(Loc.T("Install"))}</h2>
              <p class="hint">{H.E(Loc.T("SqlLookout is not installed on this computer. These settings can be changed later by installing again."))}</p>

              <label for="dir">{H.E(Loc.T("Program folder"))}</label>
              <input type="text" id="dir" value="{H.E(dir)}">
              <label for="data">{H.E(Loc.T("Data folder"))}</label>
              <input type="text" id="data" value="{H.E(data)}">
              <p class="hint">{H.E(Loc.T("Only Administrators, SYSTEM and the service account may read it: it holds the encrypted passwords."))}</p>

              <label for="port">{H.E(Loc.T("Web page port"))}</label>
              <input type="text" id="port" value="8443" inputmode="numeric" style="max-width:8em">
              <label>{H.E(Loc.T("Who can open the web page"))}</label>
              <label class="opt"><input type="radio" name="listen" value="all" checked> {H.E(Loc.T("Other computers on the network too"))}</label>
              <label class="opt"><input type="radio" name="listen" value="local"> {H.E(Loc.T("This computer only"))}</label>
              <label class="opt" id="fwRow"><input type="checkbox" id="fw" checked> {H.E(Loc.T("Allow the port through the Windows firewall"))}</label>

              <label>{H.E(Loc.T("Run the service as"))}</label>
              <label class="opt"><input type="radio" name="who" value="system" checked> {H.E(Loc.T("The computer's own account (LocalSystem)"))}</label>
              <label class="opt"><input type="radio" name="who" value="account"> {H.E(Loc.T("A Windows account"))}</label>
              <div id="accountBox" hidden>
                <input type="text" id="account" placeholder="DOMAIN\name">
                <input type="password" id="password" placeholder="{H.E(Loc.T("Password (not needed for a managed service account ending with $)"))}" style="margin-top:6px">
                <p class="hint">{H.E(Loc.T("The account needs the 'Log on as a service' right. Servers that use Windows authentication see this account."))}</p>
              </div>

              <label>{H.E(Loc.T("Keep the history and settings in"))}</label>
              <label class="opt"><input type="radio" name="store" value="sqlite" checked> {H.E(Loc.T("A file in the data folder (SQLite) — nothing else to set up"))}</label>
              <label class="opt"><input type="radio" name="store" value="sql"> {H.E(Loc.T("A SQL Server database"))}</label>
              <div id="storeBox" hidden>
                <label for="sqlServer">{H.E(Loc.T("SQL Server (name or address)"))}</label>
                <input type="text" id="sqlServer" placeholder="DB01  /  DB01\SQL2019  /  10.0.0.5,1433">
                <label for="sqlDb">{H.E(Loc.T("Database"))}</label>
                <input type="text" id="sqlDb" value="SqlLookoutStore">
                <label>{H.E(Loc.T("Sign in with"))}</label>
                <label class="opt"><input type="radio" name="sqlauth" value="sql" checked> {H.E(Loc.T("A SQL Server login (name and password)"))}</label>
                <label class="opt"><input type="radio" name="sqlauth" value="windows"> {H.E(Loc.T("Windows authentication"))}</label>
                <div id="sqlLoginBox">
                  <input type="text" id="sqlUser" placeholder="{H.E(Loc.T("Login name"))}" autocomplete="off">
                  <input type="password" id="sqlPass" placeholder="{H.E(Loc.T("Password"))}" autocomplete="new-password" style="margin-top:6px">
                </div>
                <p class="hint" id="sqlWinHint" hidden>{H.E(Loc.T("The service signs in as the account it runs as (the computer's own account unless you chose a Windows account above), so that account needs a login on the SQL Server. The test below uses your own account instead."))}</p>
                <label class="opt"><input type="checkbox" id="sqlTrust" checked> {H.E(Loc.T("Trust the server's certificate (needed when it has a self-signed one)"))}</label>
                <div class="row" style="margin-top:6px"><button class="plain" id="test" type="button">{H.E(Loc.T("Test the connection"))}</button><span id="testResult" class="hint"></span></div>
                <p class="hint">{H.E(Loc.T("Create the database and login first with sql\\create-store-database.sql from this folder. Nothing already in a SQLite file is moved over."))}</p>
              </div>

              <p style="margin-top:20px"><button id="install" type="button">{H.E(Loc.T("Install"))}</button></p>
              <p class="hint">{H.E(Loc.T("Windows will ask for permission when you press Install."))}</p>
            </div>
            """;
    }

    private string InstalledPanel((string Exe, string DataDir, int Port, string Listen, string? StoreFile) installed, Version? oldVersion, Version? newVersion)
    {
        var older = oldVersion is not null && newVersion is not null && newVersion < oldVersion;
        var same = oldVersion is not null && newVersion is not null && newVersion == oldVersion;
        var upgradeNote = older
            ? Loc.T("This folder holds version {0}, older than the installed {1}, so it cannot be used to upgrade.", newVersion, oldVersion)
            : same
                ? Loc.T("The installed version is the same as the one in this folder.")
                : Loc.T("Version {0} in this folder replaces the installed {1}. Your data is not touched; if the new version does not start, the old one is put back.", newVersion?.ToString() ?? "?", oldVersion?.ToString() ?? "?");
        var store = installed.StoreFile is null ? Loc.T("a file in the data folder (SQLite)") : Loc.T("a SQL Server database");
        return $"""
            <div class="card">
              <h2>{H.E(Loc.T("SqlLookout is installed"))}</h2>
              <p>{H.E(Loc.T("Program: {0}", installed.Exe))}<br>{H.E(Loc.T("Data folder: {0}", installed.DataDir))}<br>{H.E(Loc.T("History and settings are kept in {0}.", store))}<br>{H.E(Loc.T("Version installed: {0}", oldVersion?.ToString() ?? "?"))}</p>
            </div>
            <div class="card">
              <h2>{H.E(Loc.T("Upgrade"))}</h2>
              <p class="hint">{H.E(upgradeNote)}</p>
              <p><button id="upgrade" type="button"{(older ? " disabled" : "")}>{H.E(Loc.T("Upgrade"))}</button></p>
            </div>
            <div class="card">
              <h2>{H.E(Loc.T("Uninstall"))}</h2>
              <p class="hint">{H.E(Loc.T("Removes the service and the firewall rule."))}</p>
              <label class="opt"><input type="checkbox" id="deleteData"> {H.E(Loc.T("Also delete the data folder (history and settings kept in it cannot be recovered)"))}</label>
              <p><button class="plain" id="uninstall" type="button">{H.E(Loc.T("Uninstall"))}</button></p>
            </div>
            """;
    }
}

/// <summary>Starts the wizard on a random local port and opens it in the default browser.</summary>
public static class SetupWizardHost
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);

    public static async Task<int> RunAsync(ISetupHost host, string sourceDir, string exePath)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = sourceDir });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        var lifetime = app.Lifetime;
        var wizard = new SetupWizard(host, new ElevatedSetupRunner(exePath), sourceDir, token, lifetime.StopApplication);
        var lastRequest = DateTime.UtcNow;
        app.Use((ctx, next) =>
        {
            lastRequest = DateTime.UtcNow;
            return next(ctx);
        });
        SetupWizard.Map(app, wizard);
        await app.StartAsync();

        var address = app.Urls.First(u => u.Contains("127.0.0.1", StringComparison.Ordinal));
        var url = $"{address.TrimEnd('/')}/{token}";
        Console.WriteLine("SqlLookout setup is open in your browser. Keep this window open until you are done.");
        Console.WriteLine($"If the browser did not open, go to: {url}");
        try
        {
            // SQLLOOKOUT_SETUP_NO_BROWSER=1 only prints the address (for scripts and tests that must not open a window).
            if (Environment.GetEnvironmentVariable("SQLLOOKOUT_SETUP_NO_BROWSER") != "1")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The address above is printed for that case.
        }

        // A wizard left open for hours should not keep listening.
        _ = Task.Run(async () =>
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                if (DateTime.UtcNow - lastRequest > IdleLimit) lifetime.StopApplication();
            }
        });
        await app.WaitForShutdownAsync();
        return 0;
    }
}
