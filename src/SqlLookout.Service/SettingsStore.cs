using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using SqlLookout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using SqlLookout.Alerts;

namespace SqlLookout.Service;

public enum LoginResult
{
    Ok,
    Invalid,
    Locked,
}

public static class AuthModes
{
    public const string Windows = "windows";
    public const string Sql = "sql";
}

public static class EncryptModes
{
    /// <summary>Encrypt and accept the server's certificate. Works with the self-signed certificates most SQL Servers have.</summary>
    public const string Trust = "trust";
    /// <summary>Encrypt and verify the certificate (needs a certificate the monitoring machine trusts).</summary>
    public const string Verify = "verify";
    /// <summary>No encryption. For old servers (2008 / 2008 R2) that cannot negotiate modern TLS.</summary>
    public const string Off = "off";
}

/// <summary>A monitored instance as the web UI edits it. The password is never part of this record.</summary>
public sealed record InstanceRecord(string Name, string Server, string Auth, string? UserName, string Encrypt, bool Enabled, bool HasPassword, string IgnoredDatabases = "", string ThresholdOverrides = "")
{
    public string BuildConnectionString(string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = "master",
            ApplicationName = "SqlLookout",
        };
        if (Auth == AuthModes.Windows)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = UserName ?? "";
            b.Password = password ?? "";
        }

        switch (Encrypt)
        {
            case EncryptModes.Off: b.Encrypt = SqlConnectionEncryptOption.Optional; break;
            case EncryptModes.Verify: b.Encrypt = SqlConnectionEncryptOption.Mandatory; break;
            default: b.Encrypt = SqlConnectionEncryptOption.Mandatory; b.TrustServerCertificate = true; break;
        }
        return b.ConnectionString;
    }
}

public sealed class GlobalSettings
{
    public int RetentionDays { get; set; } = 90;
    public string Language { get; set; } = Loc.English;
    public string Look { get; set; } = Looks.Classic;
    public List<string> DisabledCollectors { get; set; } = [];
    public Dictionary<string, int> IntervalsSeconds { get; set; } = [];
    /// <summary>Writes warnings, errors and a few page events to a log file in the data folder, for reporting problems.</summary>
    public bool DiagnosticLog { get; set; }
}

/// <summary>
/// Everything the web UI configures, in the same SQLite file as the history. Secrets (SQL passwords, the SMTP
/// password) are encrypted with <see cref="ISecretProtector"/> before they are written; login passwords are hashed.
/// </summary>
public sealed partial class SettingsStore : IAsyncDisposable
{
    public const int MinPasswordLength = 10;
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(5);
    private const string AlertKey = "alerts";
    private const string GlobalKey = "global";
    private const string SmtpPasswordKey = "smtp.password";
    private const string TeamsUrlKey = "teams.webhook";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private DbConnection _connection;
    private readonly string? _sqlServerConnectionString;
    private readonly bool _sqlServer;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(string dataSource, ISecretProtector protector)
        : this(new SqliteConnection($"Data Source={dataSource}"), sqlServer: false, protector, null)
    {
    }

    private SettingsStore(DbConnection connection, bool sqlServer, ISecretProtector protector, string? sqlServerConnectionString)
    {
        _sqlServerConnectionString = sqlServerConnectionString;
        _connection = connection;
        _sqlServer = sqlServer;
        _protector = protector;
    }

    /// <summary>Accounts and settings in a SQL Server database instead of the SQLite file.</summary>
    public static SettingsStore ForSqlServer(string connectionString, ISecretProtector protector) =>
        new(new SqlConnection(connectionString), sqlServer: true, protector, connectionString);

