# Client asset tracker — finalized MVP implementation baseline

**Status:** Product decisions confirmed with the sponsor; client-environment preflight remains. This is a **plan, not authorization to build or deploy**. The target is a custom, low-maintenance, browser-based asset tracker for approximately 500–5,000 people/assets across multiple sites, running on domain-joined Windows Server and reachable only on the internal network and approved company VPN.

## 1. Scope and decisions

- A **custom application**, not an adapted existing product. Windows Server only: no Linux VM or containers.
- On-premises Active Directory (AD) is authoritative for login, people, job titles, email addresses, active state, and site. Managed domain-joined Windows clients use browser integrated authentication, including across VPN. **Scheduled AD sync replaces the initially requested user CSV import**; a user CSV upload is explicitly *not* an MVP requirement.
- Types: desktop, laptop, phone, phone number, peripheral, webcam, headset. Every individually tracked asset has an immutable unique tag. Active phone numbers and IMEIs, when present, must be normalized and unique. Physical serial numbers may be recorded when applicable.
- There are **two inventory modes**: individually tagged assets (one current holder at most), and site-specific quantity pools (SKU + site, available/total quantities) for peripherals, webcams, and headsets only. Pools may have multiple concurrent borrower assignments, each with a quantity. This **supersedes** the earlier blanket statement that every inventory record has just one holder; the same checkout/return *workflow* applies, but pooled stock has quantity semantics. No untagged desktops, laptops, phones or phone numbers.
- IT/editor group can view/manage inventory and checkout/return across sites. Manager/viewer group can read items at granted sites; one manager may have multiple site grants. Application-admin group handles settings and site grants, but **does not inherit IT inventory powers**; an admin who needs both belongs to both groups. Role membership comes from AD security groups; application admins assign manager-to-site grants in the app. No global directory browsing for managers.
- An individual asset's **current site** governs manager access to its inventory and **entire assignment history**. A manager at its new site can see pre-transfer holders, including their full synced person details; this is an intentional disclosure decision. Pool assignments and stock events remain attached to their site. A holder may live at another site; a change to the holder's AD site never automatically moves the asset. Moving an individual asset's site while it has an open loan is blocked.
- Checkout and return emails go to the affected holder and one application-admin-configured IT mailbox/distribution list via an approved internal relay to Microsoft 365. Checkout requires an active recipient with an email address; a relay outage does not undo a valid checkout. Due dates are optional. Open overdue assignments produce **one reminder per assignment per day** to holder and IT until returned; for an inactive or mail-less holder, send only to IT. Undated assignments generate no overdue reminders. No native XLSX: Excel-compatible CSV exports are sufficient.
- No borrower signature, approval process, asset bulk import, native XLSX, or attachments in MVP. Individually tagged devices are created in the UI; IT can create and adjust pools with audit.
- People missing or disabled in AD keep all existing and historical loans, but cannot receive new loans when the latest successful snapshot marks them inactive. A failed sync preserves the last good snapshot; stale sync warns IT and is logged on checkout, **but does not block** lending.
- IT can correct open or returned loans using a required reason and a **superseding event** that preserves the original. Corrections that would create overlapping assignments are rejected until the timeline is reconciled. Material holder/return corrections notify the old and corrected holders plus IT, deduplicated by recipient; email to a no-longer-valid or missing address follows the exception handling in section 4.
- The sponsor selected **indefinite retention until the client instructs deletion**, without a separate privacy-review approval gate, and full holder details for managers who can see the asset. This is a client policy risk, not a recommendation: record the decision and its owner before go-live, restrict exports/backups, and implement a documented deletion/retention-change procedure. Applicable privacy obligations remain the client's responsibility and may still constrain deployment.

## 2. User-facing MVP

