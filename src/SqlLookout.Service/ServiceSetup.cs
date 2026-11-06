using System.Net;

namespace SqlLookout.Service;

/// <summary>Outcome of checking the store: a failure text, or fine (with the database still to be created when <see cref="WillCreate"/>).</summary>
public sealed record StoreCheck(string? Failure, bool WillCreate);

/// <summary>Everything the setup commands need from the machine, so the steps can be tested without touching Windows.</summary>
public interface ISetupHost
{
    bool IsAdministrator { get; }
    string ProgramFilesDir { get; }
    string ProgramDataDir { get; }
    bool ServiceExists();
    string? ServiceImagePath();
    void StopService(TimeSpan timeout);
    void StartService(TimeSpan timeout);
    Task<bool> WaitForWebAsync(string address, int port, TimeSpan timeout);
    Version? ProgramVersion(string folder);
    (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args);
    ISecretProtector Protector { get; }
    /// <summary>Connects to the store database and checks the login may create tables; null when it works, else what is wrong.</summary>
    /// <summary>Checks the login can use the store; a store database that does not exist yet is fine and is created when <paramref name="create"/> is set.</summary>
    Task<StoreCheck> CheckStoreAsync(string connectionString, bool create);
}

/// <summary>
/// <c>sqllookout install</c>, <c>upgrade</c> and <c>uninstall</c>: the Windows service, its folder permissions and its
/// firewall rule in one step each. The data folder is never deleted unless <c>--delete-data</c> is given.
/// </summary>
public static class ServiceSetup
{
    private const string FirewallRule = "SqlLookout";
    private const string ExeName = "sqllookout.exe";
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WebTimeout = TimeSpan.FromSeconds(45);

    public static IReadOnlyList<string> CreateArgs(string imagePath, SetupOptions o, string? password)
    {
        var args = new List<string> { "create", ServiceCommandLine.ServiceName, "binPath=", imagePath, "start=", "delayed-auto", "DisplayName=", "SqlLookout" };
        if (o.Account is not null)
        {
            args.Add("obj="); args.Add(o.Account);
            if (password is not null) { args.Add("password="); args.Add(password); }
        }
        return args;
    }

