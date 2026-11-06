using System.Globalization;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;

namespace SqlLookout.Web;

internal static partial class SettingsPages
{
    private static readonly (string Rule, Func<string> Label)[] Rules =
    [
        (AlertRules.RuleBackup, () => Loc.T("Backups too old or missing")),
        (AlertRules.RuleSystemDbBackup, () => Loc.T("System databases (master, model, msdb): missing or overdue backup")),
        (AlertRules.RuleDbcc, () => Loc.T("DBCC CHECKDB too old or never run")),
        (AlertRules.RuleDisk, () => Loc.T("Low disk space and fast-falling disk space")),
        (AlertRules.RuleLog, () => Loc.T("Transaction log nearly full")),
        (AlertRules.RuleDatabaseState, () => Loc.T("Database offline, suspect or in recovery trouble")),
        (AlertRules.RuleSuspectPages, () => Loc.T("Suspect pages found")),
        (AlertRules.RuleJobs, () => Loc.T("SQL Agent job failed or running long")),
        (AlertRules.RuleCpu, () => Loc.T("CPU too high for too long")),
        (AlertRules.RuleBlocking, () => Loc.T("Sessions blocked for a long time")),
        (AlertRules.RuleMemory, () => Loc.T("Low page life expectancy or queries waiting for memory")),
        (AlertRules.RuleDeadlocks, () => Loc.T("Deadlocks")),
        (AlertRules.RuleConnections, () => Loc.T("Too many connections for too long")),
        (AlertRules.RuleIoLatency, () => Loc.T("Slow disk reads or writes for too long")),
        (AlertRules.RuleAvailabilityGroups, () => Loc.T("Availability group replica or database not healthy")),
        (AlertRules.RuleLogShipping, () => Loc.T("Log shipping behind its limit")),
        (AlertRules.RuleMirroring, () => Loc.T("Database mirroring suspended or disconnected")),
        (AlertRules.RuleExpressLimit, () => Loc.T("Express database near its size cap")),
        (AlertRules.RuleTempDb, () => Loc.T("tempdb nearly full")),
        (AlertRules.RuleFailedLogins, () => Loc.T("Many failed logins")),
    ];

