using Microsoft.Data.SqlClient;
using SqlLookout.Core;

namespace SqlLookout.Service;

/// <summary>
/// A short-lived connection to a monitored server for something read on demand (the Queries tab, the MCP live tools).
/// Errors become a sentence that never repeats text from the exception, which can contain parts of the connection string.
/// </summary>
public static class LiveConnection
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>Runs <paramref name="read"/> on a fresh connection. Returns the failure sentence instead of throwing for server-side problems.</summary>
    public static async Task<(T? Value, string? Error)> RunAsync<T>(SettingsStore store, string instance,
        Func<SqlConnection, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var connectionString = await store.GetConnectionStringAsync(instance, ct);
        if (connectionString is null) return (default, Loc.T("Could not connect."));
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 8, ConnectRetryCount = 0, ApplicationName = "SqlLookout" };
            await using var connection = new SqlConnection(builder.ConnectionString);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Limit);
            await connection.OpenAsync(limit.Token);
            return (await read(connection, limit.Token), null);
        }
        catch (SqlException ex)
        {
            // Only the number is shown: the text can repeat parts of the connection string.
            var hint = ex.Number is 229 or 300 or 297 ? " " + Loc.T("Needs VIEW SERVER STATE. Run sql/create-readonly-login.sql on this server.") : "";
            return (default, Loc.T("Could not read the queries (SQL error {0}).", ex.Number) + hint);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (default, Loc.T("The server did not answer in time."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or System.Data.SqlTypes.SqlNullValueException)
        {
            return (default, Loc.T("Could not read the queries."));
        }
    }
}
