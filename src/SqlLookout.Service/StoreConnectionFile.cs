using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace SqlLookout.Service;

/// <summary>
/// The file that tells the program to keep its history and settings in SQL Server. It holds one connection string, either
/// as plain text (to type or paste) or protected with DPAPI behind a "dpapi:" prefix, which is what 'install' writes so
/// the password in it is not left lying around in the clear.
/// </summary>
public static class StoreConnectionFile
{
    private const string Prefix = "dpapi:";
    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    /// <summary>The connection string in the file. Throws <see cref="InvalidDataException"/> with a message for the person running the command.</summary>
    public static string Read(string path, ISecretProtector protector)
    {
        string text;
        try
        {
            text = File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot read the store connection file {path}: {ex.Message}");
        }
        if (text.Length == 0) throw new InvalidDataException($"The store connection file {path} is empty. Put the SQL Server connection string on its first line.");
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return text;
        try
        {
            return protector.Unprotect(text[Prefix.Length..]);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException($"The store connection file {path} was protected on another machine or by another program and cannot be read here. Write a new one with 'sqllookout install --store-connection-file'.");
        }
    }

    public static void WriteProtected(string path, string connectionString, ISecretProtector protector) =>
        File.WriteAllText(path, Prefix + protector.Protect(connectionString) + Environment.NewLine);

    /// <summary>Null when the connection string can be used as the store; otherwise what is wrong with it.</summary>
    public static string? Validate(string connectionString)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            return $"The connection string is not valid: {ex.Message}";
        }
        if (string.IsNullOrWhiteSpace(builder.DataSource)) return "The connection string has no server (Server=...).";
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            return "The connection string has no database (Database=...). Create a database for SqlLookout and name it here; its tables are created in the database the string names.";
        if (SystemDatabases.Contains(builder.InitialCatalog, StringComparer.OrdinalIgnoreCase))
            return $"Database '{builder.InitialCatalog}' is a SQL Server system database. Create a database of its own for SqlLookout.";
        return null;
    }
}