1. **Inventory:** IT creates, edits, searches and retires individually tagged assets; chooses type, site, description/model, condition, optional serial/IMEI, and normalized phone number when applicable. An internal immutable ID anchors history; the asset tag is unique and immutable. No hard deletion of loan history. Returning a unit makes it available by default; IT may mark it unavailable for repair or retirement. Retirement requires no open loan.
2. **Pools:** IT creates one pool record for each unique SKU/site pair for an eligible category. A pool displays available and outstanding quantities. IT records receipt/loss/count adjustments with required reason; each adjustment is an auditable stock event. IT may transfer **available** units to a different site's pool with one linked source/destination transfer; outstanding units and loan history stay at their original site. No negative on-hand stock.
3. **People:** read-only scheduled full AD sync (paged), plus admin manual rerun. Stable AD object identity is the key: renaming a username does not create a second person. In-scope active/disabled state, username, name, job title, email, and designated site attribute are mapped to a local snapshot. Unmapped site, missing identifier, duplicate, failed query or unexpectedly small result is reported; a bad/incomplete run must not deactivate the whole directory. `DirectorySearcher` supports paged AD queries; unpaged searches can be limited to a server default of 1,000 results.[3]
4. **Checkout:** IT selects an available tagged asset or an available quantity from a pool, chooses an active person with email, optional due date, condition and note, then confirms. Server checks current permissions, snapshot status and quantities in the same transaction as the loan, audit and mail-outbox records. Concurrent requests must not loan the same tagged unit twice or take a pool below zero.
5. **Return:** IT records return time, condition, note, and (for a pool) an amount up to the still-outstanding quantity. Partial returns leave the remaining quantity open and still overdue if past due. A tagged asset returns to available unless marked unavailable. Due dates use an agreed client timezone and calendar-day rule established in preflight.
6. **Corrections:** IT selects a prior loan/return, enters corrected fields and a reason, sees any effects on open quantities/timeline, then commits a superseding event. No event is deleted. If old and new holder differ, both receive a correction notice, as does IT. Validation disallows temporal overlap, impossible stock balances, or unauthorized changes; history and reports use effective corrected state while showing the original and correction link to IT.
7. **Reports:** inventory by site/type/status and pool balances; current individual/pool loans with borrower and quantity; overdue items; full loan and correction history; filters and server-paginated lists. Export inventory, current assignments, overdue, and history as CSV. Managers see and export only authorized sites, but can see the full person snapshot for holders linked to those assets; an admin-only user sees no inventory/export. Exports are audited and spreadsheet-formula-safe.
8. **Notifications:** checkout/return/correction records create recipient-specific outbox rows atomically. A Windows worker sends and retries with backoff, exposes pending/failed counts and last error to IT, and permits manual requeue. A daily job creates one overdue reminder per *open assignment* per local calendar day, even if multiple pooled units remain. Use a uniqueness key for assignment/day/recipient/event so job restarts do not queue duplicates. SMTP timeouts after relay acceptance can still yield duplicate mail; do not claim exactly-once delivery.

## 3. Access and workflow invariants

| Actor | Read / export | Inventory and loan changes | Settings / grants |
| --- | --- | --- | --- |
| IT/editor | All sites, including people linked to loans | Create/edit/retire individual items; pool receipt, loss, adjustment and transfer; checkout, partial/full return, audited correction and asset transfer | No |
| Site manager/viewer | Only granted current asset sites or pool sites; full holder details when linked to accessible history; no global people search | No | No |
| Application admin | Admin settings/grants only unless also in IT/manager AD group | None by admin role alone | Yes |
| Other AD user | None | None | None |

Authorization must be enforced in service/query/export paths and tested against direct URLs and manipulated requests. AD group removal must revoke the application role promptly on a new request/session; exact token-cache behavior is tested in preflight. A manager-site grant has no effect if the principal is no longer in the manager AD group. Role overlap is a union of explicitly assigned roles, never an implied admin override.

**Tagged asset state:** available → checked out → available or unavailable; unavailable → available by explicit IT action; retirement only with no open loan. Unique constraint on one effective open loan per tagged asset. **Pool state:** `on_hand = available + outstanding` for each site/SKU; checkout decreases available/increases outstanding; partial return reverses the specified amount; receipt/adjustment/transfer changes on-hand with auditable event. Open pooled loan quantity must be positive, and no transaction may make available negative. A pool is not an individually tagged unit; its quantity must be prominent in every checkout and report. Retired or disabled people remain searchable for resolving open loans, but cannot be chosen for new ones.

**Correction model:** append superseding events with reason and actor; compute effective loan state deterministically in the transaction. If a historical correction collides with a later loan or would make stock inconsistent, reject with a clear conflict for IT to resolve rather than silently rewriting other people’s transactions. Notifications reference the effective correction event, not the original event.