    private static readonly PropertyInfo[] ThresholdProperties =
        typeof(AlertThresholds).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(int)).ToArray();

    private static string ThresholdLabel(string property) => property switch
    {
        nameof(AlertThresholds.FullBackupWarnHours) => Loc.T("Backup age: warning after (hours)"),
        nameof(AlertThresholds.FullBackupCriticalHours) => Loc.T("Backup age: critical after (hours)"),
        nameof(AlertThresholds.DbccWarnDays) => Loc.T("CHECKDB age: warning after (days)"),
        nameof(AlertThresholds.DbccCriticalDays) => Loc.T("CHECKDB age: critical after (days)"),
        nameof(AlertThresholds.DiskWarnFreePercent) => Loc.T("Disk free: warning below (%)"),
        nameof(AlertThresholds.DiskCriticalFreePercent) => Loc.T("Disk free: critical below (%)"),
        nameof(AlertThresholds.DiskWarnFreeGb) => Loc.T("Disk free: warning below (GB)"),
        nameof(AlertThresholds.DiskCriticalFreeGb) => Loc.T("Disk free: critical below (GB)"),
        nameof(AlertThresholds.DiskFallWarnHours) => Loc.T("Disk fall rate: warning if full within (hours)"),
        nameof(AlertThresholds.DiskFallCriticalHours) => Loc.T("Disk fall rate: critical if full within (hours)"),
        nameof(AlertThresholds.DiskFallWindowHours) => Loc.T("Disk fall rate: trend window (hours)"),
        nameof(AlertThresholds.LogWarnPercent) => Loc.T("Log usage: warning above (%)"),
        nameof(AlertThresholds.LogCriticalPercent) => Loc.T("Log usage: critical above (%)"),
        nameof(AlertThresholds.LogMinSizeMb) => Loc.T("Log usage: ignore logs smaller than (MB)"),
        nameof(AlertThresholds.CpuWarnPercent) => Loc.T("CPU: warning at or above (%)"),
        nameof(AlertThresholds.CpuSustainedMinutes) => Loc.T("CPU: for at least (minutes)"),
        nameof(AlertThresholds.BlockingWarnSeconds) => Loc.T("Blocking: warning when a session has waited (seconds)"),
        nameof(AlertThresholds.BlockingCriticalSeconds) => Loc.T("Blocking: critical when a session has waited (seconds)"),
        nameof(AlertThresholds.MemoryPleWarnSeconds) => Loc.T("Page life expectancy: warning below (seconds)"),
        nameof(AlertThresholds.MemoryPleSustainedMinutes) => Loc.T("Page life expectancy: for at least (minutes)"),
        nameof(AlertThresholds.MemoryGrantsPendingMinutes) => Loc.T("Memory grants: queries waiting for at least (minutes)"),
        nameof(AlertThresholds.DeadlockWindowHours) => Loc.T("Deadlocks: counted over (hours)"),
        nameof(AlertThresholds.DeadlockWarnCount) => Loc.T("Deadlocks: warning from (count)"),
        nameof(AlertThresholds.DeadlockCriticalCount) => Loc.T("Deadlocks: critical from (count)"),
        nameof(AlertThresholds.ConnectionsWarnCount) => Loc.T("Connections: warning at or above (count)"),
        nameof(AlertThresholds.ConnectionsCriticalCount) => Loc.T("Connections: critical at or above (count)"),
        nameof(AlertThresholds.ConnectionsSustainedMinutes) => Loc.T("Connections: for at least (minutes)"),
        nameof(AlertThresholds.IoLatencyWarnMs) => Loc.T("Disk I/O latency: warning at or above (ms)"),
        nameof(AlertThresholds.IoLatencyCriticalMs) => Loc.T("Disk I/O latency: critical at or above (ms)"),
        nameof(AlertThresholds.IoLatencySustainedMinutes) => Loc.T("Disk I/O latency: for at least (minutes)"),
        nameof(AlertThresholds.LogShippingCriticalMultiplier) => Loc.T("Log shipping: critical at (times the limit set up in log shipping)"),
        nameof(AlertThresholds.ExpressLimitWarnPercent) => Loc.T("Express size cap: warning at or above (% of the cap)"),
        nameof(AlertThresholds.TempDbWarnPercent) => Loc.T("tempdb: warning at or above (% of the room it can reach)"),
        nameof(AlertThresholds.FailedLoginWarnCount) => Loc.T("Failed logins: warning from (count in 15 minutes)"),
        nameof(AlertThresholds.FailedLoginCriticalCount) => Loc.T("Failed logins: critical from (count in 15 minutes)"),
        nameof(AlertThresholds.TempDbCriticalPercent) => Loc.T("tempdb: critical at or above (% of the room it can reach)"),
        nameof(AlertThresholds.ExpressLimitCriticalPercent) => Loc.T("Express size cap: critical at or above (% of the cap)"),
        nameof(AlertThresholds.UnreachableWarnAfter) => Loc.T("Unreachable: warning after (failed attempts)"),
        nameof(AlertThresholds.UnreachableCriticalAfter) => Loc.T("Unreachable: critical after (failed attempts)"),
        nameof(AlertThresholds.JobRunningMultiplier) => Loc.T("Agent job: running long above (times the usual)"),
        nameof(AlertThresholds.JobRunningMinMinutes) => Loc.T("Agent job: never warn below (minutes)"),
        nameof(AlertThresholds.CollectorFailingAfter) => Loc.T("Failing check: report after (failed runs in a row)"),
        _ => property,
    };

    private static string CollectorLabel(string collector) => collector switch
    {
        "backup_age" => Loc.T("Backup age"),
        "disk_space" => Loc.T("Disk space"),
        "log_usage" => Loc.T("Log usage"),
        "database_state" => Loc.T("Database state"),
        "file_size" => Loc.T("File sizes"),
        "suspect_pages" => Loc.T("Suspect pages"),
        "dbcc_last_good" => Loc.T("Last good DBCC CHECKDB"),
        "agent_jobs" => Loc.T("SQL Agent jobs"),
        "cpu" => Loc.T("CPU"),
        "memory" => Loc.T("Memory"),
        "connections" => Loc.T("Connections"),
        "blocking" => Loc.T("Blocking"),
        "deadlocks" => Loc.T("Deadlocks"),
        "io_latency" => Loc.T("Disk I/O latency"),
        "wait_stats" => Loc.T("Wait statistics"),
        "availability_groups" => Loc.T("Availability groups"),
        "log_shipping" => Loc.T("Log shipping"),
        "mirroring" => Loc.T("Database mirroring"),
        "express_limit" => Loc.T("Express size cap"),
        "tempdb" => Loc.T("tempdb space"),
        "failed_logins" => Loc.T("Failed logins"),
        _ => collector,
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/settings", async (HttpContext ctx, SettingsStore store, string? msg) =>
        {
            var alerts = await store.GetAlertSettingsAsync(ctx.RequestAborted);
            var global = await store.GetGlobalAsync(ctx.RequestAborted);
            var hasPassword = await store.HasSmtpPasswordAsync(ctx.RequestAborted);
            var hasTeams = await store.HasTeamsUrlAsync(ctx.RequestAborted);
            var notice = msg == "saved" ? H.Notice(Loc.T("Settings saved. They apply immediately."), "ok") : "";
            return SqlLookoutWeb.Show(ctx, Loc.T("Settings"), Render(ctx, alerts, global, hasPassword, hasTeams, notice));
        });

        app.MapPost("/settings", async (HttpContext ctx, SettingsStore store, LiveConfig live, DiagnosticLog diag) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            AlertSettings alerts;
            GlobalSettings global;
            var storedLook = (await store.GetGlobalAsync(ctx.RequestAborted)).Look;
            try
            {
                (alerts, global) = Parse(form, storedLook);
                await store.SaveSettingsAsync(alerts, global, form["email_password"].ToString(), form["email_clear_password"] == "1", ctx.RequestAborted,
                    newTeamsUrl: form["teams_url"].ToString().Trim(), clearTeamsUrl: form["teams_clear_url"] == "1");
            }
            catch (InvalidDataException ex)
            {
                var current = await store.GetAlertSettingsAsync(ctx.RequestAborted);
                var currentGlobal = await store.GetGlobalAsync(ctx.RequestAborted);
                var hasPassword = await store.HasSmtpPasswordAsync(ctx.RequestAborted);
                var hasTeams = await store.HasTeamsUrlAsync(ctx.RequestAborted);
                return SqlLookoutWeb.Show(ctx, Loc.T("Settings"), Render(ctx, current, currentGlobal, hasPassword, hasTeams, H.Notice(ex.Message)), status: 400);
            }
            await live.RefreshAsync(ctx.RequestAborted);
            diag.Enabled = global.DiagnosticLog;
            diag.Write("settings", $"Saved. Diagnostic log on, Claude Code links {(alerts.ClaudeLinks ? "on" : "off")}.");
            return Results.Redirect("/settings?msg=saved");
        });

        // The language button in every page header.
        app.MapPost("/language", async (HttpContext ctx, SettingsStore store, LiveConfig live) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var language = form["language"].ToString();
            if (Loc.IsSupported(language))
            {
                var global = await store.GetGlobalAsync(ctx.RequestAborted);
                global.Language = language;
                await store.SaveGlobalAsync(global, ctx.RequestAborted);
                await live.RefreshAsync(ctx.RequestAborted);
            }
            return Results.Redirect(SqlLookoutWeb.SafeReturnUrl(form["returnUrl"].ToString()));
        });

        // The theme button in every page header: Auto (no cookie), Light or Dark, remembered by this browser only.
        app.MapPost("/theme", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var theme = form["theme"].ToString();
            if (H.Themes.Contains(theme))
                ctx.Response.Cookies.Append(SqlLookoutWeb.ThemeCookie, theme, new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(365), Path = "/",
                });
            else
                ctx.Response.Cookies.Delete(SqlLookoutWeb.ThemeCookie, new CookieOptions { Path = "/" });
            return Results.Redirect(SqlLookoutWeb.SafeReturnUrl(form["returnUrl"].ToString()));
        });

        app.MapPost("/settings/test-email", async (HttpContext ctx, LiveConfig live) =>
        {
            var email = live.Current.Alerts.Email;
            string result;
            if (email is null)
            {
                result = H.Notice(Loc.T("No email is saved yet. Fill in the email section, press Save, then test."), "warn");
            }
            else
            {
                try
                {
                    await new SmtpAlertSender(email, email.ResolvePassword()).SendAsync(
                        Loc.T("[SqlLookout] Test mail"), Loc.T("If you can read this, SqlLookout can send email through {0}.", email.Host), ctx.RequestAborted);
                    result = H.Notice(Loc.T("Test mail sent to {0}. Check the inbox (and the spam folder).", string.Join(", ", email.To)), "ok");
                }
                catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException or System.Net.Sockets.SocketException or TimeoutException)
                {
                    var message = ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message;
                    if (message.Length > 300) message = message[..300];
                    result = H.Notice(Loc.T("Could not send: {0}", message), "bad");
                }
            }
            return SqlLookoutWeb.Show(ctx, Loc.T("Test email"), $"""<h1>{H.E(Loc.T("Test email"))}</h1>{result}<div class="actions"><a class="btn secondary" href="/settings">{H.E(Loc.T("Back to settings"))}</a></div>""");
        });

        app.MapPost("/settings/test-teams", async (HttpContext ctx, LiveConfig live) =>
        {
            var teams = live.Current.Alerts.Teams;
            string result;
            if (teams is null)
            {
                result = H.Notice(Loc.T("Teams is not saved yet. Tick the Teams box, paste the address, press Save, then test."), "warn");
            }
            else
            {
                try
                {
                    await new TeamsAlertSender(teams).SendAsync(
                        Loc.T("[SqlLookout] Test message"), Loc.T("If you can read this, SqlLookout can post to Teams."), ctx.RequestAborted);
                    result = H.Notice(Loc.T("Test message sent to Teams. Look in the chat or channel the flow posts to."), "ok");
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or TimeoutException or IOException)
                {
                    var message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                    result = H.Notice(Loc.T("Could not send: {0}", message), "bad");
                }
            }
            return SqlLookoutWeb.Show(ctx, Loc.T("Test Teams message"), $"""<h1>{H.E(Loc.T("Test Teams message"))}</h1>{result}<div class="actions"><a class="btn secondary" href="/settings">{H.E(Loc.T("Back to settings"))}</a></div>""");
        });

        app.MapGet("/account", (HttpContext ctx, string? msg) =>
            SqlLookoutWeb.Show(ctx, Loc.T("Account"), AccountPage(ctx, msg == "changed" ? H.Notice(Loc.T("Password changed."), "ok") : "")));

        app.MapPost("/account", async (HttpContext ctx, SettingsStore store) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (form["new"] != form["confirm"])
                return SqlLookoutWeb.Show(ctx, Loc.T("Account"), AccountPage(ctx, H.Notice(Loc.T("The two new passwords are not the same."))), status: 400);
            try
            {
                await store.ChangePasswordAsync(SqlLookoutWeb.UserName(ctx), form["current"].ToString(), form["new"].ToString(), DateTime.UtcNow, ctx.RequestAborted);
                // Every other session of this account ends; this browser gets a fresh cookie.
                var stamp = await store.RotateSessionStampAsync(SqlLookoutWeb.UserName(ctx), ctx.RequestAborted);
                await SqlLookoutWeb.SignInAsync(ctx, SqlLookoutWeb.UserName(ctx), stamp);
            }
            catch (InvalidDataException ex)
            {
                return SqlLookoutWeb.Show(ctx, Loc.T("Account"), AccountPage(ctx, H.Notice(ex.Message)), status: 400);
            }
            return Results.Redirect("/account?msg=changed");
        });
    }

    private static string AccountPage(HttpContext ctx, string notice) => $"""
        <h1>{H.E(Loc.T("Account"))}</h1>
        {notice}
        <form method="post" action="/account" class="card">
          {H.Token(SqlLookoutWeb.Token(ctx))}
          <p class="muted">{H.E(Loc.T("Signed in as {0}.", SqlLookoutWeb.UserName(ctx)))}</p>
          {H.Field(Loc.T("Current password"), "current", type: "password", required: true, extra: "autocomplete=\"current-password\"")}
          {H.Field(Loc.T("New password"), "new", type: "password", help: Loc.T("At least {0} characters.", SettingsStore.MinPasswordLength), required: true, extra: "autocomplete=\"new-password\"")}
          {H.Field(Loc.T("Repeat the new password"), "confirm", type: "password", required: true, extra: "autocomplete=\"new-password\"")}
          <div class="actions"><button type="submit">{H.E(Loc.T("Change password"))}</button></div>
        </form>
        """;

    private static string Render(HttpContext ctx, AlertSettings a, GlobalSettings g, bool hasSmtpPassword, bool hasTeamsUrl, string notice)
    {
        var email = a.Email;
        var sb = new StringBuilder();
        sb.Append($"<h1>{H.E(Loc.T("Settings"))}</h1>{notice}");
        sb.Append($"""<form method="post" action="/settings">{H.Token(SqlLookoutWeb.Token(ctx))}""");

        sb.Append($"""
            <section class="card"><h2 style="margin-top:0">{H.E(Loc.T("Email alerts"))}</h2>
              <p class="muted small">{H.E(Loc.T("Without email, problems only show on the dashboard."))}</p>
              {H.Check(Loc.T("Send alerts by email"), "email_on", email is not null)}
              <div class="grid2">
                <div>
                  {H.Field(Loc.T("SMTP server"), "email_host", email?.Host, help: Loc.T("For example smtp.office365.com or your mail relay."))}
                  {H.Field(Loc.T("Port"), "email_port", (email?.Port ?? 25).ToString(CultureInfo.InvariantCulture), "number", Loc.T("25 for a plain relay, 587 for STARTTLS, 465 for SSL."))}
                  {H.Check(Loc.T("Use SSL/TLS"), "email_ssl", email?.UseSsl ?? false)}
                  {H.Field(Loc.T("Send from"), "email_from", email?.From, "text", Loc.T("The sender address, for example sqllookout@yourcompany.com"))}
                </div>
                <div>
                  {H.Field(Loc.T("Send to"), "email_to", string.Join(", ", email?.To ?? []), help: Loc.T("One or more addresses, separated by commas."))}
                  {H.Field(Loc.T("SMTP user name"), "email_user", email?.User, help: Loc.T("Leave empty if the relay needs no login."))}
                  {H.Field(Loc.T("SMTP password"), "email_password", null, "password", hasSmtpPassword ? Loc.T("A password is saved. Leave blank to keep it.") : Loc.T("Stored encrypted."), extra: "autocomplete=\"new-password\"")}
                  {(hasSmtpPassword ? H.Check(Loc.T("Remove the saved password"), "email_clear_password", false) : "")}
                </div>
              </div>
              <p class="small muted">{H.E(Loc.T("Save first, then use \"Send a test email\" below."))}</p>
            </section>
            """);

        sb.Append($"""
            <section class="card"><h2 style="margin-top:0">{H.E(Loc.T("Teams alerts"))}</h2>
              <p class="muted small">{H.E(Loc.T("Posts the same alerts to a Teams chat or channel, next to email or instead of it."))}</p>
              {H.Check(Loc.T("Send alerts to Teams"), "teams_on", a.Teams is not null)}
              {H.Field(Loc.T("Teams webhook address"), "teams_url", null, "password", hasTeamsUrl ? Loc.T("An address is saved. Leave blank to keep it.") : Loc.T("Create it in Teams with a Workflows flow \"When a Teams webhook request is received\". Stored encrypted. In Teams, a chat needs at least two members before a flow can post to it."), extra: "autocomplete=\"off\"")}
              {(hasTeamsUrl ? H.Check(Loc.T("Remove the saved address"), "teams_clear_url", false) : "")}
              <p class="small muted">{H.E(Loc.T("Save first, then use \"Send a test Teams message\" below."))}</p>
            </section>
            """);

        sb.Append($"""
            <section class="card"><h2 style="margin-top:0">{H.E(Loc.T("Fewer emails"))}</h2>
              <p class="muted small">{H.E(Loc.T("Urgent problems (cannot connect, broken database, suspect pages) are always mailed at once. Everything else can be collected into one mail."))}</p>
              {H.Check(Loc.T("Write the same problem on several servers as one entry"), "merge_hosts", a.MergeHosts)}
              <div class="grid2">
                {H.Field(Loc.T("Mail the collected problems every (hours)"), "digest_hours", a.DigestEveryHours.ToString(CultureInfo.InvariantCulture), "number", Loc.T("0 = no collecting, every problem is mailed at once. For example 4 or 24."), extra: "step=\"any\" min=\"0\" max=\"24\"")}
                {H.Field(Loc.T("First collected mail of the day at"), "digest_start", a.DigestStart, "text", Loc.T("HH:mm, for example 08:00. With 4 hours the mails come at 08:00, 12:00, 16:00 and so on."))}
              </div>
            </section>
            """);

        sb.Append($"""
            <section class="card"><h2 style="margin-top:0">{H.E(Loc.T("When to tell you"))}</h2>
              <div class="grid2">
                <div>
                  {H.Field(Loc.T("Repeat a problem that is still open after (hours)"), "renotify", a.RenotifyHours.ToString(CultureInfo.InvariantCulture), "number", Loc.T("A problem is mailed once, again after this time while it lasts, at once if it gets worse, and once when it is resolved."), extra: "step=\"any\" min=\"0.1\"")}
                  {H.Field(Loc.T("Daily summary at (local time)"), "summary", a.SummaryTime, "text", Loc.T("HH:mm, for example 08:00. Leave empty for no summary. It also tells you the service is still alive."))}
                </div>
                <div>
                  {H.Field(Loc.T("Quiet hours start"), "quiet_start", a.QuietStart, "text", Loc.T("HH:mm, for example 22:00. In quiet hours warnings wait; critical problems are still sent."))}
                  {H.Field(Loc.T("Quiet hours end"), "quiet_end", a.QuietEnd, "text", Loc.T("HH:mm, for example 07:00. Leave both empty for no quiet hours."))}
                  {H.Check(Loc.T("Warnings only in the daily summary"), "warnings_summary", a.WarningsInSummaryOnly, Loc.T("Warnings are not mailed at once; they are listed, with details, in the daily summary. Critical problems are still mailed at once. Needs the daily summary to be on."))}
                  {H.Check(Loc.T("Write a diagnostic log"), "diag_log", g.DiagnosticLog, Loc.T("Writes warnings, errors and Claude Code link events to a file so a problem can be reported from it. Off by default."))}
                  <p class="small"><a href="/settings/diagnostics">{H.E(Loc.T("Open the diagnostic log"))}</a></p>
                  {H.Check(Loc.T("Add Claude Code links"), "claude_links", a.ClaudeLinks, Loc.T("Adds a link to each problem on the Problems page that opens Claude Code with a prepared question. Needs Claude Code and the SqlLookout MCP on the computer of the person who clicks; the question is only filled in, never sent for them."))}
                </div>
              </div>
            </section>
            """);

        sb.Append($"""<section class="card"><h2 style="margin-top:0">{H.E(Loc.T("What to check"))}</h2><h3>{H.E(Loc.T("Alert on"))}</h3>""");
        foreach (var (rule, label) in Rules)
            sb.Append(H.Check(label(), "rule_" + rule, !a.DisabledRules.Contains(rule, StringComparer.OrdinalIgnoreCase)));
        sb.Append($"<h3>{H.E(Loc.T("Run these checks"))}</h3>");
        foreach (var c in CollectorCatalog.All())
            sb.Append(H.Check(CollectorLabel(c.Name), "col_" + c.Name, !g.DisabledCollectors.Contains(c.Name, StringComparer.OrdinalIgnoreCase)));
        sb.Append("</section>");

        sb.Append($"""<section class="card"><h2 style="margin-top:0">{H.E(Loc.T("Limits"))}</h2><p class="muted small">{H.E(Loc.T("The defaults suit most servers. Change a number to make an alert fire earlier or later."))}</p><div class="grid2">""");
        foreach (var p in ThresholdProperties)
        {
            var value = Convert.ToString(p.GetValue(a.Thresholds), CultureInfo.InvariantCulture);
            sb.Append(H.Field(ThresholdLabel(p.Name), "th_" + p.Name, value, "number", extra: "step=\"any\" min=\"0\""));
        }
        sb.Append("</div></section>");

        sb.Append($"""
            <section class="card"><h2 style="margin-top:0">{H.E(Loc.T("General"))}</h2>
              <div class="grid2">
                <div>
                  {H.Select(Loc.T("Language"), "language", Loc.Languages.Select(l => (l.Code, l.Name)), g.Language, Loc.T("Used for the pages and for alert emails."))}
                  {H.Select(Loc.T("Look"), "look", [(Looks.Classic, Loc.T("Classic")), (Looks.Terminal, Loc.T("Terminal"))], g.Look, Loc.T("How the pages look. It is the same for everyone who opens them."))}
                </div>
                <div>
                  {H.Field(Loc.T("Keep history for (days)"), "retention", g.RetentionDays.ToString(CultureInfo.InvariantCulture), "number", Loc.T("Older measurements are deleted once a day."), extra: "min=\"1\" max=\"3650\"")}
                </div>
              </div>
            </section>
            <div class="actions"><button type="submit">{H.E(Loc.T("Save settings"))}</button></div>
            </form>
            <form method="post" action="/settings/test-email" class="actions">{H.Token(SqlLookoutWeb.Token(ctx))}<button class="secondary">{H.E(Loc.T("Send a test email"))}</button><span class="muted small">{H.E(Loc.T("Uses the saved settings."))}</span></form>
            <form method="post" action="/settings/test-teams" class="actions">{H.Token(SqlLookoutWeb.Token(ctx))}<button class="secondary">{H.E(Loc.T("Send a test Teams message"))}</button></form>
            <p><a href="/settings/mcp">{H.E(Loc.T("MCP access keys"))}</a> <span class="muted small">{H.E(Loc.T("Let an AI assistant on another computer read SqlLookout over https."))}</span></p>
            """);
        return sb.ToString();
    }

    private static (AlertSettings, GlobalSettings) Parse(IFormCollection form, string currentLook)
    {
        string F(string key) => form[key].ToString().Trim();
        double Number(string key, string label)
        {
            if (!double.TryParse(F(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v) || v < 0)
                throw new InvalidDataException(Loc.T("\"{0}\" must be a number, zero or more (use a dot for decimals).", label));
            return v;
        }

        var alerts = new AlertSettings();
        if (form["email_on"] == "1")
        {
            if (!int.TryParse(F("email_port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw new InvalidDataException(Loc.T("The SMTP port must be a number from 1 to 65535."));
            var to = SplitAddresses().Split(F("email_to")).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            foreach (var address in to.Append(F("email_from")).Where(x => x.Length > 0))
                if (!MailAddress.TryCreate(address, out _)) throw new InvalidDataException(Loc.T("\"{0}\" is not a valid email address.", address));

            alerts.Email = new EmailSettings
            {
                Host = F("email_host"),
                Port = port,
                UseSsl = form["email_ssl"] == "1",
                User = F("email_user") is { Length: > 0 } user ? user : null,
                From = F("email_from"),
                To = to,
            };
        }

        if (form["teams_on"] == "1") alerts.Teams = new TeamsSettings();

        alerts.RenotifyHours = Number("renotify", Loc.T("Repeat a problem that is still open after (hours)"));
        if (alerts.RenotifyHours <= 0) throw new InvalidDataException(Loc.T("The repeat time must be more than zero hours."));
        alerts.SummaryTime = F("summary") is { Length: > 0 } summary ? summary : null;
        alerts.WarningsInSummaryOnly = form["warnings_summary"] == "1";
        alerts.ClaudeLinks = form["claude_links"] == "1";
        alerts.DigestEveryHours = F("digest_hours").Length == 0 ? 0 : Number("digest_hours", Loc.T("Mail the collected problems every (hours)"));
        alerts.DigestStart = F("digest_start") is { Length: > 0 } ds ? ds : "08:00";
        alerts.MergeHosts = form.ContainsKey("digest_hours") ? form["merge_hosts"] == "1" : true;
        alerts.QuietStart = F("quiet_start") is { Length: > 0 } qs ? qs : null;
        alerts.QuietEnd = F("quiet_end") is { Length: > 0 } qe ? qe : null;

        alerts.DisabledRules = Rules.Where(r => form["rule_" + r.Rule] != "1").Select(r => r.Rule).ToList();

        foreach (var p in ThresholdProperties)
        {
            var v = Number("th_" + p.Name, ThresholdLabel(p.Name));
            p.SetValue(alerts.Thresholds, p.PropertyType == typeof(int) ? (object)(int)Math.Round(v) : v);
        }

        var global = new GlobalSettings
        {
            DisabledCollectors = CollectorCatalog.All().Where(c => form["col_" + c.Name] != "1").Select(c => c.Name).ToList(),
            // A form from before the language setting existed has no such field: keep the language as it is.
            Language = Loc.IsSupported(F("language")) ? F("language") : Loc.Language,
            // Same for the look: a form without the field keeps what is stored, a made-up value is refused when saving.
            Look = form.ContainsKey("look") ? F("look") : currentLook,
            DiagnosticLog = form["diag_log"] == "1",
        };
        if (!int.TryParse(F("retention"), NumberStyles.None, CultureInfo.InvariantCulture, out var days))
            throw new InvalidDataException(Loc.T("Keep history for a whole number of days."));
        global.RetentionDays = days;
        return (alerts, global);
    }

    [GeneratedRegex(@"[,;\s]+")]
    private static partial Regex SplitAddresses();
}
