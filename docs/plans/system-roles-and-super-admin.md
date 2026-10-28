# System roles in the app + super admin account: plan

Status: **draft for review, review fixes folded in**. Nothing is built yet.
Base: `main` @ `584df1f`.

## Why

Kjeld, 28 Oct 2026:
- **All roles are managed inside the app.** Today IT, Manager and Admin come from AD group SIDs in the server config file (`Roles:*GroupSids`); only Facility is given in the app.
- **One super admin account outside AD.** It lets someone run Asset Tracker without a domain account, and it is the failsafe for restoring the AD and mail connections when they break.

Decisions (Kjeld):

| Topic | Decision |
|---|---|
| How roles are given | **To people:** an admin picks a person from the directory and ticks their roles. |
| What the super admin can do | **Admin work:** settings (AD, mail), roles and grants, sites, directory sync, asset types, vendors, models, overview tiles **and Privacy**. No inventory, loans, reports, floor plans or audit log. |
| How the super admin signs in | **Username and password only.** |
| How many super admins | **Exactly one** built-in account. |
| First password | **A setup command on the server.** It runs as a one-off scheduled task under the app's own service account (gMSA), so no extra database rights are needed. The app never ships with a default password. |

**What the super admin can and can't rescue:**
- **It can rescue** broken AD/LDAP settings, broken mail settings, a lost admin role, and a failed directory sync.
- **It can't rescue a full domain outage.** The app reaches SQL Server with its gMSA, which needs AD. If the domain controllers are down, the app doesn't start at all. The runbook says so.

## 1. All roles in the app

**What changes:**
- **Roles that can be assigned:** `RoleAssignmentService.Assignable` grows from `[Facility]` to IT, Manager, Admin and Facility. The table `RoleAssignments` already stores any `AppRoles` value, so the roles need no migration.
- **Sign-in:** `AppRoleClaimsTransformation` stops reading group SIDs and resolves every Windows user's roles from `RoleAssignments`. It matches on the person's SID, and only while the person is active in the directory snapshot. This is the same single indexed join Facility already uses, so no extra cost.
  - It branches on the authentication type. The super admin gets exactly Admin from its own sign-in and has no database lookup (§2).
  - Role claims are never stored in a cookie, so they are read fresh on each request.
