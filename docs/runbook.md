# Operations runbook

Audience: the client's Windows/IIS/SQL administrators and the application's IT owners. Scope: install, configure,
monitor, release, roll back, back up/restore and troubleshoot the Asset Tracker on one domain-joined Windows Server.

> **Placeholders.** `<INTERNAL-DNS-NAME>`, `<SQL-HOST>`, `DOMAIN\gmsa-ats$`, `<client alert owner>` and similar are
> filled in from Stage 0 client preflight (`docs/mvp-plan.md` §7). This document invents no client values, recovery
> targets or owners. Fill in the table below before go-live.

| Role / value | Who / what |
| --- | --- |
| Service owner (application) | `<SERVICE-OWNER>` |
| Client alert owner (receives monitoring alerts) | `<client alert owner>` |
| Windows/IIS patching | `<WINDOWS-PATCH-OWNER>` |
| SQL Server patching and backups | `<SQL-OWNER>` |
| Restore owner, RPO / RTO | `<RESTORE-OWNER>`, `<RPO>`, `<RTO>` |
| Application support contact | `<SUPPORT-CONTACT>` |
| Privacy decision owner / deletion instruction authority | see [`privacy-and-retention.md`](privacy-and-retention.md) |

## 1. Components

| Component | Runs as | Notes |
| --- | --- | --- |
| IIS site `AssetTracker` (ASP.NET Core in-process) | App pool identity `DOMAIN\gmsa-ats$` (gMSA) | Windows Authentication plus anonymous access site-wide; the app requires sign-in everywhere except `/superadmin` and `/health*` (§15). |
| Windows service `AssetTrackerWorker` | `DOMAIN\gmsa-ats$` (may be a separate gMSA) | AD sync, mail outbox sender, per-site daily overdue reminders, heartbeat. |
| SQL Server Express database `AssetTracker` | SQL service account | No SQL Agent: all scheduling is in the worker. |
| Internal mail relay | client | Worker submits mail; relay forwards to Microsoft 365. |
| Active Directory | client | Read-only queries by both web (admin sync rerun) and worker. |

## 2. Prerequisites

1. **Windows Server** (version per Stage 0), domain-joined, patched, internal network only.
2. **IIS** with role services: Web Server, Static Content, Default Document, HTTP Errors, Request Filtering,
   **Windows Authentication**, IIS Management Console. Example:
   ```powershell
   Install-WindowsFeature Web-Server, Web-Static-Content, Web-Default-Doc, Web-Http-Errors, Web-Filtering, Web-Windows-Auth, Web-Mgmt-Console
   ```
3. **.NET 10 Hosting Bundle** (10.x, latest patch) — installs the ASP.NET Core Module V2 and the .NET runtime used by
   both web and worker (both are framework-dependent). Install it *after* IIS; run `iisreset` afterwards.
4. **SQL Server Express** (version per Stage 0), TCP enabled if remote, TLS certificate trusted by the app server.
5. Unlock the IIS authentication sections once (elevated):
   ```cmd
   %windir%\system32\inetsrv\appcmd.exe unlock config -section:system.webServer/security/authentication/windowsAuthentication
   %windir%\system32\inetsrv\appcmd.exe unlock config -section:system.webServer/security/authentication/anonymousAuthentication
   ```
6. **gMSA** `DOMAIN\gmsa-ats$` created by AD admins, allowed to retrieve its password on this host, then on the host:
   `Install-ADServiceAccount gmsa-ats` and `Test-ADServiceAccount gmsa-ats` (must be `True`).
   The account needs read access to the in-scope AD OU (people and the attributes in `deploy/README.md`).

## 3. DNS, certificate, SPN and Kerberos

1. **DNS:** an internal **A record** `<INTERNAL-DNS-NAME>` → server IP. Prefer an A record over a CNAME (browsers
   build the SPN from the canonical name for a CNAME).
2. **Certificate:** an internal CA certificate for `<INTERNAL-DNS-NAME>`; bind it to the site on 443. No HTTP binding,
   or an HTTP binding only for redirect. HSTS is sent by the app in Production. Track the expiry date (monitoring §9).
