using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Web;

internal static class AuthPages
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/login", async (HttpContext ctx, SettingsStore store, string? returnUrl) =>
        {
            if (!await store.HasAdminAsync(ctx.RequestAborted)) return Results.Redirect("/setup");
            if (ctx.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");
            return LoginPage(ctx, returnUrl, null);
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext ctx, SettingsStore store) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var user = form["username"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var result = await store.VerifyLoginAsync(user, form["password"].ToString(), DateTime.UtcNow, ctx.RequestAborted);
            switch (result)
            {
                case LoginResult.Ok:
                    await SqlLookoutWeb.SignInAsync(ctx, user, await store.GetSessionStampAsync(user, ctx.RequestAborted) ?? "");
                    return Results.Redirect(SqlLookoutWeb.SafeReturnUrl(returnUrl));
                default:
                    // The same answer for a wrong password, an unknown user and a locked account, so the page does not
                    // reveal which user names exist.
                    return LoginPage(ctx, returnUrl, Loc.T("Wrong user name or password, or the account is locked for 5 minutes after too many failed attempts."), 401);
            }
        }).AllowAnonymous();

        app.MapPost("/logout", async (HttpContext ctx, SettingsStore store) =>
        {
            // Rotating the stamp ends this session and any copy of its cookie.
            await store.RotateSessionStampAsync(SqlLookoutWeb.UserName(ctx), ctx.RequestAborted);
            await ctx.SignOutAsync(SqlLookoutWeb.Scheme);
            return Results.Redirect("/login");
        });

        app.MapGet("/setup", async (HttpContext ctx, SettingsStore store) =>
        {
            if (await store.HasAdminAsync(ctx.RequestAborted)) return Results.Redirect("/login");
            return SetupPage(ctx, null);
        }).AllowAnonymous();

        app.MapPost("/setup", async (HttpContext ctx, SettingsStore store, LiveConfig live) =>
        {
            if (await store.HasAdminAsync(ctx.RequestAborted)) return Results.Redirect("/login");
            if (!SqlLookoutWeb.IsLocalRequest(ctx)) return SetupPage(ctx, null, 403);

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var user = form["username"].ToString();
            var password = form["password"].ToString();
            if (password != form["confirm"].ToString()) return SetupPage(ctx, Loc.T("The two passwords are not the same."), 400, user);
            try
            {
                await store.CreateAdminAsync(user, password, ctx.RequestAborted);
            }
            catch (InvalidDataException ex)
            {
                return SetupPage(ctx, ex.Message, 400, user);
            }

            // The language the page was shown in becomes the language of the whole tool (changeable later in Settings).
            var global = await store.GetGlobalAsync(ctx.RequestAborted);
            global.Language = Loc.Language;
            await store.SaveGlobalAsync(global, ctx.RequestAborted);
            await live.RefreshAsync(ctx.RequestAborted);

            await SqlLookoutWeb.SignInAsync(ctx, user, await store.RotateSessionStampAsync(user, ctx.RequestAborted));
            return Results.Redirect("/");
        }).AllowAnonymous();
    }

    private static IResult LoginPage(HttpContext ctx, string? returnUrl, string? error, int status = 200)
    {
        var body = $"""
            <div class="card narrow">
              <h1>{H.E(Loc.T("Sign in"))}</h1>
              {H.Notice(error)}
              <form method="post" action="/login">
                {H.Token(SqlLookoutWeb.Token(ctx))}
                <input type="hidden" name="returnUrl" value="{H.E(SqlLookoutWeb.SafeReturnUrl(returnUrl))}">
                {H.Field(Loc.T("User name"), "username", required: true, extra: "autocomplete=\"username\" autofocus")}
                {H.Field(Loc.T("Password"), "password", type: "password", required: true, extra: "autocomplete=\"current-password\"")}
                <div class="actions"><button type="submit">{H.E(Loc.T("Sign in"))}</button></div>
              </form>
            </div>
            """;
        return SqlLookoutWeb.ShowAnonymous(Loc.T("Sign in"), body, status);
    }

    private static IResult SetupPage(HttpContext ctx, string? error, int status = 200, string? user = null)
    {
        var languageLinks = string.Join(" | ", Loc.Languages.Select(l =>
            l.Code == Loc.Language ? $"<strong>{H.E(l.Name)}</strong>" : $"<a href=\"/setup?lang={H.E(l.Code)}\">{H.E(l.Name)}</a>"));

        if (!SqlLookoutWeb.IsLocalRequest(ctx))
        {
            return SqlLookoutWeb.ShowAnonymous(Loc.T("First-time setup"), $"""
                <div class="card narrow">
                  <h1>{H.E(Loc.T("First-time setup"))}</h1>
                  <p class="notice warn">{H.E(Loc.T("The administrator account can only be created from the server itself, so nobody else on the network can claim it first."))}</p>
                  <p>{H.E(Loc.T("Open this page on the machine where SqlLookout runs (https://localhost:<port>/setup), for example over remote desktop."))}</p>
                </div>
                """, 403);
        }

        var body = $"""
            <div class="card narrow">
              <p class="small muted">{languageLinks}</p>
              <h1>{H.E(Loc.T("Welcome to SqlLookout"))}</h1>
              <p class="muted">{H.E(Loc.T("Create the administrator account. You will add your SQL Servers on the next screens."))}</p>
              {H.Notice(error)}
              <form method="post" action="/setup?lang={H.E(Loc.Language)}">
                {H.Token(SqlLookoutWeb.Token(ctx))}
                {H.Field(Loc.T("User name"), "username", user ?? "admin", required: true, extra: "autocomplete=\"username\"")}
                {H.Field(Loc.T("Password"), "password", type: "password", help: Loc.T("At least {0} characters.", SettingsStore.MinPasswordLength), required: true, extra: "autocomplete=\"new-password\"")}
                {H.Field(Loc.T("Repeat the password"), "confirm", type: "password", required: true, extra: "autocomplete=\"new-password\"")}
                <div class="actions"><button type="submit">{H.E(Loc.T("Create account"))}</button></div>
              </form>
            </div>
            """;
        return SqlLookoutWeb.ShowAnonymous(Loc.T("First-time setup"), body, status);
    }
}
