# Deployment artifacts and configuration reference

Operational procedures are in [`docs/runbook.md`](../docs/runbook.md). This folder holds the files that procedure uses.

| File | Purpose |
| --- | --- |
| `web.config` | IIS in-process hosting, Windows Authentication on, anonymous off except `/health*`. `build/publish.ps1` copies it into the web publish folder. |
| `web.appsettings.Production.template.json` | Copy to `<web folder>\appsettings.Production.json` and replace every `<PLACEHOLDER>`. |
| `worker.appsettings.Production.template.json` | Copy to `<worker folder>\appsettings.Production.json` and replace every `<PLACEHOLDER>`. |
| `Install-Worker.ps1` | Creates/updates the `AssetTrackerWorker` Windows service under a gMSA with restart-on-failure recovery. |
| `Apply-Migrations.ps1` | Applies EF Core migrations with the `efbundle.exe` migrations bundle, as the separate migration account. |
| `Quarantine-Outbox-After-Restore.sql` | After a database restore: marks queued mail created before a chosen cut-off as `Skipped`, so a restore does not resend old notifications. |

**Values:** every `<PLACEHOLDER>` comes from Stage 0 client preflight (`docs/mvp-plan.md` §7). Nothing in this repository
holds real hostnames, SIDs, addresses or credentials. Do not commit filled-in copies. Store the filled-in files with the
server configuration backup (runbook, "Backup and restore").

**Secrets:** the connection strings use Windows integrated security (the app pool / service gMSA), so they contain no
password. `Mail:Username`/`Mail:Password` stay empty for an IP-restricted relay. If the client's relay does need
credentials, restrict the file's NTFS ACL to Administrators and the worker gMSA (or use the client's secret store);
never put them in source control.

JSON has no comments, so every setting is described below. "Both" means the same value in the web and worker files.

## Settings

| Setting | App | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `ConnectionStrings:AssetTracker` | Both | Yes | none | SQL Server connection. Integrated security as the app pool / worker gMSA. `Encrypt=True`; add `TrustServerCertificate=True` only if the SQL certificate cannot be trusted. |
| `Clock:TimeZoneId` | Both | Yes | none | Client timezone (Windows or IANA id, e.g. `W. Europe Standard Time`). Drives due dates and the daily overdue rule. |
| `AllowedHosts` | Web | Recommended | `*` | The internal DNS name(s) of the site, `;`-separated. Rejects requests with other `Host` headers. |
| `Roles:ItGroupSids` | Web | Yes | `[]` | SIDs of the AD group(s) for IT/editor. |
| `Roles:ManagerGroupSids` | Web | Yes | `[]` | SIDs of the AD group(s) for managers/viewers (site access via grants). |
| `Roles:AdminGroupSids` | Web | Yes | `[]` | SIDs of the AD group(s) for application admins (settings, grants, sync rerun, privacy). |
| `Notifications:IncludePhoneNumberInMail` | Both | No | `false` | Whether mail may contain a phone asset's number (Stage 0, §7.4). Same value in both. |
| `Notifications:MaxAttempts` | Worker | No | `8` | Send attempts before a mail becomes `Failed`. |
| `Notifications:InitialRetryDelay` / `MaxRetryDelay` | Worker | No | `00:01:00` / `01:00:00` | Exponential retry backoff. |
| `Notifications:ClaimLease` | Worker | No | `00:05:00` | How long a claimed row is reserved; must exceed `Mail:Timeout`. |
| `Notifications:PollInterval` / `BatchSize` | Worker | No | `00:00:30` / `50` | Outbox polling. |
| `Notifications:ReminderTimeOfDay` | Worker | No | `08:00` | Client-local time for the daily overdue reminder run. |
| `Mail:Host` / `Port` | Worker | Yes | none / `25` | Approved internal relay. |
| `Mail:SecureSocket` | Worker | No | `StartTls` | `StartTls` (required TLS), `SslOnConnect` (port 465) or `None` (only if Stage 0 confirms no TLS). |
| `Mail:Username` / `Password` | Worker | No | empty | Only for an authenticated relay. |
| `Mail:FromAddress` / `FromName` | Worker | Yes / No | none / `Asset Tracker` | Approved sending identity. |
| `Mail:Timeout` | Worker | No | `00:00:30` | SMTP timeout. |
| `DirectorySync:Interval` | Worker | Yes | none | Full AD sync schedule. Unset ⇒ no scheduled sync (stale warning appears). |
| `DirectorySync:MinimumExpectedCount` | Both | Recommended | `0` (off) | Reject a run with fewer valid entries (set from the directory size). Both apps run syncs (worker schedule, admin rerun in web). |
| `DirectorySync:MaxDropFraction` | Both | No | `0.2` | Reject a run that would deactivate more than this fraction of active people. |
| `DirectorySync:StaleAfter` | Both | No | `1.00:00:00` | People data is "stale" (IT banner, checkout audit, `/health/ready` Degraded) after this. Set to ~2-3× the interval. |
| `DirectorySync:RunningTimeout` | Both | No | `01:00:00` | A run stuck in `Running` longer than this no longer blocks a new run. |
| `ActiveDirectory:SearchRoot` | Both | Yes | none | LDAP path of the in-scope OU. |
| `ActiveDirectory:SiteAttribute` | Both | Yes | none | AD attribute holding the person's site (mapped via Admin → Sites "directory value"). |
| `ActiveDirectory:Filter`, `PageSize`, `ClientTimeout`, `IdAttribute`, `UsernameAttribute`, `DisplayNameAttribute`, `TitleAttribute`, `MailAttribute`, `SidAttribute`, `UserAccountControlAttribute` | Both | No | standard AD | Override only if Stage 0 requires it (see `docs/development.md`). |
| `Ops:WorkerHeartbeatInterval` | Web | No | `00:01:00` | Must equal the worker's `Worker:HeartbeatInterval`. |
| `Ops:WorkerStaleAfterIntervals` | Web | No | `3` | Worker reported stale after this many missed heartbeats. |
| `Ops:OutboxPendingMaxAge` | Web | No | `00:30:00` | Pending mail older than this makes `/health/ready` Degraded. |
| `Worker:HeartbeatInterval` | Worker | No | `00:01:00` | How often the worker records its heartbeat. |
| `Logging:LogLevel:*` | Both | No | Information | Logs go to the Windows Event Log (worker, source `AssetTracker Worker`) and the ASP.NET Core Module / Event Log (web). |

Environment: IIS sets `ASPNETCORE_ENVIRONMENT=Production` via `web.config`; the worker defaults to `Production`,
so both load `appsettings.Production.json` next to the executable.
