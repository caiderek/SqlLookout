using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using SqlLookout.Alerts;
using SqlLookout.Core;
using SqlLookout.Service;
using SqlLookout.Storage;

namespace SqlLookout.Web;

internal static class InstancePages
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/instances", async (HttpContext ctx, SettingsStore store, IHistoryStore history, string? msg) =>
        {
            var list = await store.GetInstancesAsync(ctx.RequestAborted);
            var sb = new StringBuilder();
            sb.Append($"<h1>{H.E(Loc.T("SQL Servers"))}</h1>");
            sb.Append(msg switch
            {
                "added" => H.Notice(Loc.T("Server added. Press Test to check that it can be reached; data appears within a minute."), "ok"),
                "saved" => H.Notice(Loc.T("Saved."), "ok"),
                "deleted" => H.Notice(Loc.T("Server removed. Its history is kept until it ages out."), "ok"),
                _ => "",
            });

            if (list.Count == 0)
            {
                sb.Append($"""<div class="card"><p>{H.E(Loc.T("No servers yet."))}</p><div class="actions"><a class="btn" href="/instances/new">{H.E(Loc.T("Add a server"))}</a></div></div>""");
            }
            else
            {
                sb.Append($"""<div class="card tablewrap"><table><thead><tr><th>{H.E(Loc.T("Name"))}</th><th>{H.E(Loc.T("Server"))}</th><th>{H.E(Loc.T("Login"))}</th><th>{H.E(Loc.T("Encryption"))}</th><th>{H.E(Loc.T("Status"))}</th><th></th></tr></thead><tbody>""");
                var token = SqlLookoutWeb.Token(ctx);
                foreach (var i in list)
                {
                    var reach = await history.GetRecentRunsAsync(i.Name, CollectorCatalog.Connectivity, 1, ctx.RequestAborted);
                    var status = !i.Enabled ? DashboardPage.Badge("off", Loc.T("paused"))
                        : reach.Count == 0 ? DashboardPage.Badge("off", Loc.T("no data yet"))
                        : reach[0].Status == CollectionStatus.Ok ? DashboardPage.Badge("ok", Loc.T("reachable"))
                        : DashboardPage.Badge("bad", Loc.T("unreachable"));
                    var enc = i.Encrypt switch { EncryptModes.Off => Loc.T("none"), EncryptModes.Verify => Loc.T("verified"), _ => Loc.T("encrypted") };
                    var url = Uri.EscapeDataString(i.Name);
                    sb.Append($"""
                        <tr>
                          <td><a href="{ServerPage.Link(i.Name, "problems")}"><strong>{H.E(i.Name)}</strong></a></td>
                          <td>{H.E(i.Server)}</td>
                          <td>{H.E(i.Auth == AuthModes.Windows ? Loc.T("Windows account of the service") : i.UserName)}</td>
                          <td>{H.E(enc)}</td>
                          <td>{status}</td>
                          <td>
                            <form method="post" action="/instances/{url}/test" class="inline">{H.Token(token)}<button class="secondary">{H.E(Loc.T("Test"))}</button></form>
                            <a class="btn secondary" href="/instances/{url}/edit">{H.E(Loc.T("Edit"))}</a>
                            <a class="btn secondary" href="/instances/{url}/delete">{H.E(Loc.T("Remove"))}</a>
                          </td>
                        </tr>
                        """);
                }
                sb.Append($"</tbody></table></div><div class=\"actions\"><a class=\"btn\" href=\"/instances/new\">{H.E(Loc.T("Add a server"))}</a></div>");
            }
            return SqlLookoutWeb.Show(ctx, Loc.T("Servers"), sb.ToString());
        });

        app.MapGet("/instances/new", (HttpContext ctx) =>
            SqlLookoutWeb.Show(ctx, Loc.T("Add a server"), FormPage(ctx, null, true, new InstanceRecord("", "", AuthModes.Windows, null, EncryptModes.Trust, true, false))));

        app.MapPost("/instances/new", async (HttpContext ctx, SettingsStore store, LiveConfig live) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var (record, password) = ReadForm(form, form["name"].ToString());
            try
            {
                await store.SaveInstanceAsync(record, password, isNew: true, ctx.RequestAborted);
            }
            catch (InvalidDataException ex)
            {
                return SqlLookoutWeb.Show(ctx, Loc.T("Add a server"), FormPage(ctx, ex.Message, true, record), status: 400);
            }
            await live.RefreshAsync(ctx.RequestAborted);
            return Results.Redirect("/instances?msg=added");
        });

        app.MapGet("/instances/{name}/edit", async (HttpContext ctx, SettingsStore store, string name) =>
        {
            var record = await store.GetInstanceAsync(name, ctx.RequestAborted);
            return record is null
                ? NotFound(ctx)
                : SqlLookoutWeb.Show(ctx, Loc.T("Edit {0}", record.Name), FormPage(ctx, null, false, record));
        });

        app.MapPost("/instances/{name}/edit", async (HttpContext ctx, SettingsStore store, LiveConfig live, string name) =>
        {
            var existing = await store.GetInstanceAsync(name, ctx.RequestAborted);
            if (existing is null) return NotFound(ctx);

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var (record, password) = ReadForm(form, existing.Name);
            try
            {
                await store.SaveInstanceAsync(record, password, isNew: false, ctx.RequestAborted);
            }
            catch (InvalidDataException ex)
            {
                return SqlLookoutWeb.Show(ctx, Loc.T("Edit {0}", existing.Name), FormPage(ctx, ex.Message, false, record with { HasPassword = existing.HasPassword }), status: 400);
            }
            await live.RefreshAsync(ctx.RequestAborted);
            return Results.Redirect("/instances?msg=saved");
        });

        app.MapGet("/instances/{name}/delete", async (HttpContext ctx, SettingsStore store, string name) =>
        {
            var record = await store.GetInstanceAsync(name, ctx.RequestAborted);
            if (record is null) return NotFound(ctx);
            return SqlLookoutWeb.Show(ctx, Loc.T("Remove {0}?", record.Name), $"""
                <h1>{H.E(Loc.T("Remove {0}?", record.Name))}</h1>
                <div class="card">
                  <p>{H.E(Loc.T("SqlLookout stops watching {0}. Nothing is changed on that server. The history already collected stays until it ages out.", record.Server))}</p>
                  <form method="post" action="/instances/{Uri.EscapeDataString(record.Name)}/delete">
                    {H.Token(SqlLookoutWeb.Token(ctx))}
                    <div class="actions"><button class="danger">{H.E(Loc.T("Remove it"))}</button> <a class="btn secondary" href="/instances">{H.E(Loc.T("Cancel"))}</a></div>
                  </form>
                </div>
                """);
        });

        app.MapPost("/instances/{name}/delete", async (HttpContext ctx, SettingsStore store, LiveConfig live, IHistoryStore history, string name) =>
        {
            await store.DeleteInstanceAsync(name, ctx.RequestAborted);
            await AlertMutes.ClearForInstanceAsync(history, name, ctx.RequestAborted);
            await live.RefreshAsync(ctx.RequestAborted);
            return Results.Redirect("/instances?msg=deleted");
        });

        app.MapPost("/instances/{name}/test", async (HttpContext ctx, SettingsStore store, string name) =>
        {
            var connectionString = await store.GetConnectionStringAsync(name, ctx.RequestAborted);
            if (connectionString is null) return NotFound(ctx);
            return SqlLookoutWeb.Show(ctx, Loc.T("Test: {0}", name), await TestAsync(name, connectionString, ctx.RequestAborted));
        });
    }

    private static (InstanceRecord Record, string? Password) ReadForm(IFormCollection form, string name) =>
        (new InstanceRecord(
            name.Trim(),
            form["server"].ToString().Trim(),
            form["auth"].ToString(),
            form["user"].ToString().Trim(),
            form["encrypt"].ToString(),
            form["enabled"].ToString() == "1",
            false,
            form["ignored"].ToString(),
            form["overrides"].ToString()),
         form["password"].ToString());

    private static IResult NotFound(HttpContext ctx) =>
        SqlLookoutWeb.Show(ctx, Loc.T("Not found"),
            $"<h1>{H.E(Loc.T("Not found"))}</h1><p>{H.E(Loc.T("There is no such server."))} <a href=\"/instances\">{H.E(Loc.T("Back to the list"))}</a>.</p>", status: 404);

    private static string FormPage(HttpContext ctx, string? error, bool isNew, InstanceRecord r)
    {
        var action = isNew ? "/instances/new" : $"/instances/{Uri.EscapeDataString(r.Name)}/edit";
        var passwordHelp = isNew || !r.HasPassword
            ? Loc.T("Only needed for a SQL login. It is stored encrypted and is never shown again.")
            : Loc.T("Leave blank to keep the saved password.");
        return $"""
            <h1>{(isNew ? H.E(Loc.T("Add a server")) : H.E(Loc.T("Edit {0}", r.Name)))}</h1>
            {H.Notice(error)}
            <form method="post" action="{action}" class="card">
              {H.Token(SqlLookoutWeb.Token(ctx))}
              <div class="grid2">
                <div>
                  {(isNew
                      ? H.Field(Loc.T("Name"), "name", r.Name, required: true, help: Loc.T("A short label, for example PROD-01. It cannot be changed later because the history is stored under it."))
                      : $"<div class=\"field\"><label>{H.E(Loc.T("Name"))}</label><div>{H.E(r.Name)}</div></div>")}
                  {H.Field(Loc.T("Server"), "server", r.Server, required: true, help: Loc.T("Server name, server\\instance, or address,port. Example: PROD01 or PROD01\\SQL2008 or 10.0.0.5,1433"))}
                  {H.Select(Loc.T("Log in with"), "auth",
                      [(AuthModes.Windows, Loc.T("Windows account the SqlLookout service runs as")), (AuthModes.Sql, Loc.T("SQL login (user name and password)"))],
                      r.Auth, Loc.T("Use the read-only login made by sql/create-readonly-login.sql."))}
                </div>
                <div>
                  {H.Field(Loc.T("SQL login name"), "user", r.UserName, help: Loc.T("Only for a SQL login."))}
                  {H.Field(Loc.T("SQL login password"), "password", null, "password", passwordHelp, extra: "autocomplete=\"new-password\"")}
                  {H.Select(Loc.T("Encryption"), "encrypt",
                      [(EncryptModes.Trust, Loc.T("Encrypt, accept the server's certificate (usual choice)")),
                       (EncryptModes.Verify, Loc.T("Encrypt and verify the certificate")),
                       (EncryptModes.Off, Loc.T("No encryption (SQL Server 2008 and 2008 R2)"))],
                      r.Encrypt, Loc.T("If a server cannot be reached and it is an old SQL Server 2008, try \"No encryption\"."))}
                  {H.Check(Loc.T("Watch this server"), "enabled", r.Enabled, Loc.T("Untick to pause monitoring without deleting it."))}
                </div>
              </div>
              {H.TextArea(Loc.T("Databases to ignore"), "ignored", r.IgnoredDatabases, Loc.T("One name per line; * stands for any text, for example SqlLookoutAge_*. These databases are left out of the backup, CHECKDB, log, state, suspect-page, log shipping, mirroring and availability group database alerts. They still show on the Databases tab."))}
              {H.TextArea(Loc.T("Thresholds for this server only"), "overrides", r.ThresholdOverrides, Loc.T("One per line, like DiskWarnFreePercent = 15. Names are the ones in the config file (see the README). Anything not listed uses the common value from Settings."))}
              <div class="actions"><button type="submit">{H.E(isNew ? Loc.T("Add server") : Loc.T("Save"))}</button> <a class="btn secondary" href="/instances">{H.E(Loc.T("Cancel"))}</a></div>
            </form>
            """;
    }

    private static async Task<string> TestAsync(string name, string connectionString, CancellationToken ct)
    {
        var sb = new StringBuilder($"<h1>{H.E(Loc.T("Test: {0}", name))}</h1>");
        try
        {
            // Fail within about 10 seconds even when the address silently drops packets.
            var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 8, ConnectRetryCount = 0, ApplicationName = "SqlLookout" };
            await using var connection = new SqlConnection(builder.ConnectionString);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            await connection.OpenAsync(limit.Token);
            var server = await ServerInfo.ReadAsync(connection, ct);

            sb.Append($"""<p class="notice ok">{H.E(Loc.T("Connected. SQL Server {0}.{1}.{2}{3}.", server.Major, server.Minor, server.Build, server.IsExpress ? " (Express)" : ""))}</p>""");
            sb.Append($"""<div class="card tablewrap"><table><thead><tr><th>{H.E(Loc.T("Check"))}</th><th>{H.E(Loc.T("Result"))}</th><th>{H.E(Loc.T("Rows"))}</th><th>{H.E(Loc.T("Detail"))}</th></tr></thead><tbody>""");
            foreach (var collector in CollectorCatalog.All())
            {
                var result = await CollectorRunner.RunAsync(collector, connection, server, ct);
                var (kind, text) = result.Status switch
                {
                    CollectionStatus.Ok => ("ok", Loc.T("works")),
                    CollectionStatus.Unsupported => ("off", Loc.T("not available here")),
                    CollectionStatus.NoPermission => ("bad", Loc.T("no permission")),
                    _ => ("bad", Loc.T("failed")),
                };
                var detail = result.Status switch
                {
                    CollectionStatus.NoPermission => Loc.T("{0}. Needs: {1}. Run sql/create-readonly-login.sql on this server.", result.Message, Needs(collector.Name)),
                    CollectionStatus.Unsupported => Loc.T(result.Message ?? ""),
                    _ => result.Message ?? "",
                };
                sb.Append($"<tr><td>{H.E(collector.Name)}</td><td>{DashboardPage.Badge(kind, text)}</td><td class=\"num\">{result.Samples.Count}</td><td>{H.E(detail)}</td></tr>");
            }
            sb.Append("</tbody></table></div>");
        }
        catch (SqlException ex)
        {
            // Only the error number is shown: the text can repeat parts of the connection string.
            sb.Append($"""<p class="notice bad">{H.E(Loc.T("Could not connect (SQL error {0}).", ex.Number))}</p><p>{H.E(HintFor(ex.Number))}</p>""");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sb.Append($"""<p class="notice bad">{H.E(Loc.T("Could not connect: no answer within 10 seconds."))}</p><p>{H.E(HintFor(258))}</p>""");
        }
        catch (InvalidOperationException)
        {
            sb.Append($"""<p class="notice bad">{H.E(Loc.T("Could not connect."))}</p><p>{H.E(Loc.T("Check the server name."))}</p>""");
        }
        sb.Append($"""<div class="actions"><a class="btn secondary" href="/instances">{H.E(Loc.T("Back to the list"))}</a></div>""");
        return sb.ToString();
    }

    /// <summary>The permission each check reads with, so "no permission" says what to grant. These are SQL Server object names and stay in English.</summary>
    private static string Needs(string collector) => collector switch
    {
        "backup_age" => "SELECT on msdb.dbo.backupset",
        "suspect_pages" => "SELECT on msdb.dbo.suspect_pages",
        "agent_jobs" => "SELECT on msdb.dbo.sysjobs, sysjobservers, sysjobhistory, sysjobactivity, syssessions",
        "disk_space" or "log_usage" or "cpu" or "memory" or "connections" or "blocking" or "deadlocks" or "io_latency" or "wait_stats" or "tempdb" => "VIEW SERVER STATE",
        "database_state" or "file_size" or "express_limit" => "VIEW ANY DEFINITION",
        "dbcc_last_good" => "CONNECT ANY DATABASE (SQL Server 2014+)",
        "availability_groups" => "VIEW SERVER STATE and VIEW ANY DEFINITION",
        "log_shipping" => "SELECT on msdb.dbo.log_shipping_monitor_primary and log_shipping_monitor_secondary",
        "mirroring" => "VIEW ANY DEFINITION",
        "failed_logins" => "sysadmin, or EXECUTE on master.dbo.xp_readerrorlog",
        _ => "read access",
    };

    private static string HintFor(int number) => number switch
    {
        -2146893019 or -2146893048 => Loc.T("The server's certificate is not trusted by this machine. Edit the server and choose \"Encrypt, accept the server's certificate\"."),
        258 => Loc.T("The server did not answer in time. The address may be wrong, or a firewall is dropping the connection."),
        64 => Loc.T("The connection was dropped while connecting. Check the encryption option; for an old SQL Server 2008 choose \"No encryption\"."),
        18456 => Loc.T("The server refused the login. Check the user name and password, and that the login exists on that server."),
        4060 => Loc.T("The login cannot open the database. Make sure it is enabled and has access to master."),
        53 or -1 or 2 or 1231 or 10060 or 10061 => Loc.T("The server did not answer. Check the name or address, the port, that the server is running, and the firewall."),
        _ => Loc.T("If this is an old SQL Server (2008 or 2008 R2), edit the server and choose \"No encryption\". Otherwise check the name and the login."),
    };
}
