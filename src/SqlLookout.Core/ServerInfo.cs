using Microsoft.Data.SqlClient;

namespace SqlLookout.Core;

/// <summary>What a monitored instance is. Parsed from SERVERPROPERTY values only, never from message text.</summary>
public sealed record ServerInfo(int Major, int Minor, int Build, int EngineEdition)
{
    /// <summary>EngineEdition 4 is Express (Azure SQL is 5/8 and not supported).</summary>
    public bool IsExpress => EngineEdition == 4;

    public bool IsAtLeast(int major, int minor = 0, int build = 0)
    {
        if (Major != major) return Major > major;
        if (Minor != minor) return Minor > minor;
        return Build >= build;
    }

    /// <summary>Parses SERVERPROPERTY('ProductVersion'), e.g. "10.50.6000.34". ProductMajorVersion does not exist before 2016, so it is not used.</summary>
    public static ServerInfo Parse(string productVersion, int engineEdition)
    {
        var parts = productVersion.Split('.');
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var build))
        {
            throw new FormatException($"Unrecognised ProductVersion '{productVersion}'.");
        }
        return new ServerInfo(major, minor, build, engineEdition);
    }

    public static async Task<ServerInfo> ReadAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('EngineEdition') AS int);";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Parse(reader.GetString(0), reader.GetInt32(1));
    }
}
