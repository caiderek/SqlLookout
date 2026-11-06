using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace SqlLookout.Service;

/// <summary>The real machine behind <see cref="ISetupHost"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSetupHost : ISetupHost
{
    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string ProgramFilesDir => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public string ProgramDataDir => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public bool ServiceExists() => ServiceController.GetServices().Any(s => s.ServiceName == ServiceCommandLine.ServiceName);

    public string? ServiceImagePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceCommandLine.ServiceName);
        return key?.GetValue("ImagePath") as string;
    }

    public void StopService(TimeSpan timeout)
    {
        using var service = new ServiceController(ServiceCommandLine.ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public void StartService(TimeSpan timeout)
    {
        using var service = new ServiceController(ServiceCommandLine.ServiceName);
        if (service.Status == ServiceControllerStatus.Running) return;
        if (service.Status != ServiceControllerStatus.StartPending) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public async Task<bool> WaitForWebAsync(string address, int port, TimeSpan timeout)
    {
        // The certificate is self-signed by default and the address is this machine, so it is not checked here.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Any answer, even a redirect to the sign-in page, means the web interface is up.
                using var response = await http.GetAsync($"https://{address}:{port}/");
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(1000);
            }
        }
        return false;
    }

    public ISecretProtector Protector { get; } = new DpapiSecretProtector();

    public async Task<StoreCheck> CheckStoreAsync(string connectionString, bool create)
    {
        try
        {
            // Connect to master: the store database may not exist yet, and that is not an error.
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 15 };
            var database = builder.InitialCatalog;
            builder.InitialCatalog = "master";
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT DB_ID(@db), HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'), HAS_PERMS_BY_NAME(@db, 'DATABASE', 'CREATE TABLE');";
            cmd.Parameters.AddWithValue("@db", database);
            bool exists, mayCreateDatabase, mayCreateTables;
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                exists = !reader.IsDBNull(0);
                mayCreateDatabase = !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1)) == 1;
                mayCreateTables = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) == 1;
            }

            if (exists)
                return mayCreateTables
                    ? new StoreCheck(null, false)
                    : new StoreCheck($"The login cannot create tables in database '{database}'. Give it the db_owner role there (sql/create-store-database.sql does this).", false);
            if (!mayCreateDatabase)
                return new StoreCheck($"Database '{database}' does not exist and the login may not create it. Create it first (sql/create-store-database.sql does this).", false);
            if (create)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = $"CREATE DATABASE [{database.Replace("]", "]]")}];";
                await cmd.ExecuteNonQueryAsync();
            }
            return new StoreCheck(null, true);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return new StoreCheck($"Cannot use the store database: {ex.Message}", false);
        }
    }

    public Version? ProgramVersion(string folder)
    {
        var exe = Path.Combine(folder, "sqllookout.exe");
        if (!File.Exists(exe)) return null;
        var text = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        if (text is null) return null;
        // The product version may carry a suffix such as "+commit"; only the numbers are compared.
        var end = text.IndexOfAny(['+', '-']);
        return Version.TryParse(end < 0 ? text : text[..end], out var v) ? v : null;
    }

    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
