# Testing guide: local demo mode

For the Facility **Keys & digital access** module, see [`key-access.md`](key-access.md). In Demo controls, act as **Max Demoson (Facility)**. Try the room lookup, master key details, lockbox reservation, reported versus verified return and digital activation/revocation verification. Digital devices are manually managed in the manufacturer's app; the demo never opens hardware or sends real credentials.

This guide lets you try the whole asset tracker on your own computer: lending, returns, corrections, mail, reminders, reports, roles, settings and operations. You need no Active Directory, mail relay, SQL Server, IIS or Windows sign-in. Everything runs in one local process with **fictional data only**.

> The demo is for evaluation. It behaves like production in everything that matters for the scenarios below (same pages, services, permissions, audit and mail outbox). Only the parts around the app are simulated: sign-in, the database engine, the directory, the mail relay, the clock and the background worker. Those parts are tested on a real server separately (see [Testing on a real Windows server](#testing-on-a-real-windows-server)).

## 1. Prerequisites

- **The .NET 10 SDK.** This is the only thing you install. Get it from <https://dotnet.microsoft.com/download/dotnet/10.0> (choose *SDK*, version 10.0.100 or later). It works on Windows, macOS and Linux and needs no administrator rights after installation.
- A copy of this repository, either from `git clone` or as a ZIP download from GitHub.
- A web browser.

To check the SDK, open a terminal (on Windows, *PowerShell*) and run `dotnet --version`. It should print `10.0.x`.

## 2. Start the demo

Open a terminal in the repository folder (the one that contains `README.md`) and run:

```bash
dotnet run --project src/AssetTracker.Web --launch-profile Demo
```

- The first start compiles the app (about a minute) and then creates and fills the demo database (a few seconds).
- The browser opens <http://localhost:5080> automatically. If it doesn't, open that address yourself. The demo uses plain HTTP, so no certificate is needed.
- A striped **DEMO MODE — fictional data** banner appears at the top of every page.
- To stop the demo, press `Ctrl+C` in the terminal. To start it again, run the same command. Your data is kept between runs.
- If port 5080 is already in use, add a different address: `dotnet run --project src/AssetTracker.Web --launch-profile Demo -- --urls http://localhost:5090`.

**If the build fails with `error NU1100: Unable to resolve '…'`** for every package, NuGet on that machine has no usable package source. This is common on a fresh .NET install. The repository's `nuget.config` points at nuget.org, so first run `git pull` to make sure you have it. If it still fails, check `dotnet nuget list source`:
- If nuget.org isn't listed, run `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`.
- If it's listed as disabled, run `dotnet nuget enable source nuget.org`.

Behind a company proxy, nuget.org must be reachable from the machine.

The `Demo` launch profile sets `ASPNETCORE_ENVIRONMENT=Development` and `Demo__Enabled=true`. Demo mode is honoured **only** in the Development environment. If `Demo:Enabled` is set anywhere else, the app refuses to start, so a demo setting can never take effect on a server.

## 3. Where the data lives, and how to reset it

| OS | Folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\AssetTrackerDemo` (for example `C:\Users\<you>\AppData\Local\AssetTrackerDemo`) |
| macOS | `~/Library/Application Support/AssetTrackerDemo` |
| Linux | `~/.local/share/AssetTrackerDemo` |

The exact folder is shown on the **Demo controls** page (`/Demo`). It contains `demo.db` (the SQLite database), `mail/` (the demo mailbox, one `.eml` file per message, which any mail program can open) and `keys/` (the encryption keys for the SMTP password field on the settings page). To use another folder, start with `Demo__DataDirectory=<path>` set in the environment.

**To reset**, use **Reset demo data** on `/Demo`. It deletes all data and mail, sets the clock back to now and seeds the fictional data again. You can also stop the app and delete the folder; the next start recreates everything. **After updating the app** you no longer need to delete anything: the demo stores a schema version in `demo.db`, and when it differs from the app's (a new table or column), the app rebuilds `demo.db` from scratch on start-up, seeds it again and empties the demo mailbox. The console logs *"The demo database … was made by another version of the app; rebuilding it."* when that happens.

## 4. What is simulated

| Production | Demo mode |
| --- | --- |
| Windows Authentication (Kerberos); roles given in the app (Admin → System roles) | A demo sign-in that issues the same claims (name, user SID) for the person you choose on `/Demo`. Roles come from the same `RoleAssignments` table as in production, so the real role lookup, policies and site scoping run unchanged. |
| SQL Server | SQLite file `demo.db` (same data model) |
| Active Directory (paged LDAP query) | A fixed fictional directory of 151 entries (see §6) |
| SMTP relay to Microsoft 365 | The **demo mailbox** (`/Demo/Mailbox`). Mail is written as `.eml` files and shown as plain text. |
| Worker Windows service (outbox sender, reminders, sync, heartbeat) | The same jobs run inside the web process. The outbox is polled every 10 s, reminders every minute, the directory syncs hourly and the heartbeat runs every 30 s. |
| System clock | Real time plus a **time travel** offset (`/Demo`: +1 day, +7 days, reset) |
| Data Protection key ring (DPAPI) | A plain key folder `keys/` |

Demo defaults: default timezone `Europe/Amsterdam`, reminder time 08:00 site time, IT mailbox `it-helpdesk@demo.example`. You can change settings on **Admin → Settings** as in production. The directory and relay values shown there are placeholders, and a note on the page says that demo mode overrides them.

## 5. The demo controls

![Demo controls: role switcher, time travel and reset](images/demo-controls.png)

Open **Switch role / time travel / reset** in the banner (`/Demo`):

- **Roles:** tick any combination of *IT*, *Manager*, *Admin* and *Facility*, or none, and *Save roles*. The ticks **replace** the roles of the person you act as, in the demo database (the same roles Admin → System roles shows). They take effect on the next page.
- **Act as person:** the person you are signed in as; their roles and manager sites come with them. **Demo Manager (AMS)** (`DEMO\manager.ams`) has the Manager role and the Amsterdam site. **Max Demoson** has the Facility role. **Demo You** (`DEMO\you`, the default) has IT + Admin and no site.
- **Reset demo data** gives `DEMO\you` the roles **IT + Admin** again (with everything else).
- **Time travel** moves the demo clock forward. Due dates, the overdue rule, reports and reminders all follow it. After a jump, a directory sync and a reminder pass run straight away.
- **Reset demo data** starts over.
- **Super admin (not an AD account):** the panel shows the demo super admin's sign-in: `/superadmin`, username `superadmin`, password **`demo-superadmin-2026`**. This password exists only in the demo; the setup command on a server and the password change in the app refuse it.

Your choice is kept in a cookie, so each browser (or private window) can act as a different user at the same time.

![Demo mailbox](images/demo-mailbox.png)

The **demo mailbox** (`/Demo/Mailbox`) lists every message, newest first. Click a subject to read it. **Clear mailbox** deletes them all. Mail for the seeded history is marked as already sent, so after a reset the mailbox starts empty and fills with today's overdue reminders (for sites where it is already past 08:00 local time).

![Current loans as IT](images/demo-loans-it.png)

## 6. The fictional data

All names are made up, for example `Alex Demoson` or `Jules Sampleton`. All addresses end in `@demo.example` and all phone numbers come from ranges reserved for fiction.

**Sites**

| Key | Name | Timezone | Directory value |
| --- | --- | --- | --- |
| AMS | Amsterdam (demo) | Europe/Amsterdam | `Amsterdam` |
| NYC | New York (demo) | America/New_York | `New York` |
| SIN | Singapore (demo) | Asia/Singapore | `Singapore` |

**People** (directory of 151 entries; 149 people are synced and 146 are active)

- `you` (Demo You) and `manager.ams` (Demo Manager (AMS), granted site AMS). These are the role switcher's people.
- 144 regular people spread over the three sites. Usernames follow the pattern `first.surname`, for example `alex.demoson`.
- Disabled in the directory: `kai.demoson`, `frankie.sampleton` and the leaver `alex.testwell`. The leaver was active when the history was seeded and still holds an overdue laptop.
- Site value matches no site: `jules.fixturo` (`Utopia Office`) and `kai.fixturo` (`Remote`). They are synced without a site and reported as `unmapped-site`.
- No email address: `lou.fixturo`. This person is active but cannot receive new loans.
- Two entries share one directory id (`max.fixturo` and `noor.fixturo`). Both are quarantined as `duplicate-id` and never become people.

**Inventory:** 52 tagged assets: laptops (`…-LT-…`), desktops (`-DT-`), screens (`-SC-`), phones (`-PH-`, with valid IMEIs and phone numbers) and phone-number assets (`-PN-`). Two are retired (`AMS-LT-010`, `SIN-DT-003`). `NYC-LT-008` is unavailable (returned damaged). `AMS-LT-009` has a description that starts with `=` to demonstrate CSV formula neutralization. There are 12 pools, one per site for each of `HS-STEREO` (headsets), `CAM-HD` (webcams), `KB-US` (keyboards) and `MOUSE-W` (mice), with stock receipts, a loss and a transfer between sites in their history.

**Open loans** ("today" means each site's own local date)

| Item | Holder | Due |
| --- | --- | --- |
| AMS-LT-001 | Alex Demoson | 10 days overdue |
| AMS-DT-001 | Max Demoson | 2 days overdue |
| AMS-LT-004 | Alex Testwell (**inactive**) | 5 days overdue |
| HS-STEREO (AMS), 2 units | Dana Sampleton | 5 days overdue |
| AMS-LT-002 | Dana Demoson | due today |
| AMS-PH-001 | Jules Demoson | in 3 days |
| HS-STEREO (AMS), 3 units, **1 returned** | Alex Sampleton | in 5 days |
| AMS-LT-006 | Jules Sampleton (**corrected** from Gabi Sampleton) | in 6 days |
| AMS-LT-005 | Demo You | in 14 days |
| AMS-LT-003 | Gabi Demoson | no due date |
| NYC-LT-001 | Billie Demoson | 3 days overdue |
| NYC-PH-001 | Eli Demoson | due today |
| CAM-HD (NYC), 1 unit | Noor Demoson | due today |
| NYC-DT-001 | Billie Sampleton | in 2 days |
| NYC-LT-002 | Harper Demoson | no due date |
| NYC-LT-003 | Jules Fixturo (no site, D8) | no due date |
| SIN-LT-001 | Casey Demoson | 1 day overdue |
| SIN-LT-002 | Lou Demoson | 7 days overdue |
| KB-US (SIN), 2 units | Indy Demoson | in 4 days |
| SIN-PH-001 | Frankie Demoson | no due date |

**Closed history:** `AMS-LT-007` has two closed loans. `NYC-LT-007` was loaned to Billie Demoson at New York, returned, and then **moved to Amsterdam**. `NYC-LT-008` was returned damaged. A Singapore mouse loan was returned.

**Floor plans (Workplace):** AMS *Floor 1* (four offices, boardroom, canteen, print room; zones *Finance*, *Engineering*, *Service desk*), AMS *Floor 2*, NYC *Floor 1* and the dense AMS *Floor 3*, drawn from the fictional SVGs in `DemoFloorplans`, with a few people, desktops and screens placed at desks of AMS *Floor 1* (and things to check: desks without a desktop or a screen, and the leaver) and three people in *Operations* on NYC *Floor 1*. *Floor 3* is crowded on purpose, to check labels: an *Open plan* of twelve 8-seat tables close together (six zones, two tables each), with 86 people, 48 desktops and 30 screens, three of them people with long names (*Maximiliana van den Berghe-Oosterhuis*, *Christopher Montgomery-Fictaway*, *Anne-Sophie Demoulin-Placeholt*), a *Focus room* with four people and a *Huddle* room. `max.demoson` has the **Facility** role. All roles are given inside the app (Admin → System roles): `you` has IT + Admin and `manager.ams` has Manager with the AMS site.

## 7. Scenario checklist

Each scenario names the acceptance gate of the MVP plan (`docs/mvp-plan.md` §6, "Stage n") or the sponsor decision (`docs/decisions.md`, D1–D11) that it demonstrates. Unless a scenario says otherwise, use the default identity (IT + Admin as `DEMO\you`). Switch persons and roles on `/Demo`.

### IT / editor

- [ ] **Check out a laptop** (Stage 2). Go to *Loans → Check out*, enter tag `AMS-LT-008`, search a person (for example `alex`), pick a due date and confirm. **Expected:** the loan page opens and `AMS-LT-008` is *CheckedOut*. Within about 10 s the demo mailbox shows two *Checked out* messages: one to the holder and one to `it-helpdesk@demo.example`.
- [ ] **Item finder.** On *Loans → Check out* click the *Item* box with nothing typed. **Expected:** a list of items that are available (tag, vendor and model, type, site badge). Type `LT-00`, `latitude`-style words such as `Pro 14`, a serial number (`DEMO-SN-AMS-L001`) or a phone number: the list narrows. Type `AMS-LT-001`: it is listed first, greyed out as *Checked out to …* and cannot be picked. `NYC-LT-008` shows *Unavailable*. Type `stereo`: the headset pool appears with its available count. Use ↓/↑ (greyed rows are skipped) and Enter, or click, to pick; focus then moves to *Person*.
- [ ] **Finder filters.** Choose *Type: Phone* or a *Site* next to the item box: the list narrows at once. Pick a person from *Singapore* in step 2 **without** touching the site filter: the filter switches to that site; choose a site by hand first and it stays.
- [ ] **Pool via the finder.** Pick the `HS-STEREO` pool, then a person, then *Find*: the confirm step shows the pool with a quantity stepper; check out 2 units.
- [ ] **Without the dropdown.** Type `PH-001` with a site filter and press *Find* without picking: a list of the matching items is shown with links (checked-out ones greyed). Typing text that matches exactly one item selects it directly; text that matches nothing says so.
- [ ] **Person typeahead shows the site.** On *Loans → Check out* type `an` in *Person*. **Expected:** suggestions appear while typing, each with the person's name, username, email, job title and a site badge (`AMS`, `NYC`, `SIN` or *No site*). Arrow keys and Enter, or a click, pick one; *Find* then offers just that person, already selected, with a note when their site differs from the item's.
- [ ] **Overview tiles.** Log in as IT and open *Overview*. **Expected:** a find box, four clickable tiles (open loans, overdue, assets available, pool units; each opens the matching list), "Needs attention" (a loan held by someone who left, an unavailable asset, a pool below minimum), the overdue table with a *Return* button per row, due soon, inventory by type, and pool stock with the minimum next to each pool. Pick a site in the *Site* dropdown: every figure and list narrows to that site and the tiles' links keep the site.
- [ ] **Find box.** Type `AMS-LT-001` in the overview find box and press Enter. **Expected:** the return page of that loan (it is checked out). Type `AMS-LT-010`: the asset page, with *Check out* offered in the suggestion list. Type a person's name: the person appears as a suggestion. Type `stereo`: the headset pool with its available count.
- [ ] **Minimum stock.** Open the `HS-STEREO` pool: *Minimum stock* shows 10; set it above the available count and save. **Expected:** a *Below* badge appears on the pool list, *Pools → Below minimum* lists it, and it moves to the top of the overview's pool stock and counts in "Needs attention". Clear the field to remove the minimum.
- [ ] **Loans held by people who left.** *Loans* → tick *Held by people who left* (or follow the link in "Needs attention"). **Expected:** only open loans whose holder is inactive, such as `AMS-LT-004`.
- [ ] **Phone layout.** Make the browser window narrow (about 400 px wide, or use the phone view of the developer tools). **Expected:** the menu is collapsed behind a *Menu* button; tables turn into cards with the column names; nothing scrolls sideways.
- [ ] **Missing email blocks checkout** (Stage 2). Try to check out to `lou.fixturo`. **Expected:** this person is not offered as a recipient, or the checkout is refused with "has no valid email address".
- [ ] **Inactive people cannot receive new loans** (Stage 1/2). Search `kai.demoson`. **Expected:** not offered for checkout.
- [ ] **Unmapped site can still borrow** (D8). `jules.fixturo` has no site and already holds `NYC-LT-003`. Check out another item to them. **Expected:** it works.
- [ ] **Pool checkout and partial return** (Stage 2). Open the open loan of 3 × `HS-STEREO` (Alex Sampleton, 1 returned) and choose *Return* with quantity 1. **Expected:** 1 unit is still outstanding, the loan stays open, the pool's available count goes up by 1 and a *Returned* mail is sent. Return the last unit and the loan closes.
- [ ] **No negative stock** (Stage 1). On a pool, try to check out more units than are available, or to record a loss larger than the available count. **Expected:** refused.
- [ ] **Correction with preview** (Stage 2, D1, D11). Open loan `AMS-LT-006` (Jules Sampleton) and choose *Correct*. Pick another holder and/or change the due date, then *Preview*. **Expected:** the preview lists the field changes and any conflicts, and nothing is saved yet. Enter a reason and *Commit*. **Expected:** a new *Corrected* event supersedes the latest one (the original stays visible). Mail goes to the old holder, the new holder and IT (deduplicated). Even a due-date-only correction sends mail (D1). The seeded correction is visible in the same loan's history.
- [ ] **Overlapping correction is rejected** (Stage 2). On `AMS-LT-007`'s first closed loan, try to move its return date past the start of the second loan. **Expected:** conflict, nothing saved.
- [ ] **Retire blocked while loaned** (Stage 2). Open `AMS-LT-001` and choose *Edit / change state*. **Expected:** no retire or move action, and the page says to return the asset first. The server also refuses a forged request (automated tests). Retire an available asset, for example `AMS-LT-008` after returning it. **Expected:** it is retired, its history is kept and the tag cannot be reused.
- [ ] **Site transfer blocked while loaned, allowed when available** (Stage 2). Same page: an available asset can be moved to another site, a checked-out one cannot.
- [ ] **Return to an inactive holder** (D10). Return `AMS-LT-004` (Alex Testwell, inactive). **Expected:** the return is not blocked. The holder's mail is recorded as *Skipped* (see *Mail queue*) and IT is still notified.
- [ ] **Reports and CSV** (Stage 3). *Reports → Inventory / Current loans / Overdue / History* each have filters, paging and *Export CSV*. Export the inventory and open it in a spreadsheet. **Expected:** the `AMS-LT-009` description starts with `'=HYPERLINK(` (neutralized; no formula runs). Each export is recorded in the audit log.
- [ ] **Audit log is IT only** (D2). *Audit* lists every change (checkout, return, correction, settings, exports, directory sync) with actor and time. There are no edit or delete actions.
- [ ] **Ops dashboard and mail queue** (Stage 3). *Ops* shows the app version, directory sync status, outbox counts and the worker heartbeat. *Mail queue* shows skipped and failed mail with reasons and a requeue action.
- [ ] **Directory sync history, read-only for IT** (D7). With IT only (untick Admin on `/Demo` and save), *Directory sync* shows the history but no *Run sync now* button.

### Manager / viewer

On `/Demo`, act as **Demo Manager (AMS)** (they have only the **Manager** role).

- [ ] **Sees only granted sites** (Stage 1). *Assets*, *Pools*, *Loans* and *Reports* show AMS only. `NYC-LT-001` and `SIN-LT-001` are not listed, and opening their URLs directly returns *Not found*. CSV exports contain AMS only.
- [ ] **Sees prior holders after an asset moved** (plan §1, intentional disclosure). Open `NYC-LT-007` (now at AMS). **Expected:** its full history, including the New York holder *Billie Demoson*.
- [ ] **Read only** (Stage 1). There are no check out, return, correct or edit actions, and those URLs return *Forbidden*.
- [ ] **No audit** (D2). `/Audit` returns *Forbidden*.
- [ ] **Grants belong to a person and need the Manager role** (D9). As `DEMO\you`, tick only Manager. **Expected:** the inventory pages open but are empty (no site). Act as Demo Manager (AMS) and untick Manager (as if an admin removed the role). **Expected:** no inventory at all; the AMS site stays on their row in Admin → System roles and works again when the role comes back.

### Application admin

On `/Demo`, as `DEMO\you`, tick **Admin** only.

- [ ] **Admin alone sees no inventory** (Stage 1). There are no Assets, Loans or Reports links, and their URLs return *Forbidden*.
- [ ] **Tag generator and type-specific forms.** *Assets → New asset*, choose *Laptop* and a site. **Expected:** the form previews the next tag (for example `AMS-LT-011`); leaving the tag empty creates the asset with it. Choose *Phone*: vendor and model dropdowns and two IMEI fields. Choose *Phone number*: only the phone number, provider, SIM type, SIM status, SIM number and PUC (marked IT only).
- [ ] **Asset types, fields, vendors and models** (admin). *Asset types → Phone number*: switch standard fields, change the tag pattern (the example updates), add a custom field (for example a *Choice*), move it up or down and open a new asset form to see it. *Vendors* and *Models*: add a vendor and a model for *Laptop*, then pick it on a new laptop (choosing a vendor narrows the models). Deactivating a model removes it from new forms but not from existing assets. *Sites → Tag code* sets the `{SITE}` part of generated tags.
- [ ] **Overview tiles per role** (admin). *Overview tiles* is a table of tiles with a column for IT and one for managers. Untick *Inventory by type* for IT and *Pool stock* for managers, save, then switch role (*Demo*) and reload *Overview*: the tile is gone for that role only. "Needs attention" cannot be given to managers (greyed out). *Reset to defaults* brings everything back; both actions are audited.
- [ ] **Sites and grants**. *Sites*: edit a site's timezone or directory value. *System roles*: tick NYC on the row of `Demo Manager (AMS)` and save, then act as the manager and see NYC appear. Untick it again. (*Admin → Grants* is gone: old links open System roles.)
- [ ] **System roles.** One row per person with a role or site; tick boxes for IT, Manager, Admin and Facility, and the manager's sites on the same row. *Add a person*: search `billie`, tick *Manager* and *AMS*, save: they get a row. A person without a Windows account can only get sites. Filters: by role, by site, *Show inactive people*. Your own Admin box is greyed out, and the server refuses removing your own Admin role. Every change is audited (`role.assign`, `role.remove`, `grant.add`, `grant.remove`).
- [ ] **Settings: test directory query** (WP4.1). *Settings → Test directory query*. **Expected:** "Fetched 151 entries: 149 valid, 2 would be quarantined … 2 have an unmapped site value … 3 are disabled", plus a sample. Nothing is synced.
- [ ] **Settings: test email**. Enter an address (for example `you@demo.example`) and click *Send test email*. **Expected:** success, and the message is in the demo mailbox.
- [ ] **Settings: save and audit**. Change, for example, *Overdue reminder time* or *Include phone numbers in mail* (D5), then save. **Expected:** the change applies without a restart and is audited as `setting.update` (visible to IT). The SMTP password field is write-only.
- [ ] **Directory sync with issues** (Stage 1, D7). *Directory sync → Run sync now*. **Expected:** a *Succeeded* run with *fetched 151, quarantined 2*. The issue list shows `duplicate-id` (demo-duplicate-0001) and two `unmapped-site` values. The 3 disabled accounts are inactive.
- [ ] **Anonymize an inactive person** (privacy). *Privacy*: search `kai.demoson` or `frankie.sampleton` (inactive, no open loans), enter a reason, tick *I confirm* and click *Anonymize*. **Expected:** the person becomes `anonymized-<id>` and the action is audited. `alex.testwell` is refused until their open loan is returned.

### Super admin (account outside AD)

Open `/superadmin` (the link is on `/Demo`). Signing in replaces the demo identity in this browser until you sign out.

- [ ] **Sign in.** Username `superadmin`, password `demo-superadmin-2026`. **Expected:** you land on *Admin → Settings*; every page shows the banner *Signed in as the super admin (not an AD account)*; the user card says `superadmin (local)` with role *Admin*.
- [ ] **Admin pages work.** *Settings*, *System roles*, *Sites*, *Directory sync* (including *Run sync now*), *Asset types*, *Vendors*, *Models*, *Overview tiles* and *Privacy* open and save. Changes are audited as `superadmin (local)` (switch to IT later to read the audit log).
- [ ] **Last admin.** On *System roles*, untick *Admin* on the row of Demo You (the only admin) and save. **Expected:** the warning *Nobody has the Admin role any more. From now on only the super admin can give roles.* Tick it again (or *Reset demo data*).
- [ ] **No IT pages.** `/Assets`, `/Loans`, `/Reports`, `/Workplace`, `/Ops` and `/Audit` return *Forbidden* (403); the sidebar has no links to them.
- [ ] **Wrong password.** Sign out, then try a wrong password. **Expected:** *Sign-in failed. Check the username and password…*, never which part was wrong. After **5** wrong tries the account is locked for 15 minutes: even the right password then gives the same message, and the demo mailbox gets one *"the super admin account is locked"* mail. *Reset demo data* (or waiting 15 minutes, or *+1 day*) clears it. More than 10 tries from one browser within 15 minutes give *Too many sign-in attempts*.
- [ ] **Sign-in mail.** Each successful sign-in puts *"Asset Tracker: the super admin signed in"* in the demo mailbox (with the IP address).
- [ ] **Change the password.** *Account and password* in the banner: the current password is required, the new one needs 16+ characters and cannot be the demo password. You stay signed in; a second browser signed in as the super admin is signed out.
- [ ] **Sign out.** *Sign out* in the banner returns to `/superadmin` and this browser is the demo user again.
- [ ] **Session limits.** *+1 day* on `/Demo` (while signed in as the super admin) ends the session: the next page asks you to sign in again (30 minutes idle and 8 hours at most).

### No role

- [ ] Untick all roles on `/Demo` and save. **Expected:** the home page says you have no access, and every inventory, admin and audit URL returns *Forbidden* or *Not found*. `/Demo` stays reachable so you can switch back.

### Workplace (floor plans)

Delete the old demo database first (`%LOCALAPPDATA%\AssetTrackerDemo`): this module adds tables.

- [ ] **Floor plans.** *Workplace → Floor plans* lists *Floor 1*, *Floor 2* and *Floor 3* of Amsterdam and *Floor 1* of New York. Open *AMS Floor 1*. **Expected:** a map of four offices, a boardroom, a canteen and a print room; the *Finance*, *Engineering* and *Service desk* seats are coloured with a dashed outline and the zone name; the side panel shows the free desks, desks taken and what to check, and lists the three zones.
- [ ] **A big map.** **Expected:** the map fills the window below the top bar and the page header (at least 480 px high) and opens zoomed to the rooms and desks, without the empty paper around the drawing; the side panel scrolls on its own. In the map's top-right corner: the zoom level (*100%* = the whole floor), zoom in, zoom out, *Fit*, *Select area* and *Full screen*; the search, *Zones*, *Names* and *Print / PDF* stay above the map. Double-click a room: the map zooms to it; *Fit* (or `0`) goes back. Click the map and use the arrow keys to move it, `+` / `-` to zoom. At *Fit* a wide floor such as *AMS Floor 1* fills the map without big empty bands above and below it (the map is no taller than the drawing needs, but at least 480 px). Press *Full screen*: the map takes the whole window (header, menu and panel gone); click a desk and the desk view comes back as an overlay on the right. **Esc** undoes one thing at a time: print-area mode, then search highlights, then the desk view or selection, then full screen (in a search box Esc first closes its list).
- [ ] **Names and tags at Fit.** On *AMS Floor 1* at *Fit* (desktop and phone width, light and dark theme). **Expected:** every placed person has a name next to the desk, shortened when space is tight (*Gabi Demoson* → *Gabi D.* → *GD*); desktop tags appear where they fit, without the site prefix when needed (`AMS-DT-003` → `DT-003`); nothing overlaps, and no text is smaller than 10 px. Hover a desk: the tooltip has the full name and tag. Zoom in: the full names and tags come back. Zone names move above the labels or below their tables when a label is where they were (or are left out when there is no room; the colour and the panel still show the zone).
- [ ] **A crowded floor.** Open *AMS Floor 3* at *Fit*. **Expected:** short names and initials, and where not even initials fit, a dark numbered circle next to the desk; *Numbers on the map (n)* in the bottom-left corner of the map opens the list (number, full name, tag). Two desks whose numbers would touch share one number (the list shows both). Nothing overlaps. Zoom in or press *Full screen*: the numbers turn back into names. On a phone the floor is mostly numbers until you zoom in.
- [ ] **Select seats.** Click a seat: the desk view opens (see below); no seat number or code is shown anywhere. Click a table: the panel says *A whole table, 6 seats, in …*. Shift-click adds seats; click a table or a room to select all its seats; drag with **Shift** (or press *Select area* and drag) to select an area. Scroll or use + / − / *Fit* to zoom, drag to move.
- [ ] **Find on the map.** Type `sales` in the search box above the map: the *Sales* room (and, on other floors, matches there) is listed with its floor; pick it. **Expected:** its seats are highlighted and the map zooms to them. Type `Open space` on the *Floor plans* page and pick a result: it opens that floor with the seats highlighted. A person's name or a desktop tag finds their desk (*Desk in Finance*); the drawing's seat codes (such as `1.04-T01-S02`) find nothing.
- [ ] **Zone for a table.** In the *Sales* room click a table (not a seat). Under *Zone* choose *New zone…*, type `Marketing` and press *Set zone for 6 seats*. **Expected:** the table stays selected, its seats take the next colour with a dashed outline and the name *Marketing*; *Free desks* lists *Marketing · Sales*. A name already used on the floor is refused.
- [ ] **Split a table.** Click one seat on the other side of that table and Shift-click the rest of that side (or Shift-drag over them). The panel says *3 of the 6 seats of a table*. Choose *New zone…* `Design` and set it. **Expected:** the table has two outlines, *Marketing* above and *Design* below it. Select a seat and choose an existing zone or *No zone* to move it. Under *Zones* open *Edit*: rename, recolour or delete a zone (seats stay on the floor).
- [ ] **Import a floor plan.** *Workplace → Import floor plan*: choose a site, a name and an SVG. A drawing in the facility team's format (rooms, tables and seats with `data-kind`) gives one workspot per seat at once. Try a plain SVG without seats: the page asks you to click the shapes that are seats, press *Mark as workspots* and they become seats. Scripts, links and images in an uploaded file are removed.
- [ ] **Replace a drawing.** *Replace drawing* with a revised SVG of the same floor: the message says how many workspots were added and switched off; zones and placements stay, seats that disappeared are switched off (greyed), not deleted. Replace it again with the original and they come back.
- [ ] **A revised drawing keeps everything.** Place someone on *Floor 1* and give a table a zone. Export the same drawing again with other element ids (for example replace every `id="f1-` with `id="v2-`), or with every coordinate inside a `<g transform="scale(2)">` and a viewBox twice as large, and use *Replace drawing*. **Expected:** the message says *0 added, 0 switched off, N found again under a new id or in a slightly different place*; the person, desktop, screens and zone are still at the same seats and the icons sit exactly on the seats. Remove a seat that has someone on it: the message says it is flagged, and *To check* lists the person at a switched-off seat until they are dragged to another desk.
- [ ] **What sits at a seat.** On *AMS Floor 1* zoom in on the *Finance* room. **Expected:** next to each seat its person's name and desktop tag; a person icon in a taken seat, a desktop and a screen badge beside it, a ring around seats to check, free desks green. Click the first seat: the desk view shows *Gabi Demoson*, the desktop `AMS-DT-001` and the screens `AMS-SC-001` and `AMS-SC-002`, each with *Release* (person and desktop also with *Replace*). Hover a seat: the tooltip shows its label, person, desktop and number of screens.
- [ ] **At a glance and to check.** The side panel shows the free desks, desks taken and *To check*. **Expected:** to check lists the leaver (*Alex Testwell*, Engineering: *no longer works here*), a Finance desk without a screen, one without a desktop and screen, and an Engineering desk without a desktop. Each line names the zone and room. Click a line: that desk is selected. Under *Free desks* each zone or room is listed with its number of free desks; clicking one shows those desks. Click the *free desks* or *to check* counter: those seats light up on the map; click again to turn it off. A desk with only a screen and nobody is not flagged. Canteen and boardroom seats are *not a desk*: they are grey and do not count as free.
- [ ] **Desk view.** On *AMS Floor 1* click a free desk in *Sales*. **Expected:** the map zooms to its table and keeps the desk highlighted; the panel shows *Desk*, the room and zone (*Sales*, or *Finance · zone Finance*; never a desk number) and three slots, *Person*, *Desktop* and *Screens*, with the cursor already in the person box. Click into an empty box (or press ↓): it lists what is not placed yet at AMS.
- [ ] **Seat a table with the keyboard only.** In the person box type `Alex Demo` and press **Enter**: Alex is placed (a message says so, the map updates and keeps its zoom) and the cursor moves to the desktop box. Type `DT-004`, **Enter**: the desktop is placed and the cursor moves to *Screens*. Type a screen tag, **Enter**; type another to add a second screen. Press **Enter** in the empty box: the next free desk at the same table opens (then the same room, then the floor) with the cursor in the person box. Repeat for the whole table. *Previous desk* goes back to the desk before.
- [ ] **Suggestions.** Type `Testwell` in a person box. **Expected:** the list is grouped: *Not placed yet* (people of this site first, then visitors of other sites, who can be placed), *Placed elsewhere* (with where they are, *Floor 1 · Finance – move here*), *At this desk* (marked, not pickable) and *Cannot be placed here* (people no longer in the directory, marked). Pick someone placed elsewhere: they **move** here (the message says from where). In a desktop or screen box only desktops or screens are offered (never laptops); type `NYC-`: assets of New York are listed greyed with *Belongs to site NYC: transfer it first* and cannot be picked. Nothing is ever suggested from loans.
- [ ] **Replace and release.** At a desk with a person press *Replace*, type another name and press **Enter**: one message (*Released …; … placed at a desk in …*), the old person is free again (back in the lists) and the audit log has a *release* and an *assign*. *Cancel* or Esc leaves the Replace box. The same for the desktop; screens have no *Replace*, you add them. *Release* on a screen: *Released AMS-SC-… from this desk.*, it is back in the list. There is no Undo.
- [ ] **Move from the desk view.** Drag the person's card in the desk view onto another free desk: they move there. Dragging on the map itself still pans.
- [ ] **Place from a list.** Below the desk view *Place from a list* is collapsed. Open it: tabs *People*, *Desktops* and *Screens*; every row has the icon, the name and a muted second line, all left-aligned. Select a desk and click an entry: it is placed there. Drag an entry onto a desk: it is placed there.
- [ ] **Drag and drop.** As IT or facility, open *Place from a list* (tabs *People*, *Desktops* and *Screens*). Drag *Harper Demoson* onto a free desk. **Expected:** the seat lights up while you hover, a message confirms it, the map updates without reloading (zoom stays) and Harper leaves the list. Drag the spare desktop `AMS-DT-004` (desktops tab) onto the same desk, then a screen. Drag a second desktop onto it: refused ("already has a desktop"). Drag someone onto a canteen seat: refused ("not a desk"). Drag Harper's card from the desk view onto another desk: she **moves**. Alternatively select a desk and click an entry in the list.
- [ ] **Only desktops and screens.** The desktops and screens tabs list only assets of types with that floor-plan role (Admin → Asset types → *On floor plans*); a laptop or headset is never offered. Searching people with a term also finds people of other sites; assets of another site are not listed.
- [ ] **Release.** Select a seat and press *Release* next to the person, desktop or a screen: it disappears from the seat and is back in the list. A seat with something placed cannot be switched off or made *not a desk* (*Seat settings*).
- [ ] **Seat settings.** Select a canteen seat and press *Make desk*: it now counts as a free desk and takes placements. *Not a desk* turns it back.
- [ ] **Retire or transfer releases.** Retire a placed desktop (or transfer it to New York): it is no longer on the map and the person's desk is flagged *no desktop*.
- [ ] **Find a person or an asset.** In the map search type a person's name or an asset tag: the result says on which floor and at which seat, and picking it highlights that seat (on another floor it opens that floor). Placed things are found, unplaced ones are not.
- [ ] **Show on map.** Open the asset `AMS-DT-001` (*Assets*): *On the floor plan* links to its seat; an unplaced asset says *Not placed*.
- [ ] **Print to PDF.** Open *Print / PDF* above the map and choose *The whole floor*. **Expected:** a new tab with an A4 landscape sheet (the plan itself, without the paper margins of the drawing; legend; header with floor, counts, paper size and who printed it) opens the browser's print dialog; *Save as PDF* gives the map on page 1 and a plain table on the next page: **Person, Zone, Desktop**, one row per person, sorted by zone (no zone last) and then name. No rooms, screens, notes or free desks. On the map every name and tag is at least 7 pt on paper, shortened where needed, and nothing overlaps. Zoom into a part and choose *What you see now*; select some seats and choose *The selected seats*; choose *Draw an area…* and drag a rectangle: each prints only that section and lists only the people in it.
- [ ] **A4 or A3.** The print page has *Paper: A4 | A3* (A4 by default); the hint names the chosen size (*A4 landscape* / *A3 landscape*). Press *A3*: the sheet becomes larger, the labels get more room, and *Save as PDF* gives an A3 landscape page in Chrome and Edge (in Safari choose A3 in the print dialog yourself: the hint says so). When names only fit as numbers on A4 (a print whose area is much larger than the floor shows it, for example `/Workplace/Print/{id}?area=-600,-300,1800,900` for AMS *Floor 3*), A4 shows *This floor is easier to read on A3* with a link to switch, and a key to the numbers under the map. It never switches to A3 by itself.
- [ ] **Facility and manager.** As *Max Demoson* (Facility role) you can drag, place and release. As *Demo Manager (AMS)* you see the map, names, status and can print; clicking a desk opens the same desk view **read only** (cards, no search boxes, no *Replace* or *Release*), and there is no *Place from a list* or seat settings.
- [ ] **Facility role, given in the app.** As the demo admin open *Admin → System roles*: *Max Demoson* has the Facility role. On `/Demo` act as *Max Demoson (… roles: Facility)*. **Expected:** the menu shows only *Overview* and *Floor plans*; floors can be imported and edited; Assets, Loans, Reports and Admin return *Forbidden*. Back as admin remove the role: Max loses access at once. Give it again to someone else by searching their name.
- [ ] **Manager (read only).** As *Demo Manager (AMS)* with only the Manager role: the floors of Amsterdam are listed and can be searched, but there is no *Import*, *Replace* or zone editing, and the New York floor is *Not found*.

### Time, overdue and reminders (D4)

- [ ] **Due today counts as overdue (0 days).** *Loans* shows `AMS-LT-002` as "due today". The *Overdue* report includes it with 0 days overdue.
- [ ] **Per-site calendar.** `/Demo` shows each site's local date. Singapore is usually a day ahead of New York. Due dates and the overdue rule use the loan's own site.
- [ ] **Reminders after time travel.** Clear the mailbox, then click **+7 days**. **Expected:** within about 10 s the mailbox shows *Overdue* reminders. There is one per open overdue loan per recipient (holder + IT), only for sites whose local time is past 08:00. For example, `AMS-PH-001` (due in 3 days) is now overdue. The inactive leaver's loan (`AMS-LT-004`) gets an IT-only reminder that says the holder was not emailed. Undated loans get none.
- [ ] **Once per day per site.** Wait a few minutes (the reminder job runs every minute) and reload the mailbox. **Expected:** no duplicate reminders for the same day. Then click **+1 day**. **Expected:** exactly one new reminder per overdue loan and recipient for the new day.
- [ ] **Reset to now** returns the clock to real time. Mail queued "in the future" is still delivered.

### Operations

- [ ] `/health` returns 200 (liveness). `/health/ready` returns JSON with `database`, `directory-sync`, `mail-outbox` and `worker`, all *Healthy* (D3: a stale worker would be *Degraded*).
- [ ] Security headers: in the browser developer tools, every page has a strict Content-Security-Policy and the console shows no CSP violations.

### What the demo cannot show (covered by automated tests)

These run in `dotnet test` (all tests run against a real relational database):

- Two people checking out the same laptop, or overselling a pool, at the same instant (Stage 1/2 concurrency): `LendingServiceTests`, `PoolServiceTests`.
- Relay outage, retry with backoff and exhausted mail (Stage 2): `OutboxSenderTests`. In the demo the relay never fails.
- A failed or rejected directory sync and its IT alert (D6): `SponsorDecisionSyncTests`. The demo directory always answers.
- A stale or stopped worker (D3): `HealthEndpointTests`. In the demo the jobs run in-process.
- DST transitions and timezone edge cases (D4): `OverdueReminderTests`, `SiteCalendarTests`.
- Parallel super admin sign-in attempts (only 5 ever reach the password check), the per-IP limit, a password reset or disable ending open sessions, the 30-minute and 8-hour session limits, and the setup command (`hash`, `apply` for every action, the requester from the file owner): `tests/AssetTracker.Tests/Security/`.
- Windows sign-in, Kerberos over VPN, SQL Server, IIS (anonymous + Windows authentication for `/superadmin`), the setup script's scheduled task under the gMSA, the real relay and AD: only on a real server (below, and runbook §15).

## 8. Testing on a real Windows server

The demo does not replace the Stage 0 preflight and pilot. To install and test on a domain-joined Windows server with IIS, SQL Server, AD and the internal relay, follow [`docs/runbook.md`](runbook.md): prerequisites (§2), DNS/SPN/Kerberos (§3), SQL logins (§4), install (§5), configuration reference (§6, plus [`deploy/README.md`](../deploy/README.md)) and the **first-run checklist (§7)**, then health and monitoring (§8–9). Production never uses demo mode: `Demo:Enabled` outside the Development environment stops the app at start-up.