    /// <summary>Administrators and SYSTEM (by well-known id, so it works on any Windows language) and the service account only.</summary>
    public static IReadOnlyList<string> AclArgs(string dataDir, string? account)
    {
        var args = new List<string> { dataDir, "/inheritance:r", "/grant:r", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F" };
        if (account is not null) args.Add($"{account}:(OI)(CI)M");
        args.Add("/T"); args.Add("/C"); args.Add("/Q");
        return args;
    }

    public static IReadOnlyList<string> FirewallAddArgs(int port) =>
        ["advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", "protocol=TCP", $"localport={port}"];

    public static async Task<int> InstallAsync(SetupOptions o, string sourceDir, ISetupHost host, TextWriter output, TextWriter error)
    {
        if (!host.IsAdministrator) return NotAdmin(error);
        if (host.ServiceExists())
        {
            error.WriteLine("The SqlLookout service is already installed. To move to a newer version run 'sqllookout upgrade' from the new folder; to start over run 'sqllookout uninstall' first.");
            return 1;
        }
        if (!File.Exists(Path.Combine(sourceDir, ExeName)))
        {
            error.WriteLine($"{ExeName} was not found in {sourceDir}. Run this command from the unzipped SqlLookout folder.");
            return 2;
        }

        var dir = o.Dir ?? Path.Combine(host.ProgramFilesDir, "SqlLookout");
        var dataDir = o.Data ?? Path.Combine(host.ProgramDataDir, "SqlLookout");
        var sameFolder = SamePath(dir, sourceDir);
        if (!sameFolder && Directory.Exists(dir) && File.Exists(Path.Combine(dir, ExeName)))
        {
            error.WriteLine($"{dir} already holds a program. Choose another --dir, or run 'sqllookout upgrade' if it is an installed copy.");
            return 2;
        }

        string? storeText = null, storeFile = null;
        if (o.StoreConnectionFile is not null)
        {
            try
            {
                storeText = StoreConnectionFile.Read(o.StoreConnectionFile, host.Protector);
            }
            catch (InvalidDataException ex)
            {
                error.WriteLine(ex.Message);
                return 2;
            }
            if (StoreConnectionFile.Validate(storeText) is { } storeProblem)
            {
                error.WriteLine(storeProblem);
                return 2;
            }
            if ((await host.CheckStoreAsync(storeText, create: true)).Failure is { } unusable)
            {
                error.WriteLine(unusable);
                return 1;
            }
            storeFile = Path.Combine(dataDir, "store.connection");
        }

        string? password = null;
        try
        {
            if (o.PasswordFile is not null) password = ReadFirstLine(o.PasswordFile);
            if (o.Account is not null && password is null && !o.Account.EndsWith('$'))
            {
                error.WriteLine($"{o.PasswordFile} is empty.");
                return 2;
            }

            Directory.CreateDirectory(dataDir);
            if (!Step(host, error, "set permissions on the data folder", "icacls", AclArgs(dataDir, o.Account))) return 1;
            // Written after the permissions are set, so the file inherits them; the service reads this file, not the one given here.
            if (storeFile is not null) StoreConnectionFile.WriteProtected(storeFile, storeText!, host.Protector);

            if (!sameFolder)
            {
                CopyTree(sourceDir, dir, path => IsTopLevel(sourceDir, path, "data"));
                output.WriteLine($"Program files copied to {dir}");
            }

            var imagePath = ServiceCommandLine.Build(Path.Combine(dir, ExeName), o, dataDir, storeFile);
            if (!Step(host, error, "create the service", "sc.exe", CreateArgs(imagePath, o, password))) return 1;
            // Restarting after a crash and the description are conveniences; the service works without them.
            host.Run("sc.exe", ["failure", ServiceCommandLine.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"]);
            host.Run("sc.exe", ["description", ServiceCommandLine.ServiceName, "Monitors SQL Server instances and sends alerts."]);

            var openFirewall = !o.NoFirewall && !(IPAddress.TryParse(o.Listen, out var ip) && IPAddress.IsLoopback(ip));
            if (openFirewall)
            {
                if (!Step(host, error, "open the firewall port", "netsh", FirewallAddArgs(o.Port))) return 1;
                output.WriteLine($"Firewall: inbound TCP {o.Port} allowed (rule '{FirewallRule}').");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine($"Install stopped: {Describe(ex)}");
            return 1;
        }

        try
        {
            host.StartService(ServiceTimeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine($"The service is installed but did not start: {ex.Message}");
            error.WriteLine($"To see the reason, run it in a console: {ServiceCommandLine.Quote(Path.Combine(dir, ExeName))} web --data {ServiceCommandLine.Quote(dataDir)} --port {o.Port}");
            if (o.Account is not null) error.WriteLine($"Also check that {o.Account} has the 'Log on as a service' right (Local Security Policy > Local Policies > User Rights Assignment).");
            return 1;
        }

        var address = IPAddress.TryParse(o.Listen, out var l) && !l.Equals(IPAddress.Any) && !l.Equals(IPAddress.IPv6Any) ? o.Listen : "127.0.0.1";
        if (!await host.WaitForWebAsync(address, o.Port, WebTimeout))
        {
            error.WriteLine($"The service is running but https://localhost:{o.Port}/ did not answer within {WebTimeout.TotalSeconds:0} seconds. Check the Windows Application event log.");
            return 1;
        }

        output.WriteLine($"Installed. Service 'SqlLookout' runs {(o.Account is null ? "as LocalSystem" : "as " + o.Account)}, starts automatically with Windows.");
        output.WriteLine($"Data folder: {dataDir} (readable by Administrators, SYSTEM{(o.Account is null ? "" : " and " + o.Account)} only)");
        if (storeFile is not null)
        {
            var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(storeText!);
            output.WriteLine($"History and settings are kept in SQL Server database '{target.InitialCatalog}' on {target.DataSource}. The connection is saved protected in {storeFile}; you can delete {o.StoreConnectionFile} if it holds a password.");
        }
        output.WriteLine($"Open https://localhost:{o.Port}/ ON THIS MACHINE to create the administrator account (the certificate is self-signed unless you passed --cert).");
        if (o.Account is null)
            output.WriteLine("Servers using Windows authentication see the computer account (DOMAIN\\MACHINE$). Reinstall with --account to use a domain account instead.");
        return 0;
    }

    public static async Task<int> UpgradeAsync(string sourceDir, ISetupHost host, TextWriter output, TextWriter error)
    {
        if (!host.IsAdministrator) return NotAdmin(error);
        var imagePath = host.ServiceExists() ? host.ServiceImagePath() : null;
        var installed = imagePath is null ? null : ServiceCommandLine.Read(imagePath);
        if (installed is null)
        {
            error.WriteLine("The SqlLookout service is not installed. Run 'sqllookout install' instead.");
            return 1;
        }

        var dir = Path.GetDirectoryName(installed.Value.Exe)!;
        var dataDir = installed.Value.DataDir;
        if (SamePath(dir, sourceDir))
        {
            error.WriteLine("Run 'sqllookout upgrade' from the NEW unzipped folder, not from the installed one.");
            return 2;
        }
        var newVersion = host.ProgramVersion(sourceDir);
        if (newVersion is null)
        {
            error.WriteLine($"{ExeName} was not found in {sourceDir}. Run this command from the unzipped SqlLookout folder.");
            return 2;
        }
        var oldVersion = host.ProgramVersion(dir);
        if (oldVersion is not null && newVersion < oldVersion)
        {
            error.WriteLine($"This folder holds version {newVersion}, older than the installed {oldVersion}. Nothing was changed.");
            return 2;
        }

        // A data folder inside the program folder (the old "data next to the program" layout) must survive the swap.
        var keep = TopLevelEntryHolding(dir, dataDir);
        var backup = dir.TrimEnd('\\', '/') + ".prev";

        try
        {
            host.StopService(ServiceTimeout);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            CopyTree(dir, backup, path => keep is not null && SamePath(path, keep));
            // The kept entry is not in the backup, so a rollback must not clear it either (ClearDir gets the same keep).
            output.WriteLine($"Old program files saved to {backup}");

            ClearDir(dir, keep);
            CopyTree(sourceDir, dir, path => IsTopLevel(sourceDir, path, "data"));
            host.StartService(ServiceTimeout);
            var address = installed.Value.Listen is "0.0.0.0" or "::" ? "127.0.0.1" : installed.Value.Listen;
            if (await host.WaitForWebAsync(address, installed.Value.Port, WebTimeout))
            {
                output.WriteLine($"Upgraded {oldVersion?.ToString() ?? "?"} -> {newVersion}. The data folder {dataDir} was not touched.");
                return 0;
            }
            error.WriteLine("The new version did not answer on its web port.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine($"Upgrade failed: {ex.Message}");
        }

        return RollBack(dir, keep, backup, host, error);
    }

    public static int Uninstall(SetupOptions o, ISetupHost host, TextWriter output, TextWriter error)
    {
        if (!host.IsAdministrator) return NotAdmin(error);
        var imagePath = host.ServiceExists() ? host.ServiceImagePath() : null;
        var installed = imagePath is null ? null : ServiceCommandLine.Read(imagePath);
        if (installed is null)
        {
            error.WriteLine("The SqlLookout service is not installed.");
            return 1;
        }

        try
        {
            host.StopService(ServiceTimeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine($"Could not stop the service: {ex.Message}");
            return 1;
        }
        if (!Step(host, error, "delete the service", "sc.exe", ["delete", ServiceCommandLine.ServiceName])) return 1;
        // The rule may not exist (installed with --no-firewall); that is fine.
        host.Run("netsh", ["advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}"]);
        output.WriteLine("Service and firewall rule removed.");
        if (installed.Value.StoreFile is not null)
            output.WriteLine("History and settings are in SQL Server; the tables there were not touched and stay until you drop the database.");

        if (o.DeleteData)
        {
            try
            {
                if (Directory.Exists(installed.Value.DataDir)) Directory.Delete(installed.Value.DataDir, recursive: true);
                output.WriteLine($"Data folder deleted: {installed.Value.DataDir}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Could not delete {installed.Value.DataDir}: {ex.Message}");
                return 1;
            }
        }
        else
        {
            output.WriteLine($"Data kept in {installed.Value.DataDir} (use --delete-data to remove it).");
        }
        output.WriteLine($"Program files are still in {Path.GetDirectoryName(installed.Value.Exe)}; delete that folder if you no longer need it.");
        return 0;
    }

    private static int RollBack(string dir, string? keep, string backup, ISetupHost host, TextWriter error)
    {
        error.WriteLine("Going back to the previous version.");
        try
        {
            try { host.StopService(ServiceTimeout); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception) { }
            ClearDir(dir, keep);
            CopyTree(backup, dir, _ => false);
            host.StartService(ServiceTimeout);
            error.WriteLine("The previous version is running again. Nothing was lost; the data folder was not touched.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine($"Could not restore automatically ({ex.Message}). The old program files are in {backup}: copy them back over {dir} and start the service.");
        }
        return 1;
    }

    /// <summary>The exception type and every message down the inner chain, so an empty message still says what failed.</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : $"{e.Message.Trim()} ({e.GetType().Name})");
        return string.Join(" -> ", parts);
    }

    private static bool Step(ISetupHost host, TextWriter error, string what, string file, IReadOnlyList<string> args)
    {
        var (code, text) = host.Run(file, args);
        if (code == 0) return true;
        error.WriteLine($"Could not {what} ({file} exit code {code}): {text.Trim()}");
        return false;
    }

    private static int NotAdmin(TextWriter error)
    {
        error.WriteLine("This needs administrator rights. Open a command prompt with 'Run as administrator' and run it again.");
        return 1;
    }

    private static string? ReadFirstLine(string file)
    {
        using var reader = new StreamReader(file);
        var line = reader.ReadLine();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsInside(string parent, string child)
    {
        var p = Normalize(parent) + Path.DirectorySeparatorChar;
        return Normalize(child).StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The entry directly under <paramref name="dir"/> that contains <paramref name="path"/>, or null when it lies outside.</summary>
    public static string? TopLevelEntryHolding(string dir, string path)
    {
        if (!IsInside(dir, path)) return null;
        var first = Path.GetRelativePath(dir, path).Split(Path.DirectorySeparatorChar)[0];
        return Path.Combine(Normalize(dir), first);
    }

    private static bool IsTopLevel(string root, string path, string name) =>
        SamePath(path, Path.Combine(root, name));

    /// <summary>Copies a folder tree; entries directly under the root for which <paramref name="skip"/> is true are left out.</summary>
    private static void CopyTree(string source, string target, Func<string, bool> skip)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if (skip(entry)) continue;
            var dest = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, dest, _ => false);
            else File.Copy(entry, dest, overwrite: true);
        }
    }

    /// <summary>Empties a folder except the one top-level entry <paramref name="keep"/>.</summary>
    private static void ClearDir(string dir, string? keep)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (keep is not null && SamePath(entry, keep)) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }
}
