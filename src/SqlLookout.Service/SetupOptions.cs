using System.Net;
using System.Text;

namespace SqlLookout.Service;

/// <summary>Options of <c>sqllookout install</c> and <c>sqllookout uninstall</c> after parsing and checking.</summary>
public sealed record SetupOptions
{
    public string? Dir { get; init; }
    public string? Data { get; init; }
    public int Port { get; init; } = 8443;
    public string Listen { get; init; } = "0.0.0.0";
    public string? Cert { get; init; }
    public string? CertPasswordFile { get; init; }
    public string? Account { get; init; }
    public string? PasswordFile { get; init; }
    public bool NoFirewall { get; init; }
    public bool DeleteData { get; init; }
    /// <summary>A file with the SQL Server connection string to keep history and settings in; null keeps the SQLite file.</summary>
    public string? StoreConnectionFile { get; init; }
    /// <summary>The command also writes what it prints to this file; the setup wizard reads it after an elevated run.</summary>
    public string? OutputFile { get; init; }
}

public static class SetupOptionsParser
{
    private static readonly string[] InstallValues = ["--dir", "--data", "--port", "--listen", "--cert", "--cert-password-file", "--account", "--password-file", "--store-connection-file", "--output-file"];
    private static readonly string[] InstallFlags = ["--no-firewall"];
    private static readonly string[] UninstallFlags = ["--delete-data", "--keep-data"];

    /// <param name="command">install, upgrade or uninstall.</param>
    /// <param name="args">The full command line; element 0 is the command itself.</param>
    public static (SetupOptions? Options, string? Error) Parse(string command, string[] args)
    {
        var values = command == "install" ? InstallValues : ["--output-file"];
        var flags = command switch { "install" => InstallFlags, "uninstall" => UninstallFlags, _ => [] };
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var setFlags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (flags.Contains(a)) { setFlags.Add(a); continue; }
            if (!values.Contains(a)) return (null, $"Unknown option '{a}'.");
            if (i + 1 >= args.Length) return (null, $"Option '{a}' needs a value.");
            found[a] = args[++i];
        }

        if (setFlags.Contains("--delete-data") && setFlags.Contains("--keep-data"))
            return (null, "Use either --delete-data or --keep-data, not both.");

        var port = 8443;
        if (found.TryGetValue("--port", out var portText) && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            return (null, "--port must be a number from 1 to 65535.");

        var listen = found.GetValueOrDefault("--listen") ?? "0.0.0.0";
        if (!IPAddress.TryParse(listen, out _))
            return (null, "--listen must be an IP address, for example 0.0.0.0 (all) or 127.0.0.1 (this machine only).");

        var account = found.GetValueOrDefault("--account");
        var passwordFile = found.GetValueOrDefault("--password-file");
        if (passwordFile is not null && account is null) return (null, "--password-file only makes sense together with --account.");
        // A managed service account (name ends with $) has no password; any other account needs one.
        if (account is not null && passwordFile is null && !account.EndsWith('$'))
            return (null, "--account needs --password-file <file with the password on its first line>, unless it is a managed service account (name ends with $).");

        if (found.ContainsKey("--cert-password-file") && !found.ContainsKey("--cert"))
            return (null, "--cert-password-file only makes sense together with --cert.");

        return (new SetupOptions
        {
            Dir = Full(found.GetValueOrDefault("--dir")),
            Data = Full(found.GetValueOrDefault("--data")),
            Port = port,
            Listen = listen,
            Cert = Full(found.GetValueOrDefault("--cert")),
            CertPasswordFile = Full(found.GetValueOrDefault("--cert-password-file")),
            Account = account,
            PasswordFile = Full(passwordFile),
            StoreConnectionFile = Full(found.GetValueOrDefault("--store-connection-file")),
            OutputFile = Full(found.GetValueOrDefault("--output-file")),
            NoFirewall = setFlags.Contains("--no-firewall"),
            DeleteData = setFlags.Contains("--delete-data"),
        }, null);
    }

    private static string? Full(string? path) => path is null ? null : Path.GetFullPath(path);
}

/// <summary>Builds and reads the service command line, the one place where quoting rules matter.</summary>
public static class ServiceCommandLine
{
    public const string ServiceName = "SqlLookout";

    public static string Quote(string value) => "\"" + (value.EndsWith('\\') ? value + "\\" : value) + "\"";

    /// <param name="storeFile">The protected store connection file the service reads at start, or null for the SQLite file.</param>
    public static string Build(string exe, SetupOptions o, string dataDir, string? storeFile = null)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(exe)).Append(" web --data ").Append(Quote(dataDir));
        if (storeFile is not null) sb.Append(" --store-connection-file ").Append(Quote(storeFile));
        sb.Append(" --port ").Append(o.Port).Append(" --listen ").Append(o.Listen);
        if (o.Cert is not null) sb.Append(" --cert ").Append(Quote(o.Cert));
        if (o.CertPasswordFile is not null) sb.Append(" --cert-password-file ").Append(Quote(o.CertPasswordFile));
        return sb.ToString();
    }

    /// <summary>Splits a command line the way Windows does: backslashes only count in front of a quote.</summary>
    public static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var run = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { run++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1) current.Append('"'); else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', run);
                    i--;
                }
                started = true;
            }
            else if (c == '"') { inQuotes = !inQuotes; started = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
            }
            else { current.Append(c); started = true; }
        }
        if (started) result.Add(current.ToString());
        return result;
    }

    /// <summary>What an installed service was set up with, read back from its command line.</summary>
    public static (string Exe, string DataDir, int Port, string Listen, string? StoreFile)? Read(string imagePath)
    {
        var parts = Split(imagePath);
        if (parts.Count == 0) return null;
        var exe = parts[0];
        string? data = null, listen = null, store = null;
        var port = 8443;
        for (var i = 1; i + 1 < parts.Count; i++)
        {
            switch (parts[i])
            {
                case "--data": data = parts[++i]; break;
                case "--listen": listen = parts[++i]; break;
                case "--store-connection-file": store = parts[++i]; break;
                case "--port": if (int.TryParse(parts[i + 1], out var p)) port = p; i++; break;
            }
        }
        var dir = Path.GetDirectoryName(exe) ?? "";
        var dataDir = data is null ? Path.Combine(dir, "data") : Path.GetFullPath(data, dir);
        return (exe, dataDir, port, listen ?? "0.0.0.0", store);
    }
}
