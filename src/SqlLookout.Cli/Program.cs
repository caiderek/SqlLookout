using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;
using SqlLookout.Web;

const string Usage = """
    sqllookout web          [--port 8443] [--listen 0.0.0.0] [--data <folder>] [--cert <pfx> --cert-password-file <file>]
                            [--store-connection-file <file>]
                            web interface + monitoring in one process (also runs as a Windows service).
                            With --store-connection-file (one SQL Server connection string on its first line) the history and
                            settings are kept in that SQL Server database instead of the SQLite file in the data folder.

    sqllookout reset-password [--data <folder>] [--store-connection-file <file>] [--user <name>]
                            set a new sign-in password when the old one is forgotten (asks for it; or pipe it in on stdin);
                            signs everyone out. Run it on the machine that holds the data folder.

    sqllookout setup        opens a setup wizard in your browser (install, upgrade or uninstall the Windows service); no options.
                            Windows asks for administrator permission when you press the button, not before.

    sqllookout install      [--dir <folder>] [--data <folder>] [--port 8443] [--listen 0.0.0.0] [--cert <pfx> --cert-password-file <file>]
                            [--account <name> --password-file <file>] [--store-connection-file <file>] [--no-firewall]
                            (as administrator) copy the program, create the Windows service, lock down the data folder,
                            open the firewall port and start it. Default folders: Program Files\SqlLookout, ProgramData\SqlLookout
    sqllookout upgrade      (as administrator, from the NEW unzipped folder) replace the installed program, keep the data,
                            go back to the old version if the new one does not start
    sqllookout uninstall    [--delete-data]
                            (as administrator) remove the service and the firewall rule; data is kept unless --delete-data

    sqllookout mcp          [--data <folder>] [--store-connection-file <file>]
                            read-only MCP server over stdio for AI assistants: reads the history the monitor recorded
                            (list_instances, get_problems, get_overview, get_metric_history); it cannot change anything

    Headless mode, settings in a JSON file instead of the web interface:
    sqllookout run          [--config <file>]   collect and alert until stopped (also runs as a Windows service)
    sqllookout run-once     [--config <file>]   collect everything once, no mail, then print the status
    sqllookout status       [--config <file>]   print what the stored data says right now, no collection
    sqllookout check-config [--config <file>]   validate the config and test each connection

    Default config: sqllookout.json next to the program. Default data folder for 'web': data next to the program.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

var command = args[0];
if (command == "web")
{
    if (args.Skip(1).Any(a => a is "-h" or "--help"))
    {
        Console.WriteLine(Usage);
        return 0;
    }
    // A misspelled option must not silently start a server with defaults (for example on all network interfaces).
    if (OptionError(["--port", "--listen", "--data", "--cert", "--cert-password-file", "--store-connection-file"]) is { } optionError)
    {
        Console.Error.WriteLine(optionError);
        Console.Error.WriteLine(Usage);
        return 2;
    }
    return await WebAsync();
}

if (command == "reset-password")
{
    if (OptionError(["--data", "--user", "--store-connection-file"]) is { } resetOptionError)
    {
        Console.Error.WriteLine(resetOptionError);
        Console.Error.WriteLine(Usage);
        return 2;
    }
    var resetProtector = new DpapiSecretProtector();
    string? resetStoreConnection;
    try
    {
        resetStoreConnection = StoreConnectionString(resetProtector);
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    var resetFile = Path.Combine(Path.GetFullPath(Option("--data") ?? "data", AppContext.BaseDirectory), "sqllookout.db");
    if (resetStoreConnection is null && !File.Exists(resetFile))
    {
        Console.Error.WriteLine($"No data file at {resetFile}. Pass --data <folder> with the folder that holds sqllookout.db.");
        return 2;
    }
    await using var resetStore = resetStoreConnection is null
        ? new SettingsStore(resetFile, resetProtector)
        : SettingsStore.ForSqlServer(resetStoreConnection, resetProtector);
    await resetStore.InitializeAsync();
    var typed = !Console.IsInputRedirected;
    return await PasswordResetCommand.RunAsync(resetStore, Option("--user"), prompt =>
    {
        if (!typed) return Console.In.ReadLine();
        Console.Error.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return buffer.ToString();
    }, typed, Console.Out, Console.Error);
}

if (command is "install" or "upgrade" or "uninstall")
{
    var (setupOptions, setupError) = SetupOptionsParser.Parse(command, args);
    if (setupOptions is null)
    {
        Console.Error.WriteLine(setupError);
        Console.Error.WriteLine(Usage);
        return 2;
    }
    var setupHost = new WindowsSetupHost();
    var sourceDir = AppContext.BaseDirectory;
    // The setup wizard starts this command elevated and reads what it printed from a file.
    await using var setupLog = setupOptions.OutputFile is null ? null : new StreamWriter(setupOptions.OutputFile, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    TextWriter setupOut = setupLog is null ? Console.Out : new TeeTextWriter(Console.Out, setupLog);
    TextWriter setupErr = setupLog is null ? Console.Error : new TeeTextWriter(Console.Error, setupLog);
    return command switch
    {
        "install" => await ServiceSetup.InstallAsync(setupOptions, sourceDir, setupHost, setupOut, setupErr),
        "upgrade" => await ServiceSetup.UpgradeAsync(sourceDir, setupHost, setupOut, setupErr),
        _ => ServiceSetup.Uninstall(setupOptions, setupHost, setupOut, setupErr),
    };
}

if (command == "setup")
{
    if (args.Length > 1)
    {
        Console.Error.WriteLine("'setup' takes no options.");
        Console.Error.WriteLine(Usage);
        return 2;
    }
    return await SetupWizardHost.RunAsync(new WindowsSetupHost(), AppContext.BaseDirectory, Environment.ProcessPath!);
}

if (command == "mcp")
{
    // stdout carries the protocol, so every message here goes to stderr.
    if (OptionError(["--data", "--store-connection-file"]) is { } mcpOptionError)
    {
        Console.Error.WriteLine(mcpOptionError);
        Console.Error.WriteLine(Usage);
        return 2;
    }
    string? mcpStoreConnection;
    try
    {
        mcpStoreConnection = StoreConnectionString(new DpapiSecretProtector());
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    if (mcpStoreConnection is not null)
    {
        try
        {
            await using var mcpStore = new SqlServerHistoryStore(mcpStoreConnection, readOnly: true);
            await mcpStore.InitializeAsync();
            await SqlLookout.Mcp.McpServerHost.RunAsync(mcpStore);
            return 0;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not read the SQL Server store: {ex.Message}");
            return 1;
        }
    }
    var mcpFile = Path.Combine(Path.GetFullPath(Option("--data") ?? "data", AppContext.BaseDirectory), "sqllookout.db");
    if (!File.Exists(mcpFile))
    {
        Console.Error.WriteLine($"No history file at {mcpFile}. Start 'sqllookout web' first so there is something to read, or pass --data <folder>.");
        return 2;
    }
    try
    {
        await SqlLookout.Mcp.McpServerHost.RunAsync(mcpFile);
        return 0;
    }
    catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Could not read {mcpFile}: {ex.Message}");
        return 1;
    }
}

var configPath = Path.GetFullPath(Option("--config") ?? Path.Combine(AppContext.BaseDirectory, "sqllookout.json"));

SqlLookoutConfig config;
try
{
    config = SqlLookoutConfig.Load(configPath);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"Config problem ({configPath}): {ex.Message}");
    return 2;
}

// A Windows service starts in System32, so every relative path is anchored to the config file's folder.
var dbPath = Path.GetFullPath(config.Database, Path.GetDirectoryName(configPath)!);

switch (command)
{
    case "check-config": return await CheckConfigAsync();
    case "run-once": return await RunOnceAsync();
    case "status": return await StatusAsync();
    case "run": return await RunAsync();
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

string? OptionError(string[] allowed)
{
    for (var i = 1; i < args.Length; i++)
    {
        if (!allowed.Contains(args[i])) return $"Unknown option '{args[i]}'.";
        if (i + 1 >= args.Length) return $"Option '{args[i]}' needs a value.";
        i++;
    }
    return null;
}

// The SQL Server connection string when --store-connection-file is given, else null (the SQLite file is used).
// Throws InvalidDataException with a message meant for the person running the command.
string? StoreConnectionString(ISecretProtector protector)
{
    var file = Option("--store-connection-file");
    if (file is null) return null;
    var text = StoreConnectionFile.Read(Path.GetFullPath(file, AppContext.BaseDirectory), protector);
    if (StoreConnectionFile.Validate(text) is { } problem) throw new InvalidDataException(problem);
    return text;
}

string? Option(string name)
{
    for (var i = 1; i + 1 < args.Length; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static IAlertSender SenderFor(SqlLookoutConfig c) => AlertSenders.For(c.Alerts, () => new ConsoleAlertSender());

IAlertSender CreateSender() => SenderFor(config);

async Task<int> WebAsync()
{
    try
    {
        return await WebCoreAsync();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SqliteException or SqlException or FormatException or InvalidOperationException)
    {
        // Startup problems (a missing certificate file, a data folder that cannot be written, a port already in use)
        // get one readable line instead of a stack trace.
        Console.Error.WriteLine($"Could not start: {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}

async Task<int> WebCoreAsync()
{
    var dataDir = Path.GetFullPath(Option("--data") ?? "data", AppContext.BaseDirectory);
    if (!int.TryParse(Option("--port") ?? "8443", out var port) || port is < 1 or > 65535)
    {
        Console.Error.WriteLine("--port must be a number from 1 to 65535.");
        return 2;
    }
    if (!IPAddress.TryParse(Option("--listen") ?? "0.0.0.0", out var listen))
    {
        Console.Error.WriteLine("--listen must be an IP address, for example 0.0.0.0 (all) or 127.0.0.1 (this machine only).");
        return 2;
    }

    Directory.CreateDirectory(dataDir);
    var dbFile = Path.Combine(dataDir, "sqllookout.db");
    var protector = new DpapiSecretProtector();

    string? storeConnection;
    try
    {
        storeConnection = StoreConnectionString(protector);
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    // The data folder still holds the certificate, the sign-in keys and the log; only history and settings move to SQL Server.
    await using var settings = storeConnection is null ? new SettingsStore(dbFile, protector) : SettingsStore.ForSqlServer(storeConnection, protector);
    await settings.InitializeAsync();
    await using IHistoryStore history = storeConnection is null ? new SqliteHistoryStore(dbFile) : new SqlServerHistoryStore(storeConnection);
    await history.InitializeAsync();
    var live = new LiveConfig(settings);
    await live.RefreshAsync();
    var diagnostic = new DiagnosticLog(Path.Combine(dataDir, "logs", "diagnostic.log")) { Enabled = (await settings.GetGlobalAsync()).DiagnosticLog };

    using var certificate = CertificateProvider.LoadOrCreate(dataDir, protector, Option("--cert"), Option("--cert-password-file"), out var certSource);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
    builder.Services.AddWindowsService(o => o.ServiceName = "SqlLookout");
    // Sign-in and form tokens are protected with keys kept in the data folder (encrypted with DPAPI), not in a user
    // profile folder that a service account may not have.
    builder.Services.AddDataProtection()
        .SetApplicationName("SqlLookout")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
        .ProtectKeysWithDpapi(protectToLocalMachine: true);
    // One line per web request is too noisy for a service log; warnings and errors still show.
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = 1_000_000;
        k.Listen(listen, port, o => o.UseHttps(certificate));
    });
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<IHistoryStore>(history);
    builder.Services.AddSingleton(live);
    builder.Services.AddSingleton(diagnostic);
    builder.Logging.AddProvider(new DiagnosticLoggerProvider(diagnostic));
    SqlLookoutWeb.AddServices(builder.Services);
    builder.Services.AddHostedService(sp => new MonitorHostedService(live, history, sp.GetRequiredService<ILogger<MonitorHostedService>>()));

    var app = builder.Build();
    SqlLookoutWeb.Map(app);

    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Using {Certificate}", certSource);
    logger.LogInformation("Open https://localhost:{Port}/ on this machine to finish setup (a browser warning about the certificate is expected for the self-signed one).", port);
    await app.RunAsync();
    return 0;
}

async Task<int> CheckConfigAsync()
{
    Console.WriteLine($"Config OK: {configPath}");
    Console.WriteLine($"History: {dbPath}");
    Console.WriteLine(config.Alerts.Email is null ? "Email: not configured (alerts go to the log only)" : $"Email: {config.Alerts.Email.Host}:{config.Alerts.Email.Port} -> {string.Join(", ", config.Alerts.Email.To)}");
    var bad = 0;
    foreach (var instance in config.Instances)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(instance.ResolveConnectionString()) { ConnectTimeout = 15, ApplicationName = "SqlLookout" };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            var server = await ServerInfo.ReadAsync(connection);
            Console.WriteLine($"{instance.Name}: connected, SQL Server {server.Major}.{server.Minor}.{server.Build}{(server.IsExpress ? " (Express)" : "")}");
            foreach (var c in CollectorCatalog.All())
            {
                var reason = c.UnsupportedReason(server);
                if (reason is not null) Console.WriteLine($"    {c.Name}: will be skipped ({reason})");
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or ArgumentException)
        {
            // The message is not printed for SQL errors: it can repeat connection string parts.
            Console.WriteLine($"{instance.Name}: FAILED ({(ex is SqlException s ? $"SQL error {s.Number}" : ex.GetType().Name)})");
            bad++;
        }
    }
    return bad == 0 ? 0 : 1;
}

async Task<int> RunOnceAsync()
{
    await using var store = new SqliteHistoryStore(dbPath);
    await store.InitializeAsync();
    var monitor = new MonitorLoop(config, store, new ConsoleAlertSender(), Console.WriteLine);
    var failures = 0;
    foreach (var instance in config.Instances)
        failures += await monitor.CollectInstanceOnceAsync(instance, force: true, [], new ServerInfoCache(TimeSpan.FromHours(1)), CancellationToken.None);
    await PrintStatusAsync(store);
    return failures == 0 ? 0 : 1;
}

async Task<int> StatusAsync()
{
    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine($"No history yet at {dbPath}. Run 'sqllookout run-once' first.");
        return 1;
    }
    await using var store = new SqliteHistoryStore(dbPath);
    await store.InitializeAsync();
    await PrintStatusAsync(store);
    return 0;
}

async Task PrintStatusAsync(IHistoryStore store)
{
    var disabled = config.Alerts.DisabledRules.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var evaluator = new AlertEvaluator(store, config.Alerts.Thresholds, disabled, config.IgnoredFor, config.ThresholdsFor);
    foreach (var instance in config.Instances)
    {
        Console.WriteLine();
        Console.WriteLine($"== {instance.Name} ==");
        var latest = await store.GetLatestAsync(instance.Name);
        var reach = await store.GetRecentRunsAsync(instance.Name, CollectorCatalog.Connectivity, 1);
        Console.WriteLine(reach.Count == 0 ? "no data yet" : $"connection: {reach[0].Status} at {reach[0].StartedUtc:yyyy-MM-dd HH:mm:ss} UTC {reach[0].Message}");

        foreach (var c in CollectorCatalog.All())
        {
            var runs = await store.GetRecentRunsAsync(instance.Name, c.Name, 1);
            if (runs.Count == 0) continue;
            Console.WriteLine($"  {c.Name,-16} {runs[0].Status,-12} {latest.Count(s => s.Collector == c.Name),4} values  {runs[0].Message}");
        }

        var findings = await evaluator.EvaluateAsync(instance.Name, DateTime.UtcNow);
        Console.WriteLine(findings.Count == 0 ? "problems: none" : $"problems: {findings.Count}");
        foreach (var f in findings.OrderByDescending(f => f.Severity))
            Console.WriteLine($"  [{f.Severity.ToString().ToUpperInvariant()}] {f.Title} - {f.Detail}");
    }
}

async Task<int> RunAsync()
{
    var builder = Host.CreateApplicationBuilder(args.Skip(1).ToArray());
    builder.Services.AddWindowsService(o => o.ServiceName = "SqlLookout");
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<IAlertSender>(_ => CreateSender());
    builder.Services.AddHostedService(sp => new MonitorService(
        sp.GetRequiredService<SqlLookoutConfig>(), dbPath, sp.GetRequiredService<IAlertSender>(),
        sp.GetRequiredService<ILogger<MonitorService>>()));
    await builder.Build().RunAsync();
    return 0;
}

internal sealed class MonitorHostedService(LiveConfig live, IHistoryStore history, ILogger<MonitorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No email configured: nothing is sent and nothing is recorded as "already told" (the dashboard shows the problems).
        var monitor = new MonitorLoop(() => live.Current, history,
            cfg => AlertSenders.For(cfg.Alerts, () => new NullAlertSender(), message => logger.LogWarning("{Message}", message)),
            message => logger.LogWarning("{Message}", message));
        await monitor.RunAsync(stoppingToken);
    }
}

internal sealed class MonitorService(SqlLookoutConfig config, string dbPath, IAlertSender sender, ILogger<MonitorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var store = new SqliteHistoryStore(dbPath);
        await store.InitializeAsync(stoppingToken);
        logger.LogInformation("SqlLookout started: {Count} instance(s), history {Db}", config.Instances.Count, dbPath);
        var monitor = new MonitorLoop(config, store, sender, message => logger.LogWarning("{Message}", message));
        await monitor.RunAsync(stoppingToken);
        logger.LogInformation("SqlLookout stopped");
    }
}

/// <summary>Copies warnings and errors, and everything from SqlLookout's own code, into the diagnostic log while it is on.</summary>
internal sealed class DiagnosticLoggerProvider(DiagnosticLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Writer(log, categoryName);
    public void Dispose() { }

    private sealed class Writer(DiagnosticLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => log.Enabled && (level >= LogLevel.Warning || category.StartsWith("SqlLookout", StringComparison.Ordinal) && level >= LogLevel.Information);

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            log.Write($"{level}/{category}", format(state, ex) + (ex is null ? "" : $" ({ex.GetType().Name}: {ex.Message})"));
        }
    }
}