- **Config:** `RoleGroupOptions` and `Roles:*GroupSids` are removed from these places:
  - code: `Program.cs`, the transformation;
  - config: `appsettings.json`, `deploy/web.appsettings.Production.template.json`, `deploy/README.md`;
  - demo: `DemoOptions`, `DemoIdentity`;
  - tests: `AppFactory`, `TestUsers`/`TestAuthHandler`;
  - docs and comments.
  - If the config file still has the keys, startup logs one warning that they are ignored. The Admin → Settings page shows the same notice, because the Ops page is IT only and admins never see it.
  - The security test `SettingsConfigurationTests` keeps its purpose (database settings can't override file-only keys) and points at another file-only key.
- **Site grants:** manager grants (`ManagerSiteGrants`) keep their meaning. They count only while the person has the Manager role and is active. Sponsor decision **D9** is reworded from "leaves the manager group" to "loses the Manager role", and its test is renamed.

**The System roles page** (Admin → System roles; admins and the super admin) becomes the single place for roles:
- **Layout:** one row per person with any role, with a tick box each for IT, Manager, Admin and Facility.
  - For managers, the row also shows their sites. Site grants move here, and Admin → Grants redirects here.
- ***Add a person*** searches the directory snapshot. To get a **role**, a person must be active and have a Windows account (SID), because a role without a SID can never sign in. A site **grant** alone doesn't need a SID, as today.
- **Filters:** by role and by site, plus *Show inactive* for people who keep roles but are inactive in AD. Those roles have no effect until the person is active again.
- **Safety rules:**
  - An admin can't remove their own Admin role.
  - The super admin may remove any role, including the last admin's. Doing so shows a warning that from then on only the super admin can give roles.
- **Audit:** every change is audited (`role.assign`, `role.remove`, `grant.add`, `grant.remove`).

**Changes take effect on the person's next request.** Today an AD group change needs a new Windows logon.

**Moving over:** production values were never set (Stage 0, issue #2), so nothing needs migrating and there is no temporary fallback to the config groups. After the update, the super admin signs in and gives the first IT and Admin roles.

## 2. Super admin account

### Sign-in
- **Two ways to sign in:** the app keeps Windows sign-in, and adds a page at **`/superadmin`** (`[AllowAnonymous]`) with a username and password.
- **Authentication schemes:**
  - A cookie scheme `SuperAdmin`.
  - A policy scheme that forwards to the cookie when the super admin cookie is present, and otherwise to the **Windows scheme**.
  - The Windows scheme is configurable: Negotiate in production, the test scheme in Testing, the demo scheme in demo mode. That way tests and the demo cover the super admin too. Today `AppFactory` and `DemoWebSetup` set the default schemes directly; both move to this option.
- **IIS:**
  - Turn on **anonymous authentication for the whole site** alongside Windows authentication. A `<location>` rule just for `/superadmin` isn't enough: IIS would still answer every other page with a 401, and a browser without a domain account would show a credentials prompt.
  - Every page except `/superadmin` and `/health` still requires sign-in through the app's fallback policy. `AddNegotiate` keeps working in-process: it hands Windows sign-in to IIS, and the app's 401 triggers the IIS challenge.
  - This needs a **manual IIS smoke test** before go-live, because the test host can't cover it. The steps go in the deployment guide and runbook.
- **Username:** fixed, **`superadmin`**. The signed-in identity has the name claim `superadmin (local)`. Every audit and "granted by" field already uses `ICurrentUser.Name`, so no change is needed there. The only code reading `Sid` (`AccessScope`, manager grants) already handles a null SID.
- **Cookie:**
  - Named `__Host-ats-superadmin` in production. Testing and the demo use plain HTTP, so there it has no prefix.
  - HttpOnly, Secure, SameSite=Strict, protected with the existing key ring.
- **Cookie events:**
  - Forbidden pages give **403**, not a redirect.
  - Not being signed in sends you to `/superadmin`.
  - Sign-out is a POST, with anti-forgery.
- **Session checks** (`OnValidatePrincipal`, one indexed lookup per request) end the session if:
  - the password was changed or reset, or the account was disabled (the `SecurityStamp` changed);
  - it has been **30 minutes** idle;
  - it has been **8 hours** since sign-in.
  - Changing the password in the app issues a fresh cookie, so the person who changed it stays signed in.
- **Brute-force protection:**
  - **Atomic counter:** each failed attempt increments the counter in one `UPDATE … SET FailedCount = FailedCount + 1`, checked before the password is verified, so parallel attempts can't go past the limit.
  - **5 failures lock the account for 15 minutes.** The lock is stored in the database and survives a restart. The setup command can unlock it.
  - **Per-IP limit:** at most 10 attempts per IP address every 15 minutes, using ASP.NET Core's built-in rate limiter. Someone else on the network can't keep the account locked as easily, and the account owner can still unlock it with the setup command.
  - **Messages:** errors never say whether the username or the password was wrong.
- **Alerts:**
  - Every successful sign-in, lockout and sign-out is audited with the IP address.
  - Failed attempts are audited until the account locks; attempts during a lockout are only counted, not audited one by one.
  - A successful sign-in and a lockout (at most one mail per lock period) mail the IT mailbox (`Mail.ItMailbox`). If mail is broken, the outbox retries 8 times over about 2 hours and then marks the mail *Failed*. If no IT mailbox is set, it is marked *Skipped*. The audit entry is always written.
- **On screen:** a banner on every page: *Signed in as the super admin (not an AD account)*.

### Rights
- **The super admin gets the Admin role**, which covers everything in the decision table above, **including Privacy** (the permanent anonymisation of inactive people).
  - It never gets IT, Manager or Facility.
  - Sponsor decision D2 (audit log for IT only) stays.
- **Limits on what an admin can do to it:** the super admin is not in `RoleAssignments`, so no admin can remove it, and nobody can see or reset its password in the app.
- **Changing its own password** is possible in the app, but only while signed in as the super admin, and it needs the current password. The rules are the same as the setup command: at least 16 characters, and not the demo password.

### Setup command (on the server, under the gMSA)
The database only accepts the app's gMSA, so the command runs as a **one-off scheduled task under the gMSA**. An IT admin starts it with a script, so the password never passes through the task.

1. **Start:** the admin runs **`deploy/Set-SuperAdmin.ps1 -Action <action>`** on the server, in a normal elevated PowerShell console (not ISE or a remote session).
2. **The password stays with the admin:**
   - For `set-password`, the script asks for the password twice with no echo. It refuses passwords shorter than 16 characters and the demo password.
   - For `generate-password`, it creates a random 24-character password and shows it once.
   - It then calls `AssetTracker.Worker.exe superadmin hash`, which needs no database, so it runs as the admin. **Only the hash** leaves the admin's session.
3. **The request:** the script writes a request file (action and hash) into a folder that only Administrators and the gMSA can use. It then registers and starts a one-off scheduled task under the gMSA: `AssetTracker.Worker.exe superadmin apply <file>`.
4. **The task:**
   - It applies the action and audits it with the **Windows account that owns the request file** (read from the file's ACL owner, not from its content).
   - It deletes the request and writes a result file.
   - The script shows the result, then removes the task and the result.

**Actions:**
- **`set-password`** and **`generate-password`**: store the new hash, enable the account and change the security stamp, which ends any open session.
- **`unlock`**: clears a lockout.
- **`disable`**: switches the account off. Sign-in is refused until a new password is set.
- **`status`**: shows whether the account is enabled or locked, when the password was set, and the last sign-in time and IP.

**How the worker handles the command:**
- After `Build()` it branches on `args[0]` and never starts the background jobs.
- It sets `ContentRootPath = AppContext.BaseDirectory`, so the production appsettings and connection string are found wherever it is started from.
- It registers its own `ICurrentUser` that reports the requesting account instead of `system:worker`.

**Until a password is set**, the account is disabled and `/superadmin` says so.

### Storage
- **New table** `LocalAccounts`, with one row per account; only `superadmin` exists:
  - `UserName`
  - `PasswordHash`: ASP.NET Core Identity's `PasswordHasher`. It needs the package `Microsoft.Extensions.Identity.Core` 10.0.x in Core/Infrastructure/Worker, at a pinned version. A `SuccessRehashNeeded` result stores the upgraded hash.
  - `PasswordChangedAt`
  - `IsEnabled`
  - `FailedCount` and `LockedUntil`
  - `LastSignInAt` and `LastSignInIp`
  - `SecurityStamp`
- **Migration** `LocalAccounts` (SQL Server).
- **Upgrade test** in the SQL Server CI job, following `ZoneRenameMigrationTests`. CI doesn't apply migrations today; it only checks for pending model changes.

## 3. Demo and docs
- **Demo role switcher:** the ticks on `/Demo` **replace** the demo user's roles. They write `RoleAssignments` directly, because the switcher is open to users with no role and `RoleAssignmentService` requires Admin. *Reset* reseeds the default IT and Admin roles.
- **Demo super admin:** seeded only in demo mode, which already refuses to start outside Development, with the password **`demo-superadmin-2026`**, shown on `/Demo`. The setup command and the in-app password change refuse that password, so it can't be carried over to production.
- **Demo database:** the demo stores a schema version. When it differs from the app's, the demo rebuilds `demo.db`, because `EnsureCreated` does nothing on an existing file. Testers no longer need to delete `demo.db` by hand.
- **Docs:**
  - `development.md`: roles, the super admin, schemes.
  - `deploy/README.md` and `runbook.md`: IIS anonymous + Windows authentication, the setup script, a *Locked out / AD settings broken* recovery procedure (and its limit), and the "403 after sign-in" row rewritten for in-app roles.
  - `testing-guide.md`.
  - `decisions.md`: D9 reworded and a new **D12** for this decision.

## 4. Tests
- **Test users:** `TestUser.It`, `.Manager` and `.Admin` (321 uses in 37 files) become active `People` rows with role assignments, seeded by `AppFactory`. `AppFactory` keeps them in its fake directory, so a test sync never makes them inactive.
- **Role resolution:** every role comes from `RoleAssignments`; inactive people get none; group SIDs are ignored; the super admin gets exactly Admin with no lookup.
- **System roles page:**
  - assign and remove each role, with the audit rows;
  - an admin can't remove their own Admin role;
  - the super admin's last-admin warning;
  - a role needs an active person with a SID, a grant doesn't;
  - grants on the same page and the Grants redirect.
- **Super admin:**
  - sign in and sign out;
  - wrong password; lockout after 5 tries for 15 minutes (time faked); parallel attempts don't pass the limit; the per-IP limit;
  - a disabled account is refused;
  - a password change or reset ends other sessions; the 30-minute idle and 8-hour limits;
  - 403 and not a redirect on IT pages (inventory, loans, reports, Workplace, Audit);
  - it can reach Settings, System roles, Sites, Sync and Privacy;
  - audit actor `superadmin (local)`; one IT mail per sign-in and per lock period;
  - every page except `/superadmin` and `/health` still needs sign-in.
- **Setup command:**
  - `hash` makes a verifiable hash;
  - `apply` for each action;
  - the requester is taken from the file owner;
  - a password under 16 characters or the demo password is refused;
  - the request file is deleted.
- **Demo:** the switcher replaces roles, reset reseeds them, the demo super admin can sign in, and the schema-version rebuild works.
- **CI:** the SQL Server job runs the new upgrade test.

## 5. Delivery

Two PRs, each needing green CI and Kjeld's review, never merged automatically. **E is built first**, because it works on its own: the super admin gets Admin from its sign-in, not from config.

| PR | Contents |
|---|---|
| **E: Super admin** | §2, the setup command and script, the demo super admin, the demo schema rebuild, the runbook recovery procedure, tests. |
| **D: Roles in the app** | §1, the demo role switcher, test-user seeding, docs, tests. Removes `Roles:*GroupSids`. Merge it after E, or together with E. |

## Out of scope
- Two-factor sign-in.
- Limiting the super admin to certain IP addresses.
- Roles given to AD groups.
- More than one local account.
- Self-service password reset.
- Keeping the app running during a full domain outage.