## 4. Architecture and data

```text
Managed Windows browser — internal LAN / approved VPN
      │ internal DNS + HTTPS, firewall and VPN ACL
      ▼
IIS on domain-joined Windows Server
      │ ASP.NET Core server-rendered UI; Windows Authentication;
      │ server-side policies, CSRF protection, CSV endpoints
      ▼
SQL Server Express (approved Windows host)
      │ People, sites, assets, pools, loans, stock ledger,
      │ grants, audit, sync runs, notification outbox
      ▲                      ▲
      │                      │
read-only on-prem AD     Windows Worker Service
(people/groups/site)     (scheduled AD sync, daily reminders,
                        mail-outbox retry)
                               │
                        approved internal mail relay → Microsoft 365
```

ASP.NET Core on IIS supports integrated Windows Authentication in intranet AD environments, and the .NET Hosting Bundle installs the IIS module and runtime.[1][2] Use a supported .NET LTS compatible with the client's server OS; validate IIS Windows Authentication, SPN/Kerberos, browser integrated sign-in and VPN routing before committing to implementation. HTTPS with an internal trusted certificate; no public ingress. IIS/host firewall plus VPN ACLs permit only approved sources. No separate end-user password database. A managed service account is preferred if permitted; grant read-only AD query, least-privilege integrated SQL access and restricted relay submission. Validate CSRF protection, output encoding, HTTPS/cookie policies, and CSV formula neutralization. Do not use an external mail provider directly when an approved internal relay is available.

Proposed relational entities: `Person` (stable AD ID; synced snapshot, active state, site); `Site` (site key and AD-value map); `IndividualAsset` (immutable tag, type, site, state, unique normalized phone/IMEI where present); `Pool` (eligible type, SKU, site); `Loan` (inventory reference, holder, quantity, start/due/end, conditions, actor, effective version); `LoanCorrectionEvent` (prior event link, reason, effective change); `StockEvent` (receipt, loss, correction, transfer with linked counterparty); `ManagerSiteGrant`; `AuditEvent`; `NotificationOutbox`; `SyncRun`. Represent one effective open loan per tagged asset with a database-enforced uniqueness rule; lock/serialize pool balance updates transactionally. Index by site, current loans, due date and AD identity. Historical borrower fields are snapshotted on loan events while current UI may also show updated AD details. Design manager history queries against **current individual asset site**, not historical site.

SQL Server Express can run as a Windows service without a second database platform. The SQL Server 2025 edition table states Express has a 50 GB relational database cap, 1,410 MB buffer-pool limit, one-socket/four-core compute cap, and no SQL Server Agent; verify the permitted installed version before using its limits for sizing.[4] Use the application worker rather than SQL Agent for sync, reminders and mail. Never treat Express capacity as a concurrency guarantee: test actual report and CSV loads at the client's expected size.

**Mail failure distinctions:** no email on a new recipient → reject checkout; relay unavailable after commit → retry and alert IT; inactive/missing email on an overdue existing loan → notify IT only; correction with a previous or corrected holder whose address is absent/invalid → record a skipped-recipient reason, notify IT, and flag to IT rather than erase the correction. Never block a legitimate return to an inactive person. Limit retries and expose exhausted failures; do not lose pending messages on app restart.

## 5. Operations and risk posture

- Scheduled paged full AD sync with manual rerun; log start/end, fetched/mapped/quarantined counts and last successful time. Define a threshold for an unexpectedly low result. Preserve the last good snapshot on failure and show **stale data** to IT and in checkout audit while continuing to allow lending as the sponsor directed. This carries a known offboarding risk: a recently disabled AD account may remain eligible until a good sync; alert the service owner and establish an escalation path.
- Use the client's **existing backup platform**; no extra app backup scheduler. Back up the SQL database consistently and application/worker configuration together; preserve certificates/relay configuration and a deployment artifact or reproducible build. A restore drill must verify inventory, open loans, stock balance, role grants, and queued mail without sending unintended duplicate notifications. Client IT must state recovery-point and recovery-time targets; none are invented here.
- Monitor IIS/worker uptime, database growth and free disk, age/count of pending or failed mail, last successful AD sync, sync-count anomalies, backup success, and certificate expiry. Route actionable alerts to a named client owner. Document Windows/.NET/SQL security patch responsibility, app release/rollback, migration testing and an explicit support contact.
- Indefinite personal-data history plus full previous-holder disclosure after an asset moves sites is a **consciously accepted design risk**, not a data-minimization best practice. Store the sponsor's decision and require a deployable mechanism to export/delete/anonymize data under client direction without breaking accounting/audit invariants; backups and CSV exports must follow the client's privacy controls. No separate review gate was requested, but do not represent this decision as a legal conclusion.
- Retain server-side mutation and export audit with actor, timestamp, site, entity, reason and correlation ID. App admins cannot silently alter audit. Define who may view audit details and who may request deletion through operational policy.

