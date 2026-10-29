# Keys & digital access

Facility → **Keys & digital access** (`/Keys`) manages physical keys and records who has access to digital doors and lockboxes. Digital access is a manual register; hardware integration is outside the module's scope.
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

Facility selects a lock, finds a user and chooses **Record access**. This immediately records that the person has Bluetooth-app or PIN access arranged outside ATS. Start is optional (defaults to now); end is optional for ongoing access. An optional reference can identify the relevant facility record. Record the method, never the PIN or password itself.

Register the device name, location, type and methods (Bluetooth app and/or PIN). Manufacturer, model and device reference are optional details. Door locks require a room. Lockboxes may have a location room and hold registered physical copies. Records reference existing ATS people; existing access can also be recorded for inactive people or people without email so offboarding is complete. Anonymized people cannot be selected. Dates, when supplied, are UTC regardless of the host timezone.

| State | Meaning |
| --- | --- |
| Pending | Planned access, used for lockbox reservations until Facility records it as granted. |
| Confirmed | Facility recorded access. Displayed as Has access or Scheduled, or flagged if its dated period ended. Normal Record access uses this state immediately. |
| Revocation pending | Removal needs confirmation, including after expiry/deactivation. |
| Revoked | Facility recorded that access was removed outside ATS. Cannot reopen; create a new access record. |
| Failed | Access needs checking. A removal issue remains flagged until resolved. |

Use **Record that access has been removed** to close an access record directly, or **Flag access for removal** if someone still needs to arrange removal. References are optional. ATS retains who recorded each change and when. Ending a dated period does not itself prove access was removed: an independent worker flags expired/inactive access every minute, and the overview also highlights it when the worker is stopped. Ongoing access is not expired automatically.

ATS has no hardware connection, PIN generation/delivery, remote opening, mobile app or provider opening log. No manufacturer or hardware selection is required to use the register.

## Lockbox collection and room lookup

Reserve an available copy stored in a lockbox, with a UTC collection window and method. This atomically creates a key reservation and planned digital-access record. After access is arranged outside ATS, Facility records it as granted under **Digital locks & lockboxes**. Facility explicitly verifies key collection within that window to start the physical loan; collection flags pickup access for removal.

For a later unattended return, create separate temporary box access if needed, report return, then have Facility verify the physical key. Cancellation/expiry never proves physical movement or removal of digital access.

Room lookup shows direct/inherited keys, current holders, digital door permissions and potential access through available lockbox contents. Pending/failed/expired permissions remain visible until verified revoked. Potential access is labelled separately from possession. Broad box access can expose every stored master key: inspect this view when configuring contents.

## Notifications, history and privacy

Changes queue borrower notices in the existing durable outbox, in the same transaction. Notices contain tags/dates/status, with no room map or credentials. The independent worker queues due-key reminders once per building-site day after the configured notification reminder time. Long-term loans have no overdue reminders.

The overview flags inactive users' outstanding keys and digital follow-up. Facility history shows the latest 200 `keys.*` events only; the general IT audit endpoint stays unchanged. Audit stores employee/time/IDs/verification references. Never enter credentials or personal details in notes.

Anonymization is blocked until key loans close and digital revocations are verified. Afterwards, key-related outbox contents are scrubbed using a stable person-ID prefix. New tables reference Person without duplicating borrower names/emails.

## Development, deployment and demo

Core: `Domain/KeyAccess.cs`, `Keys/KeyAccessService*.cs`, `Keys/KeyAccessMaintenance.cs`, `Data/KeyAccessModel.cs`. UI: `Pages/Keys/`. Worker: `KeyAccessMaintenanceJob`, registered in production worker/demo job hosts.

Writes acquire the singleton `KeyAccessRevisions` row in a serializable transaction before reading mutable state. Audit/outbox save in that transaction. This also protects cycle checks and collection/return transitions. Idle maintenance rolls back its gate increment, preserving pending access reviews.

Apply SQL Server migrations **KeyAccess** and **DigitalAccessRegister** through the normal ATS deployment process before running the new web/worker. They add module tables/indexes and allow ongoing digital access without altering existing inventory/loans. The SQL Server test checks upgrade and rollback; CI checks the snapshot. Downgrading DigitalAccessRegister maps ongoing access to the latest possible expiry to satisfy the earlier schema; rolling back KeyAccess drops module data. Take the normal backup first.

Demo schema/seed detection rebuilds outdated demo databases. Fixtures include Building A, Finance/Server rooms, a two-room grand master, numbered copies, a due key, a lost key, a lockbox reservation and ongoing recorded digital door access. On Demo controls, act as **Max Demoson (Facility)** and open the Facility navigation entry.

The initial implementation loads the module graph and complete loan/permission lists for cross-reference calculations; history is bounded to 200 events. Large estates will need indexed search/paging and bounded graph queries.

Checks: `dotnet build --no-restore -warnaserror`, `dotnet test`, and `dotnet ef migrations has-pending-model-changes -p src/AssetTracker.Infrastructure -s src/AssetTracker.Infrastructure --no-build`. SQL Server tests require `ATS_TEST_SQLSERVER` (see `development.md`).
