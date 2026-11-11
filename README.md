# SqlLookout

English | [繁體中文](README.zh-TW.md)

Website: https://caiderek.github.io/SqlLookout/

A SQL Server monitoring tool. Work in progress: version 0.9.x, expect rough edges.

Search terms: SQL Server monitoring, wait stats, blocking, deadlock, slow queries, backup check, DBCC CHECKDB, Always On, log shipping, disk space alert, DBA tool, MCP server.

It connects to your SQL Server instances from one machine, with a read-only login, and installs nothing on them.
You set it up in a web page. It keeps history in a local SQLite file and sends email (and, if you want, a Teams message) when something needs attention.

SQL Server is a trademark of Microsoft Corporation. This project is not affiliated with or endorsed by Microsoft.

## Quick start (web interface)

1. On each SQL Server, run `sql/create-readonly-login.sql` as a sysadmin (edit the name and password at the top).
   The login can read metadata and statistics only: no table data, no writes.
2. On the monitoring machine, unzip and run it to try it out (to keep it running as a service, see "Run it as a Windows service" below):

```
sqllookout.exe web
```

3. Open `https://localhost:8443/` **on that machine**. Your browser will warn about the certificate (it is
   self-signed and made on first start); continue anyway, or use your own with `--cert`. Create the administrator
   account (this step only works from the machine itself, so nobody else on the network can claim it first).
4. **Servers > Add a server**, fill in the name and login, press **Test**. Every check shows "works", or tells you
   what permission is missing. Data appears on the **Dashboard** within a minute.
5. **Settings**: email server and recipients, Teams, quiet hours, daily summary, limits. Changes apply immediately.

**Language:** the pages and the alert emails come in English and Traditional Chinese (繁體中文). Pick one on the
first-time setup page, switch any time with the button at the top right of every page, or in Settings > General.
(Headless mode: `"language": "zh-TW"` in the JSON file.)

Options: `--port 8443`, `--listen 0.0.0.0` (use `127.0.0.1` to allow only this machine), `--data <folder>`
(history and settings; default `data` next to the program), `--cert <pfx> --cert-password-file <file>`.

### Run it as a Windows service

**With a wizard:** double-click `Setup.cmd` in the unzipped folder (or run `sqllookout.exe setup`). A setup page opens in
your browser: pick the folders, the port, who may open the page, the service account and where to keep the history
(a file, or a SQL Server database with a "Test the connection" button), then press **Install**. Windows asks for
administrator permission at that moment; the wizard itself does not run as administrator and only listens on this
computer, under an address that only the tab it opened knows. When SqlLookout is already installed the same page offers
**Upgrade** (from the newer folder you unzipped) and **Uninstall**. It is in English and Traditional Chinese. It runs the
commands below, so everything they do is what the wizard does.

**On the command line:** `sqllookout.exe web` stops when you close the window or sign out. To keep it running, open a
command prompt **as administrator** in the unzipped folder and run:

```
sqllookout.exe install
```

It copies the program to `C:\Program Files\SqlLookout`, keeps the data in `C:\ProgramData\SqlLookout` (readable by
Administrators, SYSTEM and the service account only, because the stored passwords can be decrypted by anyone who can
read that folder), creates a service that starts with Windows and restarts after a crash, opens the firewall port, and
waits until the page answers. Options: `--dir`, `--data`, `--port`, `--listen` (with `127.0.0.1` no firewall rule is
added), `--cert` and `--cert-password-file`, `--no-firewall`.

Already running it from a window? Pass the folder it uses so nothing is lost: `sqllookout.exe install --data <that data folder>`.

With a Windows-authentication server, the service signs in as the account it runs as: LocalSystem by default, which
SQL Server sees as the computer account (`DOMAIN\MACHINE$`). To use a domain account instead add
`--account DOMAIN\name --password-file <file with the password on its first line>` (a managed service account, whose
name ends with `$`, needs no password file). That account needs the "Log on as a service" right.

