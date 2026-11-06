using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlLookout.Core;
using SqlLookout.Mcp;
using SqlLookout.Service;

namespace SqlLookout.Web;

/// <summary>
/// The web interface: sign-in, dashboard, servers, settings. Server-rendered HTML with no JavaScript, so the
/// content security policy can forbid scripts entirely. Needs these services registered: <see cref="SettingsStore"/>,
/// <see cref="SqlLookout.Storage.IHistoryStore"/> and <see cref="LiveConfig"/>.
/// </summary>
public static class SqlLookoutWeb
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    internal const string StampClaim = "sqllookout:stamp";

    public static void AddServices(IServiceCollection services)
    {
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "sqllookout.csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthentication(Scheme).AddCookie(o =>
        {
            o.Cookie.Name = "sqllookout.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.LoginPath = "/login";
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            // Every request checks the cookie's session stamp against the account's current one, so signing out
            // or changing the password ends all older sessions on the server side, not only in the browser.
            o.Events.OnValidatePrincipal = async context =>
            {
                var store = context.HttpContext.RequestServices.GetRequiredService<SettingsStore>();
                var user = context.Principal?.Identity?.Name;
                var stamp = context.Principal?.FindFirst(StampClaim)?.Value;
                var current = user is null ? null : await store.GetSessionStampAsync(user, context.HttpContext.RequestAborted);
                if (current is null || current != stamp)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(Scheme);
                }
            };
        });
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddRemoteMcp();
    }

    /// <summary>A page that takes over a second is logged with where the time went (waiting for the database, or running queries).</summary>
    private static readonly long SlowRequestMs = long.TryParse(Environment.GetEnvironmentVariable("SQLLOOKOUT_SLOW_MS"), out var ms) ? ms : 1000;

    private static async Task LogSlowRequests(HttpContext ctx, Func<Task> next)
    {
        var tally = new SqlLookout.Storage.StoreTally();
        SqlLookout.Storage.StoreTrace.Current = tally;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await next();
        if (clock.ElapsedMilliseconds < SlowRequestMs) return;
        ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("SlowPage")
            .LogWarning("{Path} took {Ms} ms. {Detail}", ctx.Request.Path + ctx.Request.QueryString, clock.ElapsedMilliseconds, tally.Summary());
    }

    public static void Map(WebApplication app)
    {
        app.Use(SecurityHeaders);
        app.Use(LogSlowRequests);
        // /mcp is judged by its bearer key alone and answers in English; everything else is the signed-in web interface.
        app.Use((ctx, next) => RemoteMcp.KeyCheck(ctx, () => next(ctx)));
        app.Use(UseLanguage);
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(CheckAntiforgery);

        // The look is a setting of the whole installation, so the sign-in page already has it.
        app.MapGet("/app.css", (LiveConfig live) => Results.Text(Css.For(live.Current.Look), "text/css; charset=utf-8")).AllowAnonymous();
        app.MapGet("/favicon.ico", () => Results.NoContent()).AllowAnonymous();

        AuthPages.Map(app);
        DashboardPage.Map(app);
        ServerPage.Map(app);
        InstancePages.Map(app);
        SettingsPages.Map(app);
        DiagnosticsPage.Map(app);
        McpKeysPage.Map(app);
        MutePage.Map(app);
        HistoryPage.Map(app);
        app.MapRemoteMcp();
    }

    /// <summary>The page is shown in the language chosen in Settings (or, only on the first-time setup page, in ?lang=).</summary>
    private static async Task UseLanguage(HttpContext ctx, RequestDelegate next)
    {
        if (RemoteMcp.IsMcpPath(ctx.Request.Path)) { await next(ctx); return; }
        var chosen = ctx.RequestServices.GetRequiredService<LiveConfig>().Current.Language;
        var preview = ctx.Request.Query["lang"].ToString();
        if (ctx.Request.Path == "/setup" && Loc.IsSupported(preview)) chosen = preview;
        using var scope = Loc.Use(chosen);
        // The light/dark choice is per browser (a cookie); no cookie = Auto, which follows the browser's own setting.
        H.Theme = ctx.Request.Cookies[ThemeCookie];
        await next(ctx);
    }

    internal const string ThemeCookie = "sqllookout.theme";

    private static Task SecurityHeaders(HttpContext ctx, RequestDelegate next)
    {
        var h = ctx.Response.Headers;
        // No scripts anywhere; styles only from our own stylesheet; forms may only post back to us.
        h["Content-Security-Policy"] = "default-src 'none'; style-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Cache-Control"] = "no-store";
        return next(ctx);
    }

    private static async Task CheckAntiforgery(HttpContext ctx, RequestDelegate next)
    {
        // A request to /mcp only gets here after its key was accepted; the key replaces the form token for it.
        if (HttpMethods.IsPost(ctx.Request.Method) && !RemoteMcp.KeyAccepted(ctx))
        {
            try
            {
                await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
            }
            catch (AntiforgeryValidationException)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("This form has expired or did not come from this site. Go back, reload the page and try again.");
                return;
            }
        }
        await next(ctx);
    }

    // ---- helpers shared by the page files ----

    internal static string Token(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx).RequestToken ?? "";

    internal static string UserName(HttpContext ctx) => ctx.User.Identity?.Name ?? "";

    /// <summary>A signed-in page with the standard frame.</summary>
    internal static IResult Show(HttpContext ctx, string title, string body, int refreshSeconds = 0, int status = 200) =>
        Results.Content(H.Page(title, body, true, UserName(ctx), refreshSeconds, Token(ctx), ReturnPath(ctx)), "text/html; charset=utf-8", statusCode: status);

    /// <summary>Where the language button should come back to: the current page when it can be fetched with GET, else the home page.</summary>
    private static string ReturnPath(HttpContext ctx) =>
        HttpMethods.IsGet(ctx.Request.Method) ? SafeReturnUrl(ctx.Request.Path + ctx.Request.QueryString) : "/";

    internal static IResult ShowAnonymous(string title, string body, int status = 200) =>
        Results.Content(H.Page(title, body, false), "text/html; charset=utf-8", statusCode: status);

    internal static bool IsLocalRequest(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress);

    internal static async Task SignInAsync(HttpContext ctx, string userName, string stamp)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, userName.Trim()), new Claim(StampClaim, stamp)], Scheme);
        await ctx.SignInAsync(Scheme, new ClaimsPrincipal(identity));
    }

    /// <summary>Only same-site relative paths are followed after sign-in (no open redirect).</summary>
    internal static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
