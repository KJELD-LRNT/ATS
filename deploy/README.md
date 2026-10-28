# Deployment artifacts and configuration reference

Operational procedures are in [`docs/runbook.md`](../docs/runbook.md). This folder holds the files that procedure uses.

| File | Purpose |
| --- | --- |
| `web.config` | IIS in-process hosting, Windows Authentication **and** anonymous authentication on for the whole site (needed for the super admin page `/superadmin`; the app still requires sign-in on every other page except `/health*`). `build/publish.ps1` copies it into the web publish folder. |
| `Set-SuperAdmin.ps1` | Sets, generates, unlocks, disables or shows the super admin account (the one account outside AD) through a one-off scheduled task under the worker's gMSA. See [runbook §15](../docs/runbook.md#15-super-admin-account-outside-ad). |
| `web.appsettings.Production.template.json` | Copy to `<web folder>\appsettings.Production.json` and replace every `<PLACEHOLDER>`. |
| `worker.appsettings.Production.template.json` | Copy to `<worker folder>\appsettings.Production.json` and replace every `<PLACEHOLDER>`. |
| `Install-Worker.ps1` | Creates/updates the `AssetTrackerWorker` Windows service under a gMSA with restart-on-failure recovery. |
| `Apply-Migrations.ps1` | Applies EF Core migrations with the `efbundle.exe` migrations bundle, as the separate migration account. |
| `Quarantine-Outbox-After-Restore.sql` | After a database restore: marks queued mail created before a chosen cut-off as `Skipped`, so a restore does not resend old notifications. |

**Values:** every `<PLACEHOLDER>` comes from Stage 0 client preflight (`docs/mvp-plan.md` §7). Nothing in this repository
holds real hostnames, SIDs, addresses or credentials. Do not commit filled-in copies. Store the filled-in files with the
server configuration backup (runbook, "Backup and restore").

**Secrets:** the connection strings use Windows integrated security (the app pool / service gMSA), so they contain no
password. The SMTP password (only for an authenticated relay) is entered on **Admin → Settings**, which stores it
encrypted with ASP.NET Core Data Protection; it is never shown again, logged or written to the audit log. The key ring
lives in `DataProtection:KeyPath` (see below) and must be backed up with the database.

## Where settings live

Since WP4.1 most settings are edited in the app (**Admin → Settings**, admins only) and stored in the database
(`AppSettings` table, one row per configuration key). Database values override the files; an empty field on the
page removes the override, so the file value or built-in default applies again. The page shows each setting's
effective value and its source (database / config file / default). Changes apply without a restart: immediately in
the web app, and in the worker within `Settings:ReloadInterval`. The files keep only bootstrap settings, which the
app needs before it can read the database or which must not be changeable from inside the app (connection string,
Data Protection key folder). **Roles are not configured in a file:** an admin (or the super admin) gives them to people
on **Admin → System roles**. The old `Roles:ItGroupSids` / `Roles:ManagerGroupSids` / `Roles:AdminGroupSids` keys are
ignored; if the file still has them, the web app logs a warning at start-up and Admin → Settings shows a notice. Remove
them.

