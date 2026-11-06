using Microsoft.Data.SqlClient;

namespace SqlLookout.Core;

public interface ICollector
{
    string Name { get; }
    TimeSpan DefaultInterval { get; }
    TimeSpan Timeout { get; }

    /// <summary>Null when the collector can run; otherwise the reason it cannot (version, edition). The runner turns this into Unsupported.</summary>
    string? UnsupportedReason(ServerInfo server);

    Task<CollectionResult> CollectAsync(SqlConnection connection, ServerInfo server, CancellationToken ct);
}
