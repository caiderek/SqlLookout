using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlLookout.Core;

namespace SqlLookout.Service;

/// <summary>What a request that presented a valid key may see. <c>Instances</c> null means every instance.</summary>
public sealed record ApiKeyIdentity(long Id, string Name, IReadOnlyList<string>? Instances);

public sealed record ApiKeyRecord(
    long Id, string Name, DateTime CreatedUtc, DateTime ExpiresUtc, DateTime? RevokedUtc, DateTime? LastUsedUtc,
    IReadOnlyList<string>? Instances)
{
    public bool IsUsable(DateTime nowUtc) => RevokedUtc is null && ExpiresUtc > nowUtc;
}

/// <summary>
/// Keys for the remote MCP endpoint. Only the SHA-256 of a key is stored, so a copy of the data file cannot be used to
/// sign in; the key itself is shown once, when it is made. A key is lost if it is not copied then: make a new one.
/// </summary>
public sealed partial class SettingsStore
{
    public const string ApiKeyPrefix = "slk_";
    public const int MaxApiKeyDays = 365 * 2;
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    public static string HashApiKey(string rawKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    /// <summary>Makes a key and returns it. This is the only time the key itself exists outside the caller.</summary>
    public async Task<(long Id, string Key)> IssueApiKeyAsync(string name, DateTime expiresUtc, IReadOnlyList<string>? instances,
        DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
            throw new InvalidDataException(Loc.T("The key name must be 1 to 64 characters."));
        if (expiresUtc.ToUniversalTime() <= now)
            throw new InvalidDataException(Loc.T("The expiry date must be in the future."));
        if (expiresUtc.ToUniversalTime() > now.AddDays(MaxApiKeyDays))
            throw new InvalidDataException(Loc.T("A key can last at most {0} days.", MaxApiKeyDays));
        if (instances is { Count: 0 })
            throw new InvalidDataException(Loc.T("Pick at least one server for this key, or allow all servers."));

        var key = ApiKeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = _sqlServer
                ? "INSERT INTO ApiKeys (Name, KeyHash, CreatedUtc, ExpiresUtc, Instances) OUTPUT INSERTED.Id VALUES (@n, @h, @c, @e, @i);"
                : """
                  INSERT INTO ApiKeys (Name, KeyHash, CreatedUtc, ExpiresUtc, Instances) VALUES (@n, @h, @c, @e, @i);
                  SELECT last_insert_rowid();
                  """;
            cmd.AddParam("@n", name);
            cmd.AddParam("@h", HashApiKey(key));
            cmd.AddParam("@c", FormatTime(now));
            cmd.AddParam("@e", FormatTime(expiresUtc));
            cmd.AddParam("@i", instances is null ? DBNull.Value : JsonSerializer.Serialize(instances));
            return (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)), key);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The identity behind a key, or null when the key is unknown, revoked or expired (the caller must not say which).
    /// Records when the key was last used, at most once a minute so a busy client does not write on every call.
    /// </summary>
    public async Task<ApiKeyIdentity?> ValidateApiKeyAsync(string rawKey, DateTime? nowUtc = null, CancellationToken ct = default) =>
        (await TryValidateApiKeyAsync(rawKey, nowUtc, ct)).Identity;

    /// <summary>
    /// Like <see cref="ValidateApiKeyAsync"/>, and also says whether the key exists at all. An expired or revoked key is
    /// known: its owner is a client that needs a new key, not someone guessing, so the throttle must not count it.
    /// </summary>
    public async Task<(ApiKeyIdentity? Identity, bool Known)> TryValidateApiKeyAsync(string rawKey, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rawKey) || rawKey.Length > 200 || !rawKey.StartsWith(ApiKeyPrefix, StringComparison.Ordinal)) return (null, false);
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, ExpiresUtc, RevokedUtc, LastUsedUtc, Instances FROM ApiKeys WHERE KeyHash = @h;";
            cmd.AddParam("@h", HashApiKey(rawKey));
            ApiKeyRecord? found;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) return (null, false);
                try { found = ReadApiKey(reader); }
                catch (Exception ex) when (ex is FormatException or JsonException) { return (null, true); }   // a damaged row refuses the key instead of failing the request
            }
            if (!found.IsUsable(now)) return (null, true);

            if (found.LastUsedUtc is null || now - found.LastUsedUtc.Value >= LastUsedGranularity)
            {
                await using var touch = _connection.CreateCommand();
                touch.CommandText = "UPDATE ApiKeys SET LastUsedUtc = @t WHERE Id = @id;";
                touch.AddParam("@t", FormatTime(now));
                touch.AddParam("@id", found.Id);
                await touch.ExecuteNonQueryAsync(ct);
            }
            return (new ApiKeyIdentity(found.Id, found.Name, found.Instances), true);
        }
        finally { _gate.Release(); }
    }

    /// <summary>True when at least one key could still sign in. With none, the MCP endpoint behaves as if it did not exist.</summary>
    public async Task<bool> HasUsableApiKeyAsync(DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ApiKeys WHERE RevokedUtc IS NULL AND ExpiresUtc > @now;";
            cmd.AddParam("@now", FormatTime(now));
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListApiKeysAsync(CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, ExpiresUtc, RevokedUtc, LastUsedUtc, Instances, CreatedUtc FROM ApiKeys ORDER BY Id DESC;";
            var list = new List<ApiKeyRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(ReadApiKey(reader));
            return list;
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeApiKeyAsync(long id, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        await EnterAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE ApiKeys SET RevokedUtc = @t WHERE Id = @id AND RevokedUtc IS NULL;";
            cmd.AddParam("@t", FormatTime(nowUtc ?? DateTime.UtcNow));
            cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // Column order for ReadApiKey: Id, Name, ExpiresUtc, RevokedUtc, LastUsedUtc, Instances, [CreatedUtc].
    private static ApiKeyRecord ReadApiKey(DbDataReader r) => new(
        r.GetInt64(0), r.GetString(1),
        r.FieldCount > 6 ? ParseTime(r.GetString(6)) : DateTime.MinValue,
        ParseTime(r.GetString(2)),
        r.IsDBNull(3) ? null : ParseTime(r.GetString(3)),
        r.IsDBNull(4) ? null : ParseTime(r.GetString(4)),
        r.IsDBNull(5) ? null : JsonSerializer.Deserialize<List<string>>(r.GetString(5)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