| Setting | Where | App | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `ConnectionStrings:AssetTracker` | File | Both | Yes | none | SQL Server connection. Integrated security as the app pool / worker gMSA. `Encrypt=True`; add `TrustServerCertificate=True` only if the SQL certificate cannot be trusted. |
| `AllowedHosts` | File | Web | Recommended | `*` | The internal DNS name(s) of the site, `;`-separated. Rejects requests with other `Host` headers. |
| `DataProtection:KeyPath` | File | Both | Yes (Production) | none | Folder holding the Data Protection key ring, **the same folder for web and worker** (e.g. `D:\AppData\AssetTracker\DataProtection-Keys`, outside the web root). Keys are encrypted with machine DPAPI, so the folder only works on this server. Both service accounts need Modify on it. Startup fails in Production when it is not set. |
| `Settings:ReloadInterval` | File | Both | No | `00:00:30` | How often each app re-reads database settings. `00:00:00` disables periodic reloads (the web app still reloads after each save). |
| `Ops:WorkerHeartbeatInterval` | File | Web | No | `00:01:00` | Must equal the worker's `Worker:HeartbeatInterval`. |
| `Ops:WorkerStaleAfterIntervals` | File | Web | No | `3` | Worker reported stale after this many missed heartbeats. |
| `Ops:OutboxPendingMaxAge` | File | Web | No | `00:30:00` | Pending mail older than this makes `/health/ready` Degraded. |
| `Worker:HeartbeatInterval` | File | Worker | No | `00:01:00` | How often the worker records its heartbeat. |
| `Logging:LogLevel:*` | File | Both | No | Information | Logs go to the Windows Event Log (worker, source `AssetTracker Worker`) and the ASP.NET Core Module / Event Log (web). |
| `Clock:TimeZoneId` | App | Both | Yes | none | Default time zone (Windows or IANA id, e.g. `W. Europe Standard Time`) for every site without its own time zone (Admin → Sites). Drives due dates and the overdue rule. Set it first. |
| `ActiveDirectory:SearchRoot` | App | Both | Yes | none | LDAP path of the in-scope OU (`LDAP://...` or `GC://...`). |
| `ActiveDirectory:SiteAttribute` | App | Both | Yes | none | AD attribute holding the person's site (mapped via Admin → Sites "directory value"). |
| `ActiveDirectory:Filter`, `PageSize`, `ClientTimeout`, `IdAttribute`, `UsernameAttribute`, `DisplayNameAttribute`, `TitleAttribute`, `MailAttribute`, `SidAttribute`, `UserAccountControlAttribute` | App | Both | No | standard AD | Override only if Stage 0 requires it (see `docs/development.md`). Page size 1–1000. |
| `DirectorySync:Interval` | App | Worker | Yes | none | Full AD sync schedule. Unset ⇒ no scheduled sync (stale warning appears); the worker starts syncing within a minute of it being set. |
| `DirectorySync:MinimumExpectedCount` | App | Both | Recommended | `0` (off) | Reject a run with fewer valid entries (set from the directory size). |
| `DirectorySync:MaxDropFraction` | App | Both | No | `0.2` | Reject a run that would deactivate more than this fraction (0–1) of active people. |
| `DirectorySync:StaleAfter` | App | Both | No | `1.00:00:00` | People data is "stale" (IT banner, checkout audit, `/health/ready` Degraded) after this. Set to ~2-3× the interval. |
| `DirectorySync:RunningTimeout` | App | Both | No | `01:00:00` | A run stuck in `Running` longer than this no longer blocks a new run. |
| `Mail:Host` / `Port` | App | Worker | Yes / No | none / `25` | Approved internal relay. Port 1–65535. |
| `Mail:SecureSocket` | App | Worker | No | `StartTls` | `StartTls` (required TLS), `SslOnConnect` (port 465) or `None` (only if Stage 0 confirms no TLS). |
| `Mail:Username` / `Password` | App | Worker | No | empty | Only for an authenticated relay. The password is write-only and stored encrypted. |
| `Mail:FromAddress` / `FromName` | App | Worker | Yes / No | none / `Asset Tracker` | Approved sending identity. |
| `Mail:Timeout` | App | Worker | No | `00:00:30` | SMTP timeout. |
| `Notifications:IncludePhoneNumberInMail` | App | Both | No | `true` | Whether mail shows a phone asset's number (sponsor decision D5). |
| `Notifications:ReminderTimeOfDay` | App | Worker | No | `08:00` | Time of day at which overdue reminders are queued, in each site's local time. |
| `Notifications:ReminderPollInterval` | App | Worker | No | `00:15:00` | How often the worker checks whether a site's reminder time has been reached. |
| `Notifications:MaxAttempts` | App | Worker | No | `8` | Send attempts (1–50) before a mail becomes `Failed`. |
| `Notifications:InitialRetryDelay` / `MaxRetryDelay` | App | Worker | No | `00:01:00` / `01:00:00` | Exponential retry backoff. |
| `Notifications:ClaimLease` | App | Worker | No | `00:05:00` | How long a claimed row is reserved; must exceed `Mail:Timeout`. |
| `Notifications:PollInterval` / `BatchSize` | App | Worker | No | `00:00:30` / `50` | Outbox polling. |
| IT mailbox | App | Both | Yes | none | Notifications section of the Settings page (stored as `Mail.ItMailbox`). |

"App" settings can still be put in `appsettings.Production.json` (same key); the database value wins when both exist.
The database never overrides keys outside the `ActiveDirectory`, `DirectorySync`, `Mail`, `Notifications` and
`Clock` sections. The Settings page also has **Test directory query** (read-only preview of what a sync would see)
and **Send test email** (sends directly through the relay), both audited.

Environment: IIS sets `ASPNETCORE_ENVIRONMENT=Production` via `web.config`; the worker defaults to `Production`,
so both load `appsettings.Production.json` next to the executable.

## Super admin account

The app has one account outside AD, `superadmin`, for running it without a domain account and for repairing the AD and
mail settings when they break (sponsor decision D12). It has no password after installation: set one with
`Set-SuperAdmin.ps1` on the server ([runbook §15](../docs/runbook.md#15-super-admin-account-outside-ad)). It needs no
configuration keys. What it needs on the server:

- **IIS:** anonymous authentication enabled for the whole site next to Windows authentication (`web.config` does this;
  the `anonymousAuthentication` section must be unlocked, runbook §2). Do the IIS smoke test in runbook §15.2.
- **The worker's gMSA:** *Log on as a batch job*, because `Set-SuperAdmin.ps1` runs `AssetTracker.Worker.exe superadmin
  apply` as a one-off scheduled task under it.
- **Request folder:** created by the script (default `SuperAdmin-Requests` next to the `DataProtection:KeyPath`
  folder, else `%ProgramData%\AssetTracker\SuperAdmin-Requests`), ACL Administrators + the gMSA only.

The cookie is `__Host-ats-superadmin` (HTTPS only). Sessions end after 30 minutes idle and 8 hours after sign-in.