3. **SPN:** with a custom host name and a domain account as app pool identity, Kerberos needs an SPN on that account:
   ```cmd
   setspn -S HTTP/<INTERNAL-DNS-NAME> DOMAIN\gmsa-ats$
   setspn -S HTTP/<INTERNAL-DNS-NAME-SHORT> DOMAIN\gmsa-ats$     (only if users use the short name)
   setspn -Q HTTP/<INTERNAL-DNS-NAME>                           (must show exactly one account)
   ```
   Duplicate SPNs break Kerberos silently (fallback to NTLM or 401 loops). With the SPN on the gMSA, leave IIS
   **kernel-mode authentication** on and set `useAppPoolCredentials="true"` for Windows Authentication:
   ```cmd
   %windir%\system32\inetsrv\appcmd.exe set config "AssetTracker" -section:system.webServer/security/authentication/windowsAuthentication /useAppPoolCredentials:"True" /commit:apphost
   ```
4. **Browser policy:** add `https://<INTERNAL-DNS-NAME>` to the Local Intranet zone (GPO "Site to Zone Assignment
   List"); Edge/Chrome follow it. For Chrome/Edge without the zone, use the `AuthServerAllowlist` policy.
5. **Kerberos validation (LAN and VPN — Stage 0 gate):** from a managed client on the LAN, and again over the approved
   VPN:
   - Open the site: no credential prompt; the header shows `DOMAIN\user`.
   - `klist` shows a ticket for `HTTP/<INTERNAL-DNS-NAME>`.
   - On the server, Security event log 4624 for the logon shows *Authentication Package: Kerberos*.
   - Over VPN, the client must reach a domain controller (Kerberos 88/TCP+UDP) and resolve the internal DNS name;
     otherwise it falls back to NTLM or prompts. Record the result in the Stage 0 sign-off.

## 4. SQL logins (least privilege)

Create the database once (or let the first migration create it when the migration account has `dbcreator`).

```sql
-- App pool + worker identity (no DDL).
CREATE LOGIN [DOMAIN\gmsa-ats$] FROM WINDOWS;
USE AssetTracker;
CREATE USER [DOMAIN\gmsa-ats$] FOR LOGIN [DOMAIN\gmsa-ats$];
ALTER ROLE db_datareader ADD MEMBER [DOMAIN\gmsa-ats$];
ALTER ROLE db_datawriter ADD MEMBER [DOMAIN\gmsa-ats$];
-- The app uses no stored procedures today; grant EXECUTE only if a future release adds them:
-- GRANT EXECUTE TO [DOMAIN\gmsa-ats$];

-- Migration account (a separate, named admin group or account; used only during releases).
CREATE LOGIN [DOMAIN\<MIGRATION-GROUP>] FROM WINDOWS;
CREATE USER [DOMAIN\<MIGRATION-GROUP>] FOR LOGIN [DOMAIN\<MIGRATION-GROUP>];
ALTER ROLE db_ddladmin   ADD MEMBER [DOMAIN\<MIGRATION-GROUP>];
ALTER ROLE db_datareader ADD MEMBER [DOMAIN\<MIGRATION-GROUP>];
ALTER ROLE db_datawriter ADD MEMBER [DOMAIN\<MIGRATION-GROUP>];
```

If the worker uses a separate gMSA, repeat the first block for it. The app identities never get `db_owner` or DDL.

## 5. Install

### 5.1 Artifacts
Use the CI `publish` job artifacts (`assettracker-web`, `-worker`, `-efbundle`, `-deploy`) or run
`pwsh ./build/publish.ps1 -Version <x.y.z>` on a build machine. Keep each release's artifacts (rollback, §11).

### 5.2 Database
Run `deploy\Apply-Migrations.ps1 -Bundle .\efbundle\efbundle.exe -Server '<SQL-HOST>\SQLEXPRESS'` **as the migration
account** (`runas /user:DOMAIN\<migration-admin> pwsh`).

### 5.3 Data Protection key folder
Create one folder for the Data Protection key ring shared by web and worker, outside the web root, e.g.
`D:\AppData\AssetTracker\DataProtection-Keys`. Grant **Modify** to the app pool identity and the worker account
(either may create the next key when the current one expires), nothing to users. Put the path in
`DataProtection:KeyPath` in **both** `appsettings.Production.json` files. The keys encrypt the SMTP password stored
in the database (machine DPAPI, so they only work on this server); back the folder up with the database (§12).

### 5.4 Web
1. Copy `web\` to e.g. `D:\Apps\AssetTracker\Web`. Create `appsettings.Production.json` from
   `deploy\web.appsettings.Production.template.json` ([settings reference](../deploy/README.md)).
2. App pool `AssetTracker`: **.NET CLR version = No Managed Code**, pipeline Integrated, identity = custom account
   `DOMAIN\gmsa-ats$` (no password), Start Mode `AlwaysRunning`, idle time-out 0 (optional).
3. Site `AssetTracker`: physical path as above, HTTPS binding for `<INTERNAL-DNS-NAME>` with the certificate.
4. NTFS: app pool identity gets Read & Execute on the folder; Modify only on `logs\` if stdout logging is enabled.
5. `deploy\web.config` (already in the publish output) enables Windows Authentication **and** anonymous
   authentication for the whole site. Anonymous must be on site-wide so the super admin page `/superadmin` works from a
   browser without a domain account; the application itself still requires sign-in on every page except `/superadmin`
   and `/health*`, and its 401 makes IIS send the Windows challenge. Do the IIS smoke test in §15.2 before go-live.
6. Firewall: allow 443 only from approved LAN/VPN ranges.

### 5.5 Worker
1. Copy `worker\` to e.g. `D:\Apps\AssetTracker\Worker`; create `appsettings.Production.json` from
   `deploy\worker.appsettings.Production.template.json`. The super admin setup command (§15) uses the same folder
   and settings.
2. `deploy\Install-Worker.ps1 -InstallPath 'D:\Apps\AssetTracker\Worker' -Account 'DOMAIN\gmsa-ats$'`
   (service `AssetTrackerWorker`, delayed automatic start, restart on failure after 1/1/5 minutes).
3. Logs: Windows Event Log → Application, source `AssetTracker Worker`.

## 6. Configuration reference

All settings, defaults and which app uses them: [`deploy/README.md`](../deploy/README.md). The configuration files
hold only bootstrap values (connection string, Data Protection key folder, reload interval, ops thresholds); roles are
given in the app (Admin → System roles), not in a file; both apps need the same `ConnectionStrings` and `DataProtection:KeyPath`. Everything else (AD query,
sync safety thresholds and schedule, mail relay, notifications, default time zone, IT mailbox) is set by an admin on
**Admin → Settings**, stored in the database and picked up by both apps without a restart (the worker within
`Settings:ReloadInterval`, 30 seconds by default).

## 7. First-run checklist

1. [ ] `GET https://<INTERNAL-DNS-NAME>/health` → `Healthy` (anonymous).
   - [ ] Set the super admin password (§15.1), store it in the password safe, and do the IIS smoke test (§15.2).
2. [ ] Sign in on `/superadmin` as the super admin (roles are given in the app; right after installation nobody has one).
3. [ ] **Admin → Settings** (in this order; each section has its own *Save* button):
       - *Default time zone* (`Clock:TimeZoneId`).
       - *Mail relay*: host, port, TLS mode, sending address (and username/password only for an authenticated
         relay). **Send test email** to a test mailbox; it must report success and arrive.
       - *Notifications*: the IT mailbox / distribution list; check the reminder time.
       - *Directory*: search root and site attribute (other attributes only if Stage 0 requires). **Test directory
         query**: fetched count close to the expected directory size, few or no quarantine candidates.
       - *Directory sync safety*: sync interval (e.g. `01:00:00`), stale-after (~2-3× the interval).
4. [ ] **Admin → Sites:** create every site; set each site's *directory value* to the exact AD site attribute value.
       Set a site's *timezone* only when it differs from the default `Clock:TimeZoneId` (see §10, *Timezones*).
       Rerun **Test directory query** on the Settings page: most entries now map to a known site.
5. [ ] **Admin → Directory sync → Run now:** status `Succeeded`; review issues (unmapped sites, duplicates, missing
       ids). Fix site mappings and rerun until only expected issues remain. Confirm fetched/mapped counts against AD
       and set *Minimum expected entries* (`DirectorySync:MinimumExpectedCount`) on the Settings page accordingly.
6. [ ] **Admin → System roles:** *Add a person* for the first IT staff and application admins and tick their roles;
       tick *Manager* and their site(s) for each manager. A role needs a person who is active in the directory and has a
       Windows account, so this comes after the first directory sync. Sign out as the super admin.
7. [ ] Sign in as one of the new admins → the menu shows *Admin*. Sign in as IT: **Ops** page shows database reachable, worker heartbeat current, last
       successful sync, outbox empty.
8. [ ] Test mail: check out a test asset to a test person with a mailbox (or a test account), confirm holder and IT
       mail arrive, then return it. Check the Mail queue for failures.
9. [ ] `GET /health/ready` → `Healthy`, HTTP 200.
10. [ ] Sign in as a manager: only granted sites are visible. Sign in as a plain domain user: access denied.
11. [ ] Record the privacy decision fields ([`privacy-and-retention.md`](privacy-and-retention.md) §1).
12. [ ] Run the backup/restore drill (§12) before go-live.

## 8. Health endpoints

| Endpoint | Auth | Checks | HTTP |
| --- | --- | --- | --- |
| `/health` | anonymous | none (process is up and serving) | 200 `Healthy` |
| `/health/ready` | anonymous | `database`, `directory-sync`, `mail-outbox`, `worker` | 200 when Healthy/Degraded, **503 when Unhealthy** |

`/health/ready` returns minimal JSON (no personal data, hostnames, exception messages or connection strings):

```json
{"status":"Degraded","checks":[{"name":"database","status":"Healthy","description":"Database reachable."},
 {"name":"directory-sync","status":"Degraded","description":"Last successful directory sync is older than the stale threshold."},
 {"name":"mail-outbox","status":"Healthy","description":"Mail outbox is draining."},
 {"name":"worker","status":"Healthy","description":"Worker heartbeat is current."}]}
```

| Check | Unhealthy | Degraded |
| --- | --- | --- |
| `database` | cannot connect | — |
| `directory-sync` | no sync has ever succeeded | last success older than `DirectorySync:StaleAfter`, or latest run Failed/Rejected |
| `mail-outbox` | (query failed) | any `Failed` mail, or oldest `Pending` older than `Ops:OutboxPendingMaxAge` |
| `worker` | worker never wrote a heartbeat | heartbeat older than `Ops:WorkerHeartbeatInterval × Ops:WorkerStaleAfterIntervals` |

A stopped worker stays `Degraded` however long the outage lasts (sponsor decision D3, `docs/decisions.md`), so alert on `worker` = `Degraded`, not only on 503.

The same data, with details (last error, counts, versions), is on the IT **Ops** page (`/Ops`).

## 9. Monitoring and alerting

Route all alerts to `<client alert owner>`. Suggested rules (tune thresholds with the client):

| Signal | Source | Alert when |
| --- | --- | --- |
| Web liveness | `GET /health` | non-200 for 2 consecutive polls (5 min) |
| Readiness | `GET /health/ready` | `Unhealthy`/503 for 2 polls; `Degraded` for > 1 h (read `checks[]` to see which) |
| Worker service | Service Control Manager / `worker` check | service stopped, or `worker` Degraded |
| Mail | `mail-outbox` check / Ops page | any Failed mail; pending older than threshold |
| AD sync | `directory-sync` check / Ops page | stale or last run Rejected/Failed (offboarding risk: disabled accounts remain eligible until a good sync) |
| Sync count anomaly | Admin → Directory sync | Rejected run (low-count / drop safety check) |
| Sync failure mail | IT mailbox | Every `Failed`/`Rejected` sync run mails one "Asset Tracker: AD sync failed/rejected" alert (D6); it is `Skipped` on the Mail queue when the IT mailbox is not set |
| Disk / DB size | OS / SQL monitoring | free disk < 15%; database > 80% of the Express 10/50 GB cap (per installed version) |
| Backups | client backup platform | any failed or missing SQL backup |
| Certificate | certificate monitoring | expiry < 30 days |
| IIS app pool | Event Log (WAS) | rapid-fail protection / crashes |

## 10. Routine operations

- **Failed mail:** Ops → *Mail queue*: read the last error (relay rejected, timeout, invalid recipient). Fix the cause
  (relay, recipient), then *Requeue* one row or *Requeue all failed*. Requeues are audited. *Skipped* rows had no
  usable recipient (inactive holder, missing address, IT mailbox unset) and are not retried.
- **Sync rejected:** the IT mailbox receives an alert mail (D6). IT can read the run history and issues on
  *Directory sync* (read-only, D7); an admin opens it to rerun. Read the reason (e.g. "would deactivate N of M"). Verify AD
  (OU move, replication, permission change). If the change is legitimate (a real mass leaver event), temporarily raise
  *Maximum deactivation fraction* on Admin → Settings (applies to both apps), rerun, and restore the value. Never disable the check
  permanently.
- **Sync failed:** the IT mailbox receives an alert mail with the run id and a short error (no stack trace). Check
  the worker event log (AD unreachable, gMSA rights). The last good snapshot stays in use.
- **Timezones:** each site has a calendar timezone (*Admin → Sites*, IANA id such as `Europe/Amsterdam` or a Windows
  id such as `W. Europe Standard Time`); empty means the default `Clock:TimeZoneId`. `Clock:TimeZoneId` (Admin → Settings → *Default time zone*) is required:
  if it is missing, pages and reminder ticks that need a site without its own timezone fail with "Clock:TimeZoneId is
  not configured". A loan's calendar is its site's: a tagged loan follows the asset's **current** site, a pool loan
  the pool's site. Due dates must be today or later in that zone.
- **Overdue reminders:** an item is overdue from the start of its due date in its site's timezone (D4; "due today"
  on the due date itself). The worker ticks every `Notifications:ReminderPollInterval` (default 15 minutes) and, for
  each site whose local time has reached `Notifications:ReminderTimeOfDay` (default 08:00), queues one reminder per
  overdue loan to the holder and IT. Dedupe keys `overdue:{loanId}:{site-local date}:{recipient}` make every later
  tick that day, worker restarts and DST transition days harmless: one reminder per loan, site-local day and
  recipient. A worker that was down at the reminder time catches up on its next tick the same local day; a site-local
  day the worker missed entirely is not sent later (the next day's reminder follows). Each tick that queued
  something writes one `notification.overdue-run` audit event; idle ticks write none.
- **Stale banner:** IT sees "People data may be stale". Lending continues by design; escalate to the service owner if
  the sync cannot be restored the same day.
- **Worker stale:** check the service; it restarts automatically on failure. Inspect the Application event log.
- **Privacy request:** follow [`privacy-and-retention.md`](privacy-and-retention.md) §4.

## 11. Release and rollback

Migrations are **forward-only**: there are no down-migrations in production. Rollback = restore the pre-release backup.

**Release**
1. Announce a short maintenance window. Download the release artifacts; read the release notes for config changes.
2. `Stop-Service AssetTrackerWorker` (no mail is sent or synced during the release).
3. **Back up the database** through the client's SQL-aware backup (copy-only full backup) and note its time. Also
   back up the current web and worker folders (including `appsettings.Production.json`).
4. Stop the site's app pool (or drop `app_offline.htm` into the web folder).
5. Apply migrations: `deploy\Apply-Migrations.ps1` as the migration account.
6. Deploy web: replace the web folder contents, keeping `appsettings.Production.json`. Start the app pool / remove
   `app_offline.htm`.
7. Deploy worker: replace the worker folder contents, keeping `appsettings.Production.json`.
   `Start-Service AssetTrackerWorker`.
8. Verify: `/health/ready`, Ops page (web and worker versions match the release), sign-in as IT/manager, one report.

**Rollback** (failed verification)
1. Stop the worker and the app pool.
2. Restore the pre-release database backup (§12 steps 2–5, including the outbox check).
3. Redeploy the previous release's web and worker folders and config.
4. Start the app pool, then the worker; verify as above.

Data entered between release and rollback is lost by the restore; record what was done in that window.

## 12. Backup and restore

Use the client's **existing backup platform** with SQL-aware (VSS/SQL writer or native) backups of `AssetTracker`;
no extra app backup scheduler. Back up together: the database, both `appsettings.Production.json` files, the
**Data Protection key folder** (`DataProtection:KeyPath`; without it the SMTP password stored in the database cannot be
decrypted after a restore or server rebuild and must be re-entered on Admin → Settings), the IIS site
configuration (`appcmd add backup` or the platform's system-state backup), the site certificate (with private key,
per client policy), and the release artifacts. Backups contain personal data: apply the client's access and retention
controls ([privacy](privacy-and-retention.md)).

**Restore drill** (before go-live, then per client policy; record date, RPO/RTO achieved, owner):

1. **Stop the worker first** (`Stop-Service AssetTrackerWorker`) and keep it stopped until step 6, so restored
   pending mail is not sent before it is reviewed. Stop the app pool.
2. Restore the database to the chosen point (in a drill, preferably to a separate drill server/database).
3. Verify data (as IT):
   - Inventory: asset count per site/type/state matches the reference report taken at backup time (Reports → Inventory).
   - Open loans: Reports → Current assignments count and a sample of holders/quantities.
   - Stock balances: Reports → Inventory → Pools: available/outstanding per pool; `on hand = available + outstanding`.
   - Roles and grants: Admin → System roles (as admin) lists the expected roles and manager sites; Admin → Settings shows the IT mailbox.
   - Settings: Admin → Settings shows the expected values; **Send test email** succeeds. "The stored SMTP password
     cannot be decrypted" means the Data Protection key folder was not restored: restore it, or re-enter the password.
   - Queued mail: Ops → Mail queue counts.
4. Inspect pending mail in SQL (read-only):
   ```sql
   SELECT Status, Kind, COUNT(*) AS N, MIN(CreatedAt) AS Oldest, MAX(CreatedAt) AS Newest
   FROM dbo.NotificationOutbox GROUP BY Status, Kind;      -- Status: 1 Pending, 2 Sent, 3 Failed, 4 Skipped
   ```
   Rows that were `Pending` in the backup may already have been delivered after the backup was taken.
5. **Avoid unintended duplicate sends:** decide a cut-off with the alert owner (normally the restore point) and run
   [`deploy/Quarantine-Outbox-After-Restore.sql`](../deploy/Quarantine-Outbox-After-Restore.sql) — first as a dry run
   (`@Commit = 0`), then with `@Commit = 1`. It marks `Pending` rows created before the cut-off as `Skipped` with a
   reason and writes one audit event. Nothing is deleted. Today's overdue reminders keep their dedupe keys, so the
   reminder ticks do not re-create them. If some checkout/return notices must still go out, requeue or resend them
   deliberately afterwards.
6. Start the app pool, then the worker. Check `/health/ready` and the Ops page; the heartbeat becomes current within
   ~1 minute and the sync runs per schedule.
7. Record the outcome and any gaps.

Note: SMTP cannot guarantee exactly-once delivery; a mail accepted by the relay just before a crash may be sent again.

## 13. Patching responsibilities

| Item | Owner | Notes |
| --- | --- | --- |
| Windows Server, IIS | `<WINDOWS-PATCH-OWNER>` | Monthly; stop the worker during reboots is not required (it restarts automatically). |
| .NET Hosting Bundle (10.x patch) | `<WINDOWS-PATCH-OWNER>` | Install the latest 10.0.x; `iisreset`; restart the worker. Both apps roll forward to the latest patch. |
| SQL Server Express CU/GDR | `<SQL-OWNER>` | Take a backup first. |
| Application releases (incl. NuGet dependency updates) | `<SUPPORT-CONTACT>` | Via the release procedure (§11). |
| Certificate renewal | `<WINDOWS-PATCH-OWNER>` | Rebind in IIS before expiry. |

## 14. Troubleshooting

| Symptom | Likely cause | Action |
| --- | --- | --- |
| Repeated credential prompts / 401 loop | No/duplicate SPN, site not in Intranet zone, CNAME, kernel-mode auth vs `useAppPoolCredentials` mismatch | `setspn -Q HTTP/<INTERNAL-DNS-NAME>`; fix duplicates; check zone policy; `klist purge` on the client and retry; check System log for KDC errors (event 4 = duplicate SPN). |
| Works on LAN, prompts on VPN | DC/DNS not reachable over VPN; NTLM blocked | Test `nltest /dsgetdc:DOMAIN` and DNS over VPN; ask network team for Kerberos (88) and DNS routes. |
| 403 after sign-in | The person has no role in the app, is inactive in the directory snapshot, or their SID differs from the one in the snapshot | An admin checks **Admin → System roles** (tick *Show inactive* to see roles of inactive people) and gives the role; it applies on the person's next request, no new logon needed. Check the person on the last directory sync (active, with a SID). If no admin can sign in, use the super admin (§15.3). |
| 500.30 / 500.31 at start | Missing Hosting Bundle / runtime, bad config | Event Log (IIS AspNetCore Module V2); enable `stdoutLogEnabled` temporarily; check `appsettings.Production.json` JSON validity. |
| Mail `Failed`: "5.7.1 relay denied" | Server IP not allowed on the relay, wrong sending address | Ask the mail team to allow the app server / `FromAddress`; requeue. |
| Mail `Failed`: "stored SMTP password cannot be decrypted" | Data Protection key folder missing/changed, or web and worker use different `DataProtection:KeyPath` | Point both apps at the same folder (restore it from backup), or re-enter the password on Admin → Settings. |
| Settings change not picked up by the worker | Worker cannot read the database, or `Settings:ReloadInterval` is 0 | Wait one reload interval; check the worker event log for "Database settings could not be loaded"; restart the worker. |
| Mail `Failed`: TLS errors | Relay certificate not trusted, or `SecureSocket` mismatch | Trust the relay CA on the server; align `Mail:SecureSocket`/`Port`. |
| Mail pending, nothing sent | Worker stopped or stale | Ops page heartbeat; start the service; event log. |
| Sync `Rejected` | Safety check (low count / mass deactivation) | See §10. Do not disable the check. |
| Sync `Failed` | AD unreachable, gMSA lacks rights, wrong `SearchRoot` | Worker event log; test the LDAP path as the gMSA. |
| `/health/ready` `worker` Unhealthy right after install | Worker has never started | Start the service; verify `Ops:WorkerHeartbeatInterval` equals `Worker:HeartbeatInterval`. |
| Browser without a domain account gets a credentials prompt on `/superadmin` | Anonymous authentication is off in IIS (or the section is locked) | Unlock `anonymousAuthentication` (§2 step 5) and redeploy `deploy\web.config`; check *Authentication* on the site in IIS Manager. |
| Domain users are not signed in automatically | Windows Authentication off, or the app's 401 is not turned into a challenge | IIS Manager → site → *Authentication*: Windows **and** Anonymous enabled; providers Negotiate, NTLM. Repeat §15.2. |
| `Set-SuperAdmin.ps1`: "No result after … seconds" | The gMSA cannot run the task (no *Log on as a batch job*), cannot read the worker folder, or cannot reach SQL Server | Grant the right (§15.1), check NTFS on the worker folder, read the Application event log and *Task Scheduler → History*. |
| Super admin: "Sign-in failed" with the right password | Account locked (5 failures, 15 minutes) or switched off | `Set-SuperAdmin.ps1 -Action status`, then `-Action unlock` or `-Action set-password`. IT can check the audit log for `superadmin.*` rows. |
| Super admin: 429 "Too many sign-in attempts" | More than 10 sign-in attempts from this IP address in 15 minutes | Wait 15 minutes, or sign in from another workstation. |

## 15. Super admin (account outside AD)

One built-in account, username **`superadmin`**, signs in with a password on **`https://<INTERNAL-DNS-NAME>/superadmin`**
(sponsor decision D12). It gets the **Admin** role only: Admin → Settings (AD and mail), roles and grants, sites,
directory sync, asset types, vendors, models, overview tiles and Privacy. It cannot open inventory, loans, reports, floor
plans, Ops or the audit log. While it is signed in, every page shows *Signed in as the super admin (not an AD account)*.
Sessions end after 30 minutes without activity and 8 hours after sign-in. Five wrong passwords lock it for 15 minutes; one
IP address gets 10 attempts per 15 minutes. Every sign-in, lockout and sign-out is audited with the IP address, and each
sign-in and each lockout mails the IT mailbox. The app never ships with a password: until one is set, the account is off.

### 15.1 Setting the password (setup script)

The database only accepts the gMSA, so the change runs as a one-off scheduled task under the gMSA. The password never
leaves the administrator's console: only its hash goes to the task.

Once per server: give the worker's gMSA **Log on as a batch job** (Local Security Policy → *User Rights Assignment*,
or the server's GPO), so Task Scheduler can run a task as it.

On the server, in a normal **elevated** Windows PowerShell console (not the ISE, not a remote session), from the
`deploy` folder of the release:

```powershell
.\Set-SuperAdmin.ps1 -Action set-password        # asks twice, no echo; at least 16 characters
.\Set-SuperAdmin.ps1 -Action generate-password   # random 24 characters, shown once: put it in the password safe
.\Set-SuperAdmin.ps1 -Action unlock              # clears a lockout
.\Set-SuperAdmin.ps1 -Action disable             # switches the account off and ends its sessions
.\Set-SuperAdmin.ps1 -Action status              # on/off, locked, password date, last sign-in time and IP
```

Defaults: `-WorkerPath` = the `AssetTrackerWorker` service's folder; `-ServiceAccount` = the account that service runs
as; `-RequestFolder` = `SuperAdmin-Requests` next to the Data Protection key folder (`DataProtection:KeyPath`), else
`%ProgramData%\AssetTracker\SuperAdmin-Requests`. The script:

1. refuses the demo password and passwords shorter than 16 characters;
2. runs `AssetTracker.Worker.exe superadmin hash` in your session (no database);
3. writes a request file (action and hash) into the request folder (ACL: Administrators and the gMSA only) and makes
   you the file's owner;
4. registers and starts a one-off task `AssetTracker super admin <id>` under the gMSA
   (`AssetTracker.Worker.exe superadmin apply <file>`), waits for the result file, prints it, and removes the task and
   the files.

The audit log names **the Windows account that owns the request file** (you) as the actor of `superadmin.password-set`,
`superadmin.unlock`, `superadmin.disable` or `superadmin.status`. `set-password`, `generate-password` and `disable` end
any open super admin session.

The signed-in super admin can also change its own password on `/superadmin/account` (current password required, same
rules). Nobody can see or reset the password inside the app, and no admin can remove the account.

### 15.2 IIS smoke test (before go-live and after IIS changes)

The test host cannot cover IIS authentication, so check by hand:

1. From a domain-joined workstation: open `https://<INTERNAL-DNS-NAME>/`. You are signed in with Windows without a
   prompt, and the header shows your account.
2. From a browser without a domain account (or with automatic intranet sign-in switched off): open `/superadmin`. The
   sign-in page appears without a credentials prompt.
3. Sign in as `superadmin`. You land on Admin → Settings with the banner; `/Assets` and `/Audit` give 403.
4. *Sign out*. You are back on `/superadmin`; `/Admin/Settings` asks for sign-in again.
5. `GET /health` and `/health/ready` still answer anonymously.
6. The IT mailbox received the sign-in mail; as IT, the audit log shows `superadmin.signin` and `superadmin.signout`.

### 15.3 Recovery: locked out, or AD settings broken

Use this when no AD account can get in as an admin: for example after wrong AD/LDAP settings on Admin → Settings, a lost
Admin role (nobody has it any more, or every admin is inactive after a bad sync), broken mail settings or a failed
directory sync.

1. If the super admin password is unknown, or the account is off or locked: run `Set-SuperAdmin.ps1 -Action
   set-password` (or `generate-password`, or `unlock`) on the server (§15.1).
2. Open `https://<INTERNAL-DNS-NAME>/superadmin` and sign in as `superadmin`.
3. Fix the cause on **Admin → Settings** (directory search root, site attribute, mail relay, IT mailbox) and use
   *Test directory query* and *Send test email*; check **Admin → Directory sync** and rerun it.
4. Sign out. If the password was shared during the incident, set a new one afterwards (§15.1).

**Limit:** this does **not** cover a full domain outage. The app reaches SQL Server with its gMSA, which needs Active
Directory: if no domain controller is reachable, the app (and the setup task) cannot start or read the database at all.
Restore AD first; the super admin only helps once the domain is back.
