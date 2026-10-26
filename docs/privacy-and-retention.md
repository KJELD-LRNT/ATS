# Privacy, retention and deletion

This document records a client policy decision and the technical procedure that supports it. It is **not a legal
assessment**: applicable privacy obligations remain the client's responsibility and may still constrain deployment
(`docs/mvp-plan.md` §1, §5).

## 1. Sponsor decision — accepted client risk

| Field | Value |
| --- | --- |
| Decision | (a) Personal data in loan history is retained **indefinitely, until the client instructs deletion**. (b) Managers who can see an asset (its **current** site is one of their granted sites) see **full holder details for the whole assignment history**, including previous holders from before the asset moved to their site. (c) No separate privacy-review approval gate before go-live. |
| Status | **Accepted client risk.** Not a data-minimization recommendation by the supplier. |
| Decision owner (name, role) | `<to be filled in by the client>` |
| Date accepted | `<YYYY-MM-DD>` |
| Authority to instruct deletion / retention change | `<to be filled in by the client>` |
| Who may view audit details | IT group (current implementation); `<client to confirm>` |
| Review date (optional) | `<YYYY-MM-DD>` |
| Reference (ticket/minutes) | `<reference>` |

Consequences the owner accepts: personal data of leavers remains visible to IT and to managers of the asset's current
site; CSV exports and backups carry the same data and must follow the client's handling rules; deletion is a manual,
client-directed procedure (§4), not automatic.

## 2. Data inventory

| Where | Personal data | Mutable? | Notes |
| --- | --- | --- | --- |
| `People` | AD display name, username, job title, email, SID, site, active flag, directory id (objectGUID) | Yes (sync) | Snapshot of AD; refreshed by every successful sync unless `IsAnonymized`. |
| `Loans` | `HolderId`, `HolderNameSnapshot`, `HolderEmailSnapshot`, free-text checkout/return notes, `CreatedBy` (IT actor) | Yes (effective state) | Snapshot taken at checkout; corrections update it (`CorrectionService`, conditional `ExecuteUpdate`). |
| `LoanEvents` | `HolderId`, `Actor` (IT account), free-text `Note`/`Reason`; `ChangeJson` holds only ids, dates and quantities | **Append-only** | No holder name or email is stored on events. |
| `StockEvents` | `Actor` (IT account), free-text `Reason` | **Append-only** | No holder data. |
| `NotificationOutbox` | `RecipientEmail`, `Subject`, `Body` (holder name, asset tag, optionally phone number), `DedupeKey` (contains the recipient address), `LastError` | Yes | Sent rows are kept (no automatic purge). |
| `AuditEvents` | `Actor`; `DetailsJson` may contain names/emails (e.g. `loan.checkout` holder name, `notification.requeue` recipient, `setting.update` IT mailbox); `Reason` free text | **Append-only** | Retained; see §5. |
| `ManagerSiteGrants` | principal directory id and name | Yes | Removed by anonymization. |
| `SyncRuns.IssuesJson` | directory id and username of quarantined entries | Yes | Operational log; may be purged by DBA under the same instruction. |
| `AppSettings` | IT mailbox address (a role mailbox, normally not personal) | Yes | |
| CSV exports | Whatever the report showed (holder name, username, email, title) | Outside the app | Downloaded files are the client's responsibility; exports are audited (`export.*`). |
| Backups | Everything above | Per backup platform | Anonymization does not reach existing backups; they age out per the client's backup retention. |
| Logs (Event Log / IIS) | Windows account names of users | Per server policy | No mail bodies are logged. |

## 3. What anonymization preserves

Ids, quantities, dates, states and every ledger row stay unchanged, so these invariants keep holding:
pool `on hand = available + outstanding`; one open loan per tagged asset; loan timelines and correction chains;
stock-event history; audit trail completeness. Reports show the pseudonym (`anonymized-<id>`) instead of the person.

## 4. Anonymization procedure (under client instruction)

**Preconditions:** a written client instruction (record its reference); the person is **inactive** in the latest
directory snapshot (left/disabled in AD); the person has **no open loans** (return or correct them first).

**In the application** (application admin, page **Admin → Privacy**):

