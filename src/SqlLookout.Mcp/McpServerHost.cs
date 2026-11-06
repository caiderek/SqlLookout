using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlLookout.Storage;

namespace SqlLookout.Mcp;

public static class McpServerHost
{
    /// <summary>
    /// Serves the four read-only tools over stdio until the client disconnects. The history file is opened read-only,
    /// so the server cannot change what the monitor recorded even if a tool were wrong. stdout is the protocol channel:
    /// every log line goes to stderr.
    /// </summary>
    public static async Task RunAsync(string historyFile, CancellationToken ct = default)
    {
        await using var store = new SqliteHistoryStore(historyFile, readOnly: true);
        await store.InitializeAsync(ct);
        await RunAsync(store, ct);
    }

    /// <summary>Same server over a store the caller opened read-only (for example a SQL Server history database).</summary>
    public static async Task RunAsync(IHistoryStore store, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(new MonitorQueries(store));
        builder.Services.AddSingleton<IInstanceScopeSource, AllInstancesScopeSource>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<MonitorTools>();
        await builder.Build().RunAsync(ct);
    }
}
