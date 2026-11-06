using Microsoft.Data.SqlClient;

namespace SqlLookout.Core;

public static class CollectorRunner
{
    // 229 = permission denied on object, 262 = denied in database, 297 = user lacks permission, 300 = VIEW SERVER STATE style denial.
    private static readonly int[] PermissionErrors = [229, 262, 297, 300];

    public static async Task<CollectionResult> RunAsync(
        ICollector collector, SqlConnection connection, ServerInfo server, CancellationToken ct)
    {
        var reason = collector.UnsupportedReason(server);
        if (reason is not null) return CollectionResult.Unsupported(reason);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(collector.Timeout);
        try
        {
            return await collector.CollectAsync(connection, server, timeout.Token);
        }
        catch (SqlException ex) when (PermissionErrors.Contains(ex.Number))
        {
            return new CollectionResult(CollectionStatus.NoPermission, [], $"SQL error {ex.Number}");
        }
        catch (SqlException ex)
        {
            return new CollectionResult(CollectionStatus.Failed, [], $"SQL error {ex.Number}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CollectionResult(CollectionStatus.Failed, [], $"Timed out after {collector.Timeout.TotalSeconds:0}s");
        }
    }
}