1. Search the person (name, username or email). Only inactive, not yet anonymized people are listed.
2. Enter the reason (the client instruction reference), tick the confirmation and press *Anonymize*.
3. `PrivacyService.AnonymizePersonAsync` does, in one transaction:
   - `People`: username and display name → `anonymized-<id>`; job title, email, SID, site → null; `IsActive = false`;
     `IsAnonymized = true`. The directory id is kept so the sync recognizes the person and leaves the row alone.
   - `Loans` held by the person: `HolderNameSnapshot` → pseudonym, `HolderEmailSnapshot` → null.
   - `NotificationOutbox` rows about those loans (holder and IT copies) or addressed to the person: subject, body
     and last error → `[removed: privacy request]`; the person's address → null; dedupe key → `anonymized:<row id>`;
     still-`Pending` rows → `Skipped` (never sent).
   - `ManagerSiteGrants` of the person: removed.
   - Audit event `privacy.anonymize` (actor, reason, counts only — no personal data).
4. The action cannot be undone in the app (restore from backup is the only way back).

**Directory sync afterwards:** an anonymized person is never refreshed or reactivated. If AD still contains or
re-enables the account, the sync run reports an `anonymized-present` issue. If the person is re-hired and must borrow
again, a DBA (under instruction) changes that row's `DirectoryId` to a unique tombstone value (e.g.
`anonymized-<id>`); the next sync then creates a fresh person record with no link to the old history.

**Not handled by the app path (documented limitations):**

- Free-text fields written by IT (`Loans.CheckoutNote`/`ReturnNote`, `LoanEvents.Note`/`Reason`,
  `StockEvents.Reason`) may mention the person. Review them with the client; mutable `Loans` notes can be edited by a
  DBA. Event notes are append-only (below).
- `AuditEvents.DetailsJson` and append-only event free text are **retained**: the application deliberately has no
  update/delete path for them (tamper-evidence). If the client instructs that these must also be scrubbed, a DBA does it
  directly in SQL as a documented exception: take a backup; within a transaction, replace the person's name/email in
  `AuditEvents.DetailsJson`/`Reason` (and event `Note`/`Reason` if instructed) with the pseudonym, matching on the
  exact old values recorded before anonymization; insert one `AuditEvents` row (`Action = 'privacy.audit-scrub'`,
  actor = DBA, reason = instruction reference, details = row counts). Record it with the decision in §1.
- Existing backups and previously downloaded CSV exports.
- Race: a correction that sets a closed loan's holder to the person *while* the anonymization runs could rewrite the
  snapshot from stale data. Corrections to an inactive holder are rare; if one is suspected, re-check that person's
  loans after anonymizing.

## 5. Bulk retention (if the client later sets a retention period)

The decision in §1 is indefinite retention, so nothing is purged automatically. If the client sets a period `X`,
these DBA steps (under instruction, after a backup) reduce stored mail content without breaking invariants:

```sql
-- Scrub sent/skipped/failed mail content older than the cut-off (keeps ids, kinds, statuses and timestamps).
DECLARE @Cutoff datetimeoffset = '<YYYY-MM-DDT00:00:00+00:00>';
UPDATE dbo.NotificationOutbox
SET Subject = N'[removed: retention]', Body = N'[removed: retention]', RecipientEmail = NULL,
    LastError = CASE WHEN LastError IS NULL THEN NULL ELSE N'[removed: retention]' END,
    DedupeKey = CONCAT(N'retention:', Id)
WHERE Status IN (2, 3, 4) /* Sent, Failed, Skipped */ AND CreatedAt < @Cutoff;
```

Inactive people whose last loan closed before the cut-off can then be anonymized one by one via the app (§4), so each
has its own audit event. A bulk app path can be added if the client needs it.

## 6. Export and access controls

- Managers and IT export only what they can see; every export is audited (`export.*`, actor, filters).
- Responses (pages and CSV) are sent with `Cache-Control: no-store`, so browsers and proxies do not keep copies.
- The client decides where exports may be stored and for how long; this app cannot control downloaded files.
- Backups contain personal data: restrict access and retention through the backup platform.
