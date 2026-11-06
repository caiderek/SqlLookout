using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Mcp;

/// <summary>
/// The MCP endpoint inside the web process: <c>POST /mcp</c> over HTTPS with <c>Authorization: Bearer slk_...</c>.
/// Without a usable key the path answers 404 as if it did not exist. Stateless, so no session lives on the server.
/// </summary>
public static class RemoteMcp
{
    public const string Path = "/mcp";
    internal const string ScopeItem = "sqllookout.mcp.scope";
    internal const string KeyNameItem = "sqllookout.mcp.key";

    public static void AddRemoteMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton(sp => new MonitorQueries(sp.GetRequiredService<IHistoryStore>()));
        services.AddSingleton<IInstanceScopeSource, HttpContextScopeSource>();
        services.AddSingleton<McpKeyThrottle>();
        services.AddSingleton<LiveToolGate>();
        services.AddMcpServer()
            .WithTools<MonitorTools>()
            .WithTools<LiveTools>()
            .WithHttpTransport(o => o.Stateless = true);
    }

    /// <summary>
    /// The endpoint is anonymous as far as the web sign-in goes: the bearer key is the only way in, and the cookie,
    /// the CSRF token and the sign-in redirect do not apply to it.
    /// </summary>
    public static void MapRemoteMcp(this WebApplication app) => app.MapMcp(Path).AllowAnonymous();

    /// <summary>Runs before authentication and CSRF so that <c>/mcp</c> is judged only by its key.</summary>
    public static async Task KeyCheck(HttpContext ctx, Func<Task> next)
    {
        if (!IsMcpPath(ctx.Request.Path)) { await next(); return; }

        var services = ctx.RequestServices;

        // A blocked address is turned away before anything touches the settings database, so a flood cannot queue up behind it.
        var throttle = services.GetRequiredService<McpKeyThrottle>();
        var client = McpKeyThrottle.ClientKey(ctx.Connection.RemoteIpAddress);
        if (throttle.IsBlocked(client, DateTime.UtcNow))
        {
            ctx.Response.Headers.RetryAfter = "300";
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var store = services.GetRequiredService<SettingsStore>();
        if (!await store.HasUsableApiKeyAsync(ct: ctx.RequestAborted))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var token = BearerToken(ctx.Request);
        var (identity, known) = await store.TryValidateApiKeyAsync(token ?? "", ct: ctx.RequestAborted);
        if (identity is null)
        {
            // Only a key that does not exist counts as a guess. No header, an expired key and a revoked key are clients
            // that need attention, and counting them would lock out everyone who shares their address.
            if (token is not null && !known) throttle.Failed(client, DateTime.UtcNow);
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // What this key may see = its list (or everything) limited to the servers that exist now. History can still hold
        // a server that was deleted from the settings; it must not show up.
        var live = services.GetRequiredService<LiveConfig>().Current.Instances.Select(i => i.Name);
        var allowed = identity.Instances is null ? live : live.Where(n => identity.Instances.Contains(n, StringComparer.OrdinalIgnoreCase));
        ctx.Items[ScopeItem] = new InstanceScope(allowed.ToList());
        ctx.Items[KeyNameItem] = identity.Name;

        // The endpoint is one POST address. Anything else a valid key asks for gets a plain answer here instead of falling
        // through to the web sign-in (a redirect to the login page means nothing to an MCP client).
        var address = ctx.Request.Path.Value!.TrimEnd('/');
        if (!string.Equals(address, Path, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.Headers.Allow = "POST";
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        await next();
    }

    /// <summary>True only for a /mcp request whose key was checked and accepted by <see cref="KeyCheck"/>.</summary>
    public static bool KeyAccepted(HttpContext ctx) =>
        IsMcpPath(ctx.Request.Path) && ctx.Items.ContainsKey(ScopeItem);

    public static bool IsMcpPath(PathString path) =>
        path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }
}

internal sealed class HttpContextScopeSource(IHttpContextAccessor accessor) : IInstanceScopeSource
{
    // No scope on the request means the key check did not run: show nothing rather than everything.
    public InstanceScope Current =>
        accessor.HttpContext?.Items[RemoteMcp.ScopeItem] as InstanceScope ?? new InstanceScope([]);
}

/// <summary>
/// Slows down guessing of keys: after 10 wrong keys from one address within 5 minutes, that address is refused for 5
/// minutes. In memory, so a restart clears it. While an address is refused, everything from it is refused, a right key
/// included; a right key from any other address is never limited.
/// </summary>
public sealed class McpKeyThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();
    private const int MaxClients = 10_000;
    private readonly Dictionary<string, List<DateTime>> _failures = new();

    /// <summary>
    /// One bucket per client. An IPv4 address mapped into IPv6 counts as the IPv4 address, and an IPv6 address counts as its
    /// /64 network, because one subscriber usually holds a whole /64 and could otherwise use a new address for every try.
    /// </summary>
    public static string ClientKey(System.Net.IPAddress? address)
    {
        if (address is null) return "";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new System.Net.IPAddress(bytes).ToString() + "/64";
    }

    /// <summary>Makes room by dropping addresses whose failures have all aged out, and if none, the one that failed longest ago. A lockout in force is never wiped wholesale.</summary>
    private void Evict(DateTime nowUtc)
    {
        foreach (var key in _failures.Where(kv => kv.Value.All(t => nowUtc - t > Window)).Select(kv => kv.Key).ToList()) _failures.Remove(key);
        if (_failures.Count < MaxClients) return;
        // Addresses that are not locked out go first: otherwise a flood of throwaway addresses would push out (and so lift)
        // the lockout of the one that is actually guessing.
        var candidates = _failures.Where(kv => kv.Value.Count < MaxFailures).ToList();
        if (candidates.Count == 0) candidates = _failures.ToList();
        _failures.Remove(candidates.MinBy(kv => kv.Value.Max()).Key);
    }

    public bool IsBlocked(string client, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (!_failures.TryGetValue(client, out var list)) return false;
            list.RemoveAll(t => nowUtc - t > Window);
            if (list.Count == 0) { _failures.Remove(client); return false; }
            return list.Count >= MaxFailures;
        }
    }

    public void Failed(string client, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_failures.Count >= MaxClients && !_failures.ContainsKey(client)) Evict(nowUtc);
            if (!_failures.TryGetValue(client, out var list)) _failures[client] = list = [];
            list.RemoveAll(t => nowUtc - t > Window);
            list.Add(nowUtc);
        }
    }
}