**Upgrade:** unzip the new version anywhere, open an administrator prompt in that folder and run
`sqllookout.exe upgrade`. It stops the service, saves the old program files to `C:\Program Files\SqlLookout.prev`,
copies the new ones, starts the service and checks the page answers. The data folder is not touched. If the new
version does not start or answer, the old files are put back and started again. A build older than the installed one is
refused.

**Uninstall:** `sqllookout.exe uninstall` removes the service and the firewall rule and keeps the data;
add `--delete-data` to remove that too. The program folder is left for you to delete.

### Keep the history and settings in SQL Server (optional)

By default the history, the alerts, the server list and the sign-in accounts live in a SQLite file in the data folder.
To keep them in a SQL Server database instead, run `sql/create-store-database.sql` once on the server that should hold
them (it creates a database of its own and a login with `db_owner` on it), put the connection string on the first line of
a text file, and start with it:

```
sqllookout.exe install --store-connection-file C:\temp\store.txt
```

`install` connects, checks that the login may create tables, refuses a system database such as `master`, and saves the
connection string protected with Windows DPAPI in the data folder; you can delete the plain file afterwards. Without
`install`, `sqllookout.exe web --store-connection-file <file>` does the same for a window you keep open, and
`reset-password` and `mcp` take the same option. The tables are created in that database the first time it starts.

* **Not moved over.** Switching does not copy what is already in the SQLite file: the new database starts empty, so you
  add the servers and the administrator again and the history begins from that day. The old file stays where it is.
* **One SqlLookout per database.** Two programs writing into the same database would raise every alert twice. The saved
  SQL passwords are encrypted for the machine that saved them, so moving the service to another computer means entering
  them again.
* **Where.** Prefer a server that is not itself one of the monitored ones, or you lose the history exactly when that server fails.
* **Still in the data folder:** the certificate, the sign-in keys and the diagnostic log.
* **Headless mode** (`run`, `run-once`, `status` with a JSON file) keeps using SQLite.
* The program checks the connection before each use and opens it again after a network break or a server restart.
* The MCP server opened with a store file only reads: the program refuses every write on that connection. For a second
  layer give its login only `db_datareader`.

## Reading the pages