## 6. Delivery sequence and acceptance gates

| Stage | Deliverable | Must pass before the next stage |
| --- | --- | --- |
| **0. Client preflight** | Actual Windows Server/SQL versions and permitted topology; scoped AD query/group/site mapping; Kerberos/VPN browser proof; HTTPS cert, LAN/VPN ACL, relay test; backup platform and recovery targets; privacy-decision owner and timezone | Representative AD sync sample, LAN and VPN SSO, restricted mail relay send, SQL-aware backup/restore path, agreed site and person mapping |
| **1. Domain and access** | Schema/migrations, individual assets, pools/stock ledger, immutable tags and normalized identifiers, AD people sync, AD-group role policy, manager site grants, audit | Competing checkout tests; zero negative pool balance; no cross-site manager view/export; AD rename/disable/failed run; admin role alone cannot read inventory |
| **2. Lending and communication** | Tagged/pool checkout and return, partial return, superseding corrections, unavailable/retirement/site transfer rules, durable outbox and daily overdue job | Missing email blocks checkout; relay outage does not lose loan/mail; correction does not overlap later loan; previous/new holder emails; daily reminder dedupe; inactive-holder IT-only reminder |
| **3. Reporting and pilot** | Search/pagination, four CSV exports, inventory/overdue summary, operational dashboard/runbook, deploy/rollback, support handover | Site-scoped reports and safe CSV, multi-site pilot, SSO over VPN, restore drill including pending outbox, worker restart, sync outage, backup and alert ownership signoff |

**Test at the highest feasible seam:** run browser-to-app-to-test-SQL integration tests with a replaceable AD/relay adapter and simulated Windows principals; isolate only external directory/relay boundaries. Unit tests complement rather than replace concurrency and authorization integration tests. Include authorization on every read/export, revoked roles, site moves exposing prior holders, pooled stock receipt/transfer/loss/partial return, concurrent lending, overdue time boundaries, corrected history, role combinations, stale AD status, notification retries/idempotent creation, malicious CSV cells, schema migration and restore. Use representative multi-site fixtures without real personal data.

## 7. Client facts that remain blocking before a production build/deployment

1. Windows Server version, SQL edition/version and whether SQL is co-located or on an approved Windows database host; compatibility and licensing approval.
2. AD search scope/OU, stable ID field, group names, exact site attribute and mapping values, disabled-user semantics, service account method, sync frequency and low-count alert threshold.
3. Client VPN routes/source ranges, internal DNS name, certificate issuer, browser policy, SPN and Kerberos proof across LAN/VPN.
4. Internal mail relay address/port/TLS/auth, sending identity, permitted recipients, IT mailbox/list and alert owner. Specify whether reminder email can contain asset tag and phone number.
5. Client backup platform's SQL-aware procedure, restore owner, RPO/RTO, patch/monitor ownership, retention/deletion instruction authority, and actual timezone/overdue cutoff.

**The product design is complete enough to create development work packages now.** Stage 0 facts are integration gates, not reasons to guess production values. Do not provision infrastructure or implement the app until separately authorized.

## Sources

[1] https://learn.microsoft.com/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0
[2] https://learn.microsoft.com/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0
[3] https://learn.microsoft.com/dotnet/api/system.directoryservices.directorysearcher?view=net-10.0
[4] https://learn.microsoft.com/sql/sql-server/editions-and-components-of-sql-server-2025?view=sql-server-ver17
