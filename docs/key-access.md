# Keys & digital access

Facility → **Keys & digital access** (`/Keys`) manages physical keys and records digital door/lockbox permissions.
Facility is required in pages and services. Admin, IT and Manager alone do not grant access. Facility covers all sites, matching Workplace. Admin → System roles assigns it.

## Physical keys

1. On **Buildings & keys**, add a building within an ATS site, then its rooms. Rooms exist without drawings; optional floor/drawing-room references link the register to Workplace.
2. Create one key definition per pattern. Assign its direct rooms on the detail page.
3. Add subordinate definitions to a master. A master opens the union of direct rooms and all descendants' rooms. Multiple masters may share a subordinate. Self-links, cycles and cross-building relationships are rejected.
4. Review effective rooms, subordinate room lists and outstanding copies before changing access. The previous graph/outstanding loan IDs stay in the same audit transaction; checkout stores effective room IDs at issue.
5. Register each physical copy with a unique tag and storage location. Its optional home lockbox must be in the same building. Available copies are recorded as stored; issued/lost copies are not treated as present. A reservation keeps a copy physically in stock while preventing another loan.
6. On **Lend & return**, search a borrower, select a copy and review every room it opens. Filtering by required room sorts narrowest access first. Borrowers must be active, not anonymized and have valid email. Enter a due date or leave it empty for a long-term issue.

Due dates follow the building site's timezone and ATS's existing rule: due/overdue begins at the start of the due date. One open loan/reservation per copy is enforced by a filtered unique index. Access confirmations carry a database revision: refresh/review if information changed after loading the page.

**Return reported** does not free stock: Facility must physically verify return and record condition. **Lost** keeps responsibility open; the definition page shows room impact. **Close loss case** ends responsibility without recording return or making the copy available. A recovered copy needs an explicit verified-copy update. Extensions require a reason. Records are retained rather than deleted; archive guards protect outstanding dependencies.

## Digital locks and lockboxes

This release uses **manual manufacturer management**. It does not connect to hardware, generate/deliver PINs, open locks or supply a mobile app. Configure Bluetooth permissions and individual PINs in the manufacturer's system; deliver credentials through its secure channel. ATS stores operational references, never PINs, provider passwords or activation links.

Register manufacturer, model, external ID, opening methods (Bluetooth app and/or PIN), location and type. Door locks require a room. Lockboxes may have a location room and hold multiple registered key copies. Permissions belong to existing active ATS users, with a method, reason and UTC start/expiry instants. Browser datetime fields explicitly mean UTC, independent of the host timezone.

| State | Meaning |
| --- | --- |
| Pending | Facility must configure access and expiry in the manufacturer app. |
| Confirmed | Facility verified activation/configured expiry and recorded a reference. Displayed as Scheduled, Active or Expired according to the window. |
| Revocation pending | Removal requested, including after expiry/deactivation. Actual device access may remain. |
| Revoked | Facility verified removal and recorded a reference. Cannot reopen; create a new grant. |
| Failed | Provisioning failed or provider state is uncertain. Failed revocation remains Revocation pending. |

Expiry never claims verified removal. An independent worker checks expired/inactive access every minute and requests revocation. Offline Bluetooth locks may retain credentials until synchronisation: verify the provider/device state before completing revocation. The overview flags expired/failed/inactive access even when the worker is stopped.

Recurring schedules, one-use PINs, provider opening logs and automatic provisioning are not implemented. A first integration requires the selected manufacturer/model, supported API/SDK, authentication configuration and event/credential delivery design. Keep request and hardware-confirmation states separate. A box opening must not prove key collection/return without key-identifying hardware.

## Lockbox collection and room lookup

Reserve an available copy stored in a lockbox, with a UTC collection window and method. This atomically creates a key reservation and pending digital permission. Configure/verify access on **Digital locks & lockboxes**. Facility explicitly verifies collection within that permission's window to start the physical loan; collection requests revocation of collection access.

For a later unattended return, create separate temporary box access if needed, report return, then have Facility verify the physical key. Cancellation/expiry never proves physical movement or removal of digital access.

Room lookup shows direct/inherited keys, current holders, digital door permissions and potential access through available lockbox contents. Pending/failed/expired permissions remain visible until verified revoked. Potential access is labelled separately from possession. Broad box access can expose every stored master key: inspect this view when configuring contents.

## Notifications, history and privacy

Changes queue borrower notices in the existing durable outbox, in the same transaction. Notices contain tags/dates/status, with no room map or credentials. The independent worker queues due-key reminders once per building-site day after the configured notification reminder time. Long-term loans have no overdue reminders.

The overview flags inactive users' outstanding keys and digital follow-up. Facility history shows the latest 200 `keys.*` events only; the general IT audit endpoint stays unchanged. Audit stores employee/time/IDs/verification references. Never enter credentials or personal details in notes.

Anonymization is blocked until key loans close and digital revocations are verified. Afterwards, key-related outbox contents are scrubbed using a stable person-ID prefix. New tables reference Person without duplicating borrower names/emails.

## Development, deployment and demo

Core: `Domain/KeyAccess.cs`, `Keys/KeyAccessService*.cs`, `Keys/KeyAccessMaintenance.cs`, `Data/KeyAccessModel.cs`. UI: `Pages/Keys/`. Worker: `KeyAccessMaintenanceJob`, registered in production worker/demo job hosts.

Writes acquire the singleton `KeyAccessRevisions` row in a serializable transaction before reading mutable state. Audit/outbox save in that transaction. This also protects cycle checks and collection/return transitions. Idle maintenance rolls back its gate increment, preserving pending access reviews.

Apply SQL Server migration **KeyAccess** through the normal ATS deployment process before running the new web/worker. It adds tables/indexes without altering existing inventory/loans. The SQL Server upgrade test checks upgrade and rollback; CI checks the model snapshot. Rollback drops module data: take the normal backup first.

Demo schema/seed detection rebuilds outdated demo databases. Fixtures include Building A, Finance/Server rooms, a two-room grand master, numbered copies, a due key, a lost key, a lockbox reservation and confirmed digital door access. On Demo controls, act as **Max Demoson (Facility)** and open the Facility navigation entry.

The initial implementation loads the module graph and complete loan/permission lists for cross-reference calculations; history is bounded to 200 events. Large estates will need indexed search/paging and bounded graph queries.

Checks: `dotnet build --no-restore -warnaserror`, `dotnet test`, and `dotnet ef migrations has-pending-model-changes -p src/AssetTracker.Infrastructure -s src/AssetTracker.Infrastructure --no-build`. SQL Server tests require `ATS_TEST_SQLSERVER` (see `development.md`).