* **Dashboard**: one line per server, worst first (unreachable, critical, warning, healthy, paused), with the problem
  counts, a six-hour CPU line, the lowest disk free %, the oldest full backup and the number of failed jobs. Under the
  table is one short list of all current problems (the first 20; the rest are on the servers' own pages).
* **A server's page** (click its name): tabs for **Problems**, **Databases** (the ones that need a look first),
  **Disks** (table and a free-space chart), **SQL Agent jobs** (only failed, cancelled or running jobs until you press
  "Show all") and **Performance** (CPU, memory, page life expectancy, connections, blocking, disk I/O latency,
  deadlocks), and **Queries**. Charts cover 6 hours, 24 hours, 7 days or 30 days; short spikes are kept when a long range is thinned out.
  The Queries tab reads the server live when you open it (nothing is stored, the page does not refresh itself): the user queries running
  right now with their current statement, wait and blocker, and the 15 costliest statements in the plan cache. Query text can contain
  data, so it is shown only to signed-in users. Needs VIEW SERVER STATE.
* Charts are drawn on the server as plain SVG: the pages still contain no JavaScript.
* **Theme:** the button in the header cycles Auto (follows the browser or Windows), Light and Dark. The choice is kept
  in a cookie of that browser only, so each person can pick their own.
* **Look:** pick **Classic** (the original, and the default) or **Terminal** in Settings > General. It is one choice for the whole
  installation and applies at once, also on the sign-in page. Terminal is a dense, square-cornered monospace style; status is always
  shown with a word or a marker as well as a colour. It uses fonts already on the computer (IBM Plex Mono if installed, otherwise
  Consolas or the system monospace font), because the pages load nothing from the internet. An update never changes the look of an
  existing installation. (Headless mode: `"look": "terminal"` in the JSON file.)

## What it watches

| Check | Alerts when |
|---|---|
| Last full backup | older than 26 h (warning) / 48 h or none (critical); one alert per instance listing the databases, critical first (20 at most) |
| Last good DBCC CHECKDB | older than 8 days / 15 days, or never; one alert per instance, listed the same way |
| Suspect pages | any unrepaired page |
| Database state | SUSPECT, OFFLINE, EMERGENCY, RECOVERY_PENDING |
| Disk free space | under 20% or 10 GB (warning) / 10% or 5 GB (critical) |
| Disk fall rate | the recent trend fills the volume within 4 h (warning) / 1 h (critical) |
| Log usage | over 80% / 90% (logs under 100 MB are ignored); one alert per instance that lists the databases, worst first (20 at most) |
| File sizes | recorded for history, no alert |
| SQL Agent jobs | last run failed (critical), running over 2x its average (warning) |
| CPU | machine CPU at or above 90% for 10 minutes (warning) |
| Blocking | a session has been blocked 60 s or more (warning) / 300 s or more (critical); names the head blocker |
| Page life expectancy | below 300 s at every check for 10 minutes (warning) |
| Memory grants | at least one query waiting for a memory grant at every check for 5 minutes (warning) |
| Deadlocks | 3 or more in the last hour (warning) / 20 or more (critical) |
| Connections | user connections at or above 500 at every check for 5 minutes (warning) / 1500 (critical) |
| Disk I/O latency | instance-wide average read or write latency at or above 50 ms at every check for 15 minutes (warning) / 150 ms (critical) |
| Top waits | recorded for history and for the MCP server, no alert |
| Availability groups | a replica disconnected or not healthy, or a database whose data movement is suspended or not healthy (critical); partly healthy is a warning. One alert per instance listing what is wrong. Needs SQL Server 2012+ with Always On on; a secondary sees only itself, so also add the primary |
| Log shipping | the last log backup or restore is older than the limit set up in log shipping itself (warning) / 2x that limit (critical, `LogShippingCriticalMultiplier`); a job that never ran is a warning. Pairs whose alert is off in the setup are skipped. One alert per instance |
| Database mirroring | a mirrored database suspended or disconnected (critical), a disconnected witness (warning). One alert per instance |
| Express size cap | SQL Server Express only: a database whose data files reach 80% (warning) / 95% (critical) of the edition's cap (10 GB; 4 GB on 2008 and older; 50 GB on SQL Server 2025); log files and tempdb do not count. One alert per instance, listing the databases |
| tempdb space | tempdb data files 80% (warning) / 95% (critical) full, measured against the room they can reach (size now plus allowed growth, limited by max_size and free space on the volume). The alert shows user / internal / version-store use and the biggest sessions. Not judged when unlimited growth sits on a server too old to report volume space |
| Failed logins | 10 or more failed logins in the last 15 minutes (warning), 50 or more (critical), counted from the current SQL Server error log (error 18456). The alert lists the busiest logins and the client addresses they came from. Needs sysadmin or EXECUTE on `master.dbo.xp_readerrorlog`; shows nothing if the server does not audit failed logins |
| Reachability | 2 failed connections in a row (warning) / 3 (critical) |
| The collectors themselves | a check that fails or is denied 3 times in a row |

A problem that goes away is called resolved only after it has stayed away for 30 minutes (`ResolveAfterMinutes` in the JSON settings, 0 = at once), so a value that hovers around its limit does not send a new and a resolved mail on every swing.

Test and scratch databases nobody looks after can be left out: edit a server and list their names under "Databases to ignore" (one per line; `*` stands for any text, for example `SqlLookoutAge_*`). They are then skipped by the backup, CHECKDB, log, state, suspect-page, log shipping, mirroring and availability group database alerts and by the dashboard's oldest-backup column, and still shown on the Databases tab.

A server can have its own thresholds: edit the server and list them under "Thresholds for this server only", one per line, for example `DiskWarnFreePercent = 15` or `FailedLoginWarnCount = 100` (the names are those of the `thresholds` section of the config file, case does not matter; in the JSON config file use `"thresholdOverrides": { "DiskWarnFreePercent": 15 }` on the instance). Whatever is not listed uses the common value from Settings. The result must still fit together (a warning level below its critical level), otherwise saving is refused.

**Teams:** Settings > Teams alerts posts the same alerts to a Teams chat or channel, alone or next to email. In Teams, make a Workflows flow with the trigger "When a Teams webhook request is received" and the action "Post card in a chat or channel", and paste the address it gives you (an https address that contains a secret key, so it is stored encrypted and not shown again). The older incoming-webhook connector address works too. In Teams a chat needs at least two members before a flow can post into it. Press "Send a test Teams message" after saving. If email and Teams are both on and one of them fails, the other still tells you and the failure is written to the log; only when every channel fails is the problem tried again on the next check. Headless mode: `"teams": { "urlEnv": "NAME_OF_A_VARIABLE" }` or `"urlFile": "path"` under `alerts`.

A problem you already know about can be silenced from its row on the server's Problems tab: "Acknowledge until it is gone", or snooze it for 1 hour, 4 hours, 1 day or 7 days. While silenced it is not mailed (the dashboard marks it, and the daily summary still lists it with a note). It speaks up again when the snooze runs out, when it gets worse (a warning that turns critical), or when it is gone and comes back; "Turn alerts back on" ends the silence at once. A silenced problem that clears still sends the usual resolved mail.

Every limit can be changed on the Settings page, and each alert group can be switched off. SQL Server 2008 and
later; Express is supported (no Agent checks there).

A problem is mailed once, again after the repeat time while it stays active, immediately if it gets worse, and once
more when it clears. The daily summary mail doubles as the "is the service still alive" check: if it stops arriving,
the service is down.

## Security

* HTTPS only. No plain-HTTP port exists.
* Sign-in with a local account; passwords are stored as PBKDF2 hashes; 5 wrong attempts lock the account for 5 minutes
  (a locked account answers like an unknown user, so the page does not reveal which user names exist). Signing out or
  changing the password ends every older session, even one whose cookie was copied.
* A forgotten password is reset on the machine itself, not on the web page (there is no "forgot password" link): run `sqllookout reset-password --data <folder>` (add `--user <name>` if there is more than one account). It asks for the new password twice, or reads one line from standard input when piped. It signs everyone out and lifts a lockout, and it works while the web interface is running.
* SQL passwords, the SMTP password and the Teams webhook address are stored encrypted with Windows DPAPI (machine scope), never shown again.
  This protects the data file if it is copied to another machine. **On the monitoring machine itself, any local user who
  can read the data folder can decrypt them**, so restrict the folder (the `--data` folder, default `data` next to the
  program) to Administrators, SYSTEM and the account the service runs as. `sqllookout install` does this for the data folder.
* The keys that protect sign-in cookies and form tokens are kept in `data\keys` (also DPAPI-encrypted).
* Pages contain no JavaScript, and the content security policy forbids it. Forms carry anti-forgery tokens.
* Connection errors are shown as SQL error numbers only, never as text, because the text can repeat the connection string.
* The monitoring login is read-only. SqlLookout never writes to your SQL Servers.
* The remote MCP endpoint (`/mcp`) is off until a key is made, takes only `Authorization: Bearer` keys that expire, and is read-only; see the MCP section.

## Old servers (2008 / 2008 R2)

* If a server cannot be reached, edit it and choose **No encryption** (old servers often cannot negotiate modern TLS).
* The disk check needs 2008 R2 SP1 or later; on older builds it is skipped and shown as "not available", not as an error.
* The last good CHECKDB time is only readable by a sysadmin before 2016 SP2. With the read-only login those
  databases show "unknown" and raise no alert.

## Headless mode (no web page)

Settings in a JSON file instead (`sqllookout.example.json`):

```
sqllookout check-config    # validates the file and tests each connection
sqllookout run-once        # collects everything once, sends no mail, prints what it found
sqllookout status          # prints the stored results again, no collection
sqllookout run             # collects and alerts until stopped
```

A connection string with a password is refused in that file; use `connectionStringFile` or `connectionStringEnv`.

## MCP server (for AI assistants)

```
sqllookout mcp --data <folder>      # the same data folder as 'sqllookout web'
```

A read-only MCP server over stdio. It opens the history file in SQLite's read-only mode, so it cannot change anything
the monitor recorded and never connects to your SQL Servers. Tools: `list_instances`, `get_problems`, `get_overview`,
`get_metric_history`. Names in the answers (databases, jobs, logins) come from the monitored servers and are untrusted
text. Example client entry:

```json
{ "mcpServers": { "sqllookout": { "command": "C:\\SqlLookout\\sqllookout.exe", "args": ["mcp", "--data", "C:\\SqlLookout\\data"] } } }
```

The account that runs the client needs read access to the data folder; the monitor keeps running while it is used.

### From another computer (https, with a key)

`sqllookout web` also answers MCP at `https://<this machine>:<port>/mcp`, so an assistant on another computer can use it
without access to the data folder.

1. In the web interface open **Settings > MCP access keys** and make a key. Give it a name, a lifetime (30, 90 or 365
   days) and either all servers or the servers it may see. **The key is shown once**; only a hash is kept.
2. Add it to the client, for example in Claude Code:

   ```
   claude mcp add --transport http sqllookout https://monitor.example:8443/mcp --header "Authorization: Bearer slk_..."
   ```

* Until the first key exists, `/mcp` answers 404 as if it were not there. A missing, wrong, expired or revoked key all get the same 401.
  After 10 keys that do not exist from one address in 5 minutes (an IPv6 address counts as its /64 network), that address
  is refused for 5 minutes; a restart clears this. Requests with no header, or with an expired or revoked key, are not
  counted, so they cannot lock out other people behind the same address. While an address is refused, everything from it
  gets 429, a valid key included, so someone who can send 10 bad keys from your network can block that address for 5
  minutes. A valid key from another address is never limited.
* The remote endpoint has the four tools above plus two that **connect to the monitored server when called** and need
  `VIEW SERVER STATE` (the read-only login from `sql/create-readonly-login.sql` has it):
  `get_running_queries` (queries running now and the 15 costliest cached statements) and `get_blocking` (who blocks whom,
  including a head blocker that is idle with an open transaction). They run SELECTs only and store nothing. To protect the
  monitored server, only one live read per server runs at a time (a second caller is told the server is busy after 2
  seconds), and an answer is reused for 3 seconds, however many keys ask. The costliest statements come from the plan cache
  of the whole SQL Server instance, so they can include statements of databases that are not your concern: a key limited to
  one server still sees every database on it.
* A key limited to some servers cannot see the others in any tool, and the error for a server it may not see is the same
  as for one that does not exist. Deleted servers disappear from the answers at once.
* **A key can read monitoring data and the text of running queries, which can contain data.** Give each person or tool its
  own key, keep keys out of shared files, and revoke one when it is no longer needed.
* The stdio server above does not use keys and never connects to a SQL Server; it only has the four history tools.
* The default certificate is self-signed, so a client that checks certificates refuses it. Use `--cert` with a certificate
  your computers already trust (for example one from your company's CA, issued for this machine's name). The self-signed
  certificate is made for `localhost`, the machine name and its full DNS name, so connect with one of those. Claude Code refuses the self-signed certificate, so give the endpoint a certificate from a CA your machines trust.


### Links that open Claude Code

Turn on **Add Claude Code links** in Settings > Alerts to put an "Investigate with Claude Code" link on each problem on the
Problems page. A click opens Claude Code in a new terminal window with a question already typed in; nothing
is sent until the person presses Enter.

* The question is fixed text with only the server name and the kind of problem filled in. Titles, details, login names and
  SQL text of a problem are never put into it.
* The person who clicks needs Claude Code and the SqlLookout MCP (above) on their own computer, and must have sent at least
  one message in Claude Code there so its link handler is registered.
* The links are only on the Problems page; mails do not carry them.

License: MIT