    /// <summary>
    /// Takes the gate. A SQL Server connection that the server or the network closed still looks open until it is used,
    /// and the pool cannot tell either; a one-row query finds out, and a broken connection is replaced.
    /// </summary>
    private async Task EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_sqlServer)
            {
                if (_connection.State != ConnectionState.Open) await _connection.OpenAsync(ct);
                return;
            }
            if (_connection.State == ConnectionState.Open)
            {
                try
                {
                    await using var ping = _connection.CreateCommand();
                    ping.CommandText = "SELECT 1;";
                    await ping.ExecuteScalarAsync(ct);
                    return;
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                }
            }
            // A broken connection object is not reused: it is dropped, together with the pool's dead siblings, and a new one is opened.
            try
            {
                SqlConnection.ClearPool((SqlConnection)_connection);
                await _connection.DisposeAsync();
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
            }
            _connection = new SqlConnection(_sqlServerConnectionString);
            await _connection.OpenAsync(ct);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private const string SqlServerSchema = """
        IF OBJECT_ID(N'Users', N'U') IS NULL
        CREATE TABLE Users (
            UserName       NVARCHAR(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL PRIMARY KEY,
            Hash           NVARCHAR(MAX) NOT NULL,
            Salt           NVARCHAR(MAX) NOT NULL,
            Iterations     INT NOT NULL,
            FailedCount    INT NOT NULL DEFAULT 0,
            LockedUntilUtc NVARCHAR(24) NULL,
            SessionStamp   NVARCHAR(64) NULL
        );
        IF OBJECT_ID(N'Instances', N'U') IS NULL
        CREATE TABLE Instances (
            Name               NVARCHAR(128) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL PRIMARY KEY,
            Server             NVARCHAR(512) NOT NULL,
            Auth               NVARCHAR(32) NOT NULL,
            UserName           NVARCHAR(256) NULL,
            PasswordProtected  NVARCHAR(MAX) NULL,
            Encrypt            NVARCHAR(32) NOT NULL,
            Enabled            INT NOT NULL DEFAULT 1,
            IgnoredDatabases   NVARCHAR(MAX) NULL,
            ThresholdOverrides NVARCHAR(MAX) NULL
        );
        IF OBJECT_ID(N'Settings', N'U') IS NULL
        CREATE TABLE Settings (
            [Key]   NVARCHAR(256) NOT NULL PRIMARY KEY,
            [Value] NVARCHAR(MAX) NOT NULL
        );
        IF OBJECT_ID(N'ApiKeys', N'U') IS NULL
        CREATE TABLE ApiKeys (
            Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            Name        NVARCHAR(128) NOT NULL,
            KeyHash     NVARCHAR(128) NOT NULL UNIQUE,
            CreatedUtc  NVARCHAR(24) NOT NULL,
            ExpiresUtc  NVARCHAR(24) NOT NULL,
            RevokedUtc  NVARCHAR(24) NULL,
            LastUsedUtc NVARCHAR(24) NULL,
            Instances   NVARCHAR(MAX) NULL
        );
        """;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _connection.OpenAsync(ct);
        if (_sqlServer)
        {
            await ExecuteAsync(SqlServerSchema, ct);
            return;
        }
        await ExecuteAsync("PRAGMA journal_mode=WAL;", ct);
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS Users (
                UserName       TEXT PRIMARY KEY COLLATE NOCASE,
                Hash           TEXT NOT NULL,
                Salt           TEXT NOT NULL,
                Iterations     INTEGER NOT NULL,
                FailedCount    INTEGER NOT NULL DEFAULT 0,
                LockedUntilUtc TEXT NULL,
                SessionStamp   TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS Instances (
                Name              TEXT PRIMARY KEY COLLATE NOCASE,
                Server            TEXT NOT NULL,
                Auth              TEXT NOT NULL,
                UserName          TEXT NULL,
                PasswordProtected TEXT NULL,
                Encrypt           TEXT NOT NULL,
                Enabled           INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS Settings (
                [Key] TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ApiKeys (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Name        TEXT NOT NULL,
                KeyHash     TEXT NOT NULL UNIQUE,
                CreatedUtc  TEXT NOT NULL,
                ExpiresUtc  TEXT NOT NULL,
                RevokedUtc  TEXT NULL,
                LastUsedUtc TEXT NULL,
                Instances   TEXT NULL
            );
            """, ct);

        // Data files made by an earlier build have no session stamp column.
        await using var info = _connection.CreateCommand();
        info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name = 'SessionStamp';";
        if (Convert.ToInt64(await info.ExecuteScalarAsync(ct)) == 0)
            await ExecuteAsync("ALTER TABLE Users ADD COLUMN SessionStamp TEXT NULL;", ct);

        info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Instances') WHERE name = 'IgnoredDatabases';";
        if (Convert.ToInt64(await info.ExecuteScalarAsync(ct)) == 0)
            await ExecuteAsync("ALTER TABLE Instances ADD COLUMN IgnoredDatabases TEXT NULL;", ct);

        info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Instances') WHERE name = 'ThresholdOverrides';";
        if (Convert.ToInt64(await info.ExecuteScalarAsync(ct)) == 0)
            await ExecuteAsync("ALTER TABLE Instances ADD COLUMN ThresholdOverrides TEXT NULL;", ct);
    }

    /// <summary>
    /// A random value kept with the account and copied into the sign-in cookie. Changing it (on sign-out and on a
    /// password change) makes every cookie issued before that moment worthless, even if someone copied it.
    /// </summary>
    public async Task<string?> GetSessionStampAsync(string userName, CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SessionStamp, '') FROM Users WHERE UserName = @u;";
            cmd.AddParam("@u", userName.Trim());
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> RotateSessionStampAsync(string userName, CancellationToken ct = default)
    {
        var stamp = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Users SET SessionStamp = @s WHERE UserName = @u;";
            cmd.AddParam("@s", stamp);
            cmd.AddParam("@u", userName.Trim());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
        return stamp;
    }

    // ---- login accounts ----

    public async Task<bool> HasAdminAsync(CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Users;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
        }
        finally { _gate.Release(); }
    }

    public async Task CreateAdminAsync(string userName, string password, CancellationToken ct = default)
    {
        ValidateUserName(userName);
        ValidateNewPassword(password);
        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        await EnterAsync(ct);
        try
        {
            await using var check = _connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM Users;";
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) throw new InvalidOperationException(Loc.T("An administrator already exists."));

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Users (UserName, Hash, Salt, Iterations) VALUES (@u, @h, @s, @i);";
            cmd.AddParam("@u", userName.Trim());
            cmd.AddParam("@h", hash);
            cmd.AddParam("@s", salt);
            cmd.AddParam("@i", iterations);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<LoginResult> VerifyLoginAsync(string userName, string password, DateTime nowUtc, CancellationToken ct = default)
    {
        string? hash = null, salt = null;
        var iterations = 0;
        DateTime? lockedUntil = null;

        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Hash, Salt, Iterations, LockedUntilUtc FROM Users WHERE UserName = @u;";
            cmd.AddParam("@u", userName.Trim());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                hash = reader.GetString(0);
                salt = reader.GetString(1);
                iterations = reader.GetInt32(2);
                if (!reader.IsDBNull(3)) lockedUntil = ParseTime(reader.GetString(3));
            }
        }
        finally { _gate.Release(); }

        if (hash is null)
        {
            PasswordHasher.Decoy(password);
            return LoginResult.Invalid;
        }
        if (lockedUntil is { } until && until > nowUtc) return LoginResult.Locked;

        var ok = PasswordHasher.Verify(password, hash, salt!, iterations);

        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            if (ok)
            {
                cmd.CommandText = "UPDATE Users SET FailedCount = 0, LockedUntilUtc = NULL WHERE UserName = @u;";
            }
            else
            {
                // Count the failure and lock the account once the limit is reached.
                cmd.CommandText = """
                    UPDATE Users SET
                        FailedCount = CASE WHEN FailedCount + 1 >= @max THEN 0 ELSE FailedCount + 1 END,
                        LockedUntilUtc = CASE WHEN FailedCount + 1 >= @max THEN @until ELSE LockedUntilUtc END
                    WHERE UserName = @u;
                    """;
                cmd.AddParam("@max", MaxFailedLogins);
                cmd.AddParam("@until", FormatTime(nowUtc + LockoutTime));
            }
            cmd.AddParam("@u", userName.Trim());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }

        return ok ? LoginResult.Ok : LoginResult.Invalid;
    }

    public async Task ChangePasswordAsync(string userName, string currentPassword, string newPassword, DateTime nowUtc, CancellationToken ct = default)
    {
        ValidateNewPassword(newPassword);
        if (await VerifyLoginAsync(userName, currentPassword, nowUtc, ct) != LoginResult.Ok)
            throw new InvalidDataException(Loc.T("The current password is not correct."));

        var (hash, salt, iterations) = PasswordHasher.Hash(newPassword);
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Users SET Hash = @h, Salt = @s, Iterations = @i WHERE UserName = @u;";
            cmd.AddParam("@h", hash);
            cmd.AddParam("@s", salt);
            cmd.AddParam("@i", iterations);
            cmd.AddParam("@u", userName.Trim());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Names of all sign-in accounts, for the command line that resets a forgotten password.</summary>
    public async Task<IReadOnlyList<string>> GetUserNamesAsync(CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT UserName FROM Users ORDER BY UserName;";
            var names = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
            return names;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sets a new password without knowing the old one. Only the command line on the machine itself calls this (whoever can
    /// open the data folder can do anything anyway). The lockout is lifted and every signed-in browser is signed out.
    /// Returns false when there is no such account.
    /// </summary>
    public async Task<bool> ResetPasswordAsync(string userName, string newPassword, CancellationToken ct = default)
    {
        ValidateNewPassword(newPassword);
        var (hash, salt, iterations) = PasswordHasher.Hash(newPassword);
        var stamp = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Users SET Hash = @h, Salt = @s, Iterations = @i, FailedCount = 0, LockedUntilUtc = NULL, SessionStamp = @stamp WHERE UserName = @u;";
            cmd.AddParam("@h", hash);
            cmd.AddParam("@s", salt);
            cmd.AddParam("@i", iterations);
            cmd.AddParam("@stamp", stamp);
            cmd.AddParam("@u", userName.Trim());
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        finally { _gate.Release(); }
    }

    // ---- instances ----

    public async Task<IReadOnlyList<InstanceRecord>> GetInstancesAsync(CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Name, Server, Auth, UserName, Encrypt, Enabled, CASE WHEN PasswordProtected IS NOT NULL THEN 1 ELSE 0 END, IgnoredDatabases, ThresholdOverrides FROM Instances ORDER BY Name;";
            var list = new List<InstanceRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(ReadInstance(reader, 7, 8));
            return list;
        }
        finally { _gate.Release(); }
    }

    public async Task<InstanceRecord?> GetInstanceAsync(string name, CancellationToken ct = default) =>
        (await GetInstancesAsync(ct)).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds an instance, or updates it when it exists. A blank password on an existing SQL-login instance keeps the saved one.
    /// Messages of the exceptions thrown here are written for the person using the form.
    /// </summary>
    public async Task SaveInstanceAsync(InstanceRecord instance, string? password, bool isNew, CancellationToken ct = default)
    {
        var name = instance.Name.Trim();
        // The name appears in web addresses and as the key of the stored history, so it stays plain: letters (any language), digits, space . _ -
        if (name.Length is 0 or > 64 || name.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-')))
            throw new InvalidDataException(Loc.T("The name must be 1 to 64 characters: letters, digits, spaces, dots, dashes or underscores."));
        if (string.IsNullOrWhiteSpace(instance.Server) || instance.Server.Length > 256) throw new InvalidDataException(Loc.T("Enter the server name (for example PROD01 or PROD01\\SQL2008 or 10.0.0.5,1433)."));
        if (instance.Auth is not (AuthModes.Windows or AuthModes.Sql)) throw new InvalidDataException(Loc.T("Choose an authentication type."));
        if (instance.Encrypt is not (EncryptModes.Trust or EncryptModes.Verify or EncryptModes.Off)) throw new InvalidDataException(Loc.T("Choose an encryption option."));
        if (instance.Auth == AuthModes.Sql && string.IsNullOrWhiteSpace(instance.UserName)) throw new InvalidDataException(Loc.T("Enter the SQL login name."));

        var ignored = string.Join('\n', DatabaseFilter.Parse(instance.IgnoredDatabases));
        // Parsed and checked against the common thresholds now, so a bad value is refused here and not found by the monitor later.
        var overrides = AlertThresholds.ParseOverrides(instance.ThresholdOverrides);
        (await GetAlertSettingsAsync(ct)).Thresholds.With(overrides);
        var overridesText = AlertThresholds.FormatOverrides(overrides);

        await EnterAsync(ct);
        try
        {
            await using var exists = _connection.CreateCommand();
            exists.CommandText = "SELECT PasswordProtected FROM Instances WHERE Name = @n;";
            exists.AddParam("@n", name);
            var existing = await exists.ExecuteScalarAsync(ct);
            var found = existing is not null;
            if (isNew && found) throw new InvalidDataException(Loc.T("An instance named '{0}' already exists.", name));
            if (!isNew && !found) throw new InvalidDataException(Loc.T("There is no instance named '{0}'.", name));

            string? protectedPassword = null;
            if (instance.Auth == AuthModes.Sql)
            {
                if (!string.IsNullOrEmpty(password)) protectedPassword = _protector.Protect(password);
                else if (existing is string old) protectedPassword = old;
                else throw new InvalidDataException(Loc.T("Enter the SQL login password."));
            }

            await using var cmd = _connection.CreateCommand();
            // SQL Server has no ON CONFLICT: update first, insert when no row matched.
            cmd.CommandText = _sqlServer
                ? """
                  UPDATE Instances SET
                      Server = @s, Auth = @a, UserName = @u, PasswordProtected = @p, Encrypt = @e, Enabled = @en,
                      IgnoredDatabases = @ig, ThresholdOverrides = @to
                  WHERE Name = @n;
                  IF @@ROWCOUNT = 0
                      INSERT INTO Instances (Name, Server, Auth, UserName, PasswordProtected, Encrypt, Enabled, IgnoredDatabases, ThresholdOverrides)
                      VALUES (@n, @s, @a, @u, @p, @e, @en, @ig, @to);
                  """
                : """
                  INSERT INTO Instances (Name, Server, Auth, UserName, PasswordProtected, Encrypt, Enabled, IgnoredDatabases, ThresholdOverrides)
                  VALUES (@n, @s, @a, @u, @p, @e, @en, @ig, @to)
                  ON CONFLICT(Name) DO UPDATE SET
                      Server = excluded.Server, Auth = excluded.Auth, UserName = excluded.UserName,
                      PasswordProtected = excluded.PasswordProtected, Encrypt = excluded.Encrypt, Enabled = excluded.Enabled,
                      IgnoredDatabases = excluded.IgnoredDatabases, ThresholdOverrides = excluded.ThresholdOverrides;
                  """;
            cmd.AddParam("@n", name);
            cmd.AddParam("@s", instance.Server.Trim());
            cmd.AddParam("@a", instance.Auth);
            cmd.AddParam("@u", instance.Auth == AuthModes.Sql ? instance.UserName!.Trim() : DBNull.Value);
            cmd.AddParam("@p", (object?)protectedPassword ?? DBNull.Value);
            cmd.AddParam("@e", instance.Encrypt);
            cmd.AddParam("@en", instance.Enabled ? 1 : 0);
            cmd.AddParam("@ig", ignored);
            cmd.AddParam("@to", overridesText);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteInstanceAsync(string name, CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Instances WHERE Name = @n;";
            cmd.AddParam("@n", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The connection string for an instance, with its password decrypted. Only for connecting, never for display.</summary>
    public async Task<string?> GetConnectionStringAsync(string name, CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Name, Server, Auth, UserName, Encrypt, Enabled, CASE WHEN PasswordProtected IS NOT NULL THEN 1 ELSE 0 END, PasswordProtected FROM Instances WHERE Name = @n;";
            cmd.AddParam("@n", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var record = ReadInstance(reader);
            var password = reader.IsDBNull(7) ? null : _protector.Unprotect(reader.GetString(7));
            return record.BuildConnectionString(password);
        }
        finally { _gate.Release(); }
    }

    // ---- alert and global settings ----

    public async Task<AlertSettings> GetAlertSettingsAsync(CancellationToken ct = default)
    {
        var json = await GetValueAsync(AlertKey, ct);
        return json is null ? new AlertSettings() : JsonSerializer.Deserialize<AlertSettings>(json, Json) ?? new AlertSettings();
    }

    public async Task<bool> HasSmtpPasswordAsync(CancellationToken ct = default) => await GetValueAsync(SmtpPasswordKey, ct) is not null;

    public async Task<bool> HasTeamsUrlAsync(CancellationToken ct = default) => await GetValueAsync(TeamsUrlKey, ct) is not null;

    /// <summary>Saves the alert settings. A null password keeps the saved SMTP password; clearPassword removes it.</summary>
    public async Task SaveAlertSettingsAsync(AlertSettings settings, string? newSmtpPassword, bool clearSmtpPassword, CancellationToken ct = default,
        string? newTeamsUrl = null, bool clearTeamsUrl = false)
    {
        settings.Validate();
        // A server's own thresholds are laid over these; refuse a change that would make one of them stop fitting (it would silently fall back).
        foreach (var record in await GetInstancesAsync(ct))
        {
            try { settings.Thresholds.With(AlertThresholds.ParseOverrides(record.ThresholdOverrides)); }
            catch (InvalidDataException e) { throw new InvalidDataException(Loc.T("The thresholds of server '{0}' no longer fit these values: {1}", record.Name, e.Message)); }
        }
        if (!string.IsNullOrEmpty(newTeamsUrl) &&!TeamsSettings.IsAcceptableUrl(newTeamsUrl))
            throw new InvalidDataException(Loc.T("The Teams address must start with https://."));
        if (settings.Teams is not null && string.IsNullOrEmpty(newTeamsUrl) && (clearTeamsUrl || await GetValueAsync(TeamsUrlKey, ct) is null)
            && settings.Teams.UrlFile is null && settings.Teams.UrlEnv is null)
            throw new InvalidDataException(Loc.T("Teams needs the webhook address."));
        await SetValueAsync(AlertKey, JsonSerializer.Serialize(settings, Json), ct);
        if (clearSmtpPassword) await DeleteValueAsync(SmtpPasswordKey, ct);
        else if (!string.IsNullOrEmpty(newSmtpPassword)) await SetValueAsync(SmtpPasswordKey, _protector.Protect(newSmtpPassword), ct);
        if (!string.IsNullOrEmpty(newTeamsUrl)) await SetValueAsync(TeamsUrlKey, _protector.Protect(newTeamsUrl), ct);
        else if (clearTeamsUrl) await DeleteValueAsync(TeamsUrlKey, ct);
    }

    /// <summary>Validates everything first and writes only if all of it is valid, so a bad field never leaves a half-saved page.</summary>
    public async Task SaveSettingsAsync(AlertSettings alerts, GlobalSettings global, string? newSmtpPassword, bool clearSmtpPassword, CancellationToken ct = default,
        string? newTeamsUrl = null, bool clearTeamsUrl = false)
    {
        alerts.Validate();
        ValidateGlobal(global);
        await SaveAlertSettingsAsync(alerts, newSmtpPassword, clearSmtpPassword, ct, newTeamsUrl, clearTeamsUrl);
        await SaveGlobalAsync(global, ct);
    }

    private static void ValidateGlobal(GlobalSettings settings)
    {
        if (!Loc.IsSupported(settings.Language)) throw new InvalidDataException(Loc.T("Choose a language from the list."));
        if (!Looks.IsSupported(settings.Look)) throw new InvalidDataException(Loc.T("Choose a look from the list."));
        if (settings.RetentionDays is < 1 or > 3650) throw new InvalidDataException(Loc.T("Keep history for 1 to 3650 days."));
    }

    public async Task<GlobalSettings> GetGlobalAsync(CancellationToken ct = default)
    {
        var json = await GetValueAsync(GlobalKey, ct);
        return json is null ? new GlobalSettings() : JsonSerializer.Deserialize<GlobalSettings>(json, Json) ?? new GlobalSettings();
    }

    public async Task SaveGlobalAsync(GlobalSettings settings, CancellationToken ct = default)
    {
        ValidateGlobal(settings);
        await SetValueAsync(GlobalKey, JsonSerializer.Serialize(settings, Json), ct);
    }

    /// <summary>Builds the configuration the monitor runs on, with secrets decrypted. Held in memory only.</summary>
    public async Task<SqlLookoutConfig> BuildConfigAsync(CancellationToken ct = default)
    {
        var global = await GetGlobalAsync(ct);
        var alerts = await GetAlertSettingsAsync(ct);
        if (alerts.Email is not null && await GetValueAsync(SmtpPasswordKey, ct) is { } smtp)
            alerts.Email.Password = _protector.Unprotect(smtp);
        if (alerts.Teams is not null && await GetValueAsync(TeamsUrlKey, ct) is { } teams)
            alerts.Teams.Url = _protector.Unprotect(teams);

        var config = new SqlLookoutConfig
        {
            RetentionDays = global.RetentionDays,
            Language = global.Language,
            Look = global.Look,
            DisabledCollectors = global.DisabledCollectors,
            IntervalsSeconds = global.IntervalsSeconds,
            Alerts = alerts,
        };
        foreach (var record in await GetInstancesAsync(ct))
        {
            config.Instances.Add(new InstanceConfig
            {
                Name = record.Name,
                Enabled = record.Enabled,
                IgnoredDatabases = DatabaseFilter.Parse(record.IgnoredDatabases),
                ThresholdOverrides = AlertThresholds.ParseOverrides(record.ThresholdOverrides),
                ResolvedConnectionString = await GetConnectionStringAsync(record.Name, ct),
            });
        }
        return config;
    }

    // ---- helpers ----

    private static InstanceRecord ReadInstance(DbDataReader r, int ignoredColumn = -1, int overridesColumn = -1) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt32(5) != 0, r.GetInt32(6) != 0,
            ignoredColumn >= 0 && !r.IsDBNull(ignoredColumn) ? r.GetString(ignoredColumn) : "",
            overridesColumn >= 0 && !r.IsDBNull(overridesColumn) ? r.GetString(overridesColumn) : "");

    private async Task<string?> GetValueAsync(string key, CancellationToken ct)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT [Value] FROM Settings WHERE [Key] = @k;";
            cmd.AddParam("@k", key);
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task SetValueAsync(string key, string value, CancellationToken ct)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = _sqlServer
                ? "UPDATE Settings SET [Value] = @v WHERE [Key] = @k; IF @@ROWCOUNT = 0 INSERT INTO Settings ([Key], [Value]) VALUES (@k, @v);"
                : "INSERT INTO Settings ([Key], Value) VALUES (@k, @v) ON CONFLICT([Key]) DO UPDATE SET Value = excluded.Value;";
            cmd.AddParam("@k", key);
            cmd.AddParam("@v", value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task DeleteValueAsync(string key, CancellationToken ct)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Settings WHERE [Key] = @k;";
            cmd.AddParam("@k", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateUserName(string userName)
    {
        var name = userName.Trim();
        if (name.Length is < 3 or > 64 || name.Any(char.IsControl)) throw new InvalidDataException(Loc.T("The user name must be 3 to 64 characters."));
    }

    public static void ValidateNewPassword(string password)
    {
        if (password.Length < MinPasswordLength) throw new InvalidDataException(Loc.T("The password must be at least {0} characters.", MinPasswordLength));
    }

    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    private static string FormatTime(DateTime utc) => utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
    private static DateTime ParseTime(string text) =>
        DateTime.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
    }
}

/// <summary>The configuration the running monitor reads. The web UI calls <see cref="RefreshAsync"/> after every save.</summary>
public sealed class LiveConfig(SettingsStore store)
{
    private volatile SqlLookoutConfig _current = new();

    public SqlLookoutConfig Current => _current;

    public async Task RefreshAsync(CancellationToken ct = default) => _current = await store.BuildConfigAsync(ct);
}

internal static class DbCommandExtensions
{
    /// <summary>Adds a parameter on any ADO.NET provider; a null value is sent as SQL NULL (SqlClient refuses a null value).</summary>
    public static DbParameter AddParam(this DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
