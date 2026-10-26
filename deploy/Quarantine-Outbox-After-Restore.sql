/*
  Quarantine-Outbox-After-Restore.sql

  Purpose: after restoring the Asset Tracker database, prevent the worker from (re)sending notifications that were
  already sent before the incident but were still Pending in the backup, or that are simply too old to be useful.

  Procedure (docs/runbook.md, "Restore drill"):
    1. The worker service is STOPPED (Stop-Service AssetTrackerWorker) before the restore and stays stopped.
    2. Restore the database. Run the INSPECT section below and decide the cut-off with the client alert owner.
       Typical cut-off: the restore point (backup time). Rows created before it may already have been delivered.
    3. Set @CutoffUtc and @Actor, run the whole script (it rolls back unless @Commit = 1).
    4. Start the worker. Remaining Pending rows (created after the cut-off) are sent normally.

  Effect: matching Pending rows become Skipped with a SkipReason; nothing is deleted. Overdue reminders for today
  are re-queued by the worker's daily job only if their dedupe key is unused, so quarantined reminders are not
  recreated for the same day. One audit event is written (the app's audit table is append-only; insert only).

  Run as the migration/DBA account. Times are UTC (the application stores DateTimeOffset in UTC).
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CutoffUtc datetimeoffset = '<RESTORE-POINT-UTC e.g. 2026-01-31T22:00:00+00:00>';
DECLARE @Actor     nvarchar(256)  = N'<DBA-ACCOUNT e.g. DOMAIN\dba.user>';
DECLARE @Reason    nvarchar(2000) = N'Database restore: quarantined mail queued before the restore point.';
DECLARE @Commit    bit            = 0;   -- set to 1 to apply

/* ---- INSPECT (read-only) ---- */
SELECT Status, COUNT(*) AS RowTotal, MIN(CreatedAt) AS Oldest, MAX(CreatedAt) AS Newest
FROM dbo.NotificationOutbox
GROUP BY Status;

SELECT Kind, COUNT(*) AS PendingBeforeCutoff
FROM dbo.NotificationOutbox
WHERE Status = 1 /* Pending */ AND CreatedAt < @CutoffUtc
GROUP BY Kind;

/* ---- QUARANTINE ---- */
BEGIN TRANSACTION;

DECLARE @Count int;

UPDATE dbo.NotificationOutbox
SET Status        = 4 /* Skipped */,
    SkipReason    = LEFT(N'Quarantined after database restore (created before ' + CONVERT(nvarchar(40), @CutoffUtc, 127) + N').', 500),
    NextAttemptAt = SYSUTCDATETIME()
WHERE Status = 1 /* Pending */ AND CreatedAt < @CutoffUtc;

SET @Count = @@ROWCOUNT;

INSERT INTO dbo.AuditEvents (OccurredAt, Actor, Action, EntityType, EntityId, SiteId, Reason, DetailsJson, CorrelationId)
VALUES (SYSDATETIMEOFFSET(), @Actor, N'notification.restore-quarantine', N'NotificationOutbox', NULL, NULL, @Reason,
        N'{"Count":' + CONVERT(nvarchar(20), @Count) + N',"CutoffUtc":"' + CONVERT(nvarchar(40), @CutoffUtc, 127) + N'"}',
        REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N''));

SELECT @Count AS RowsQuarantined;

IF @Commit = 1
    COMMIT TRANSACTION;
ELSE
BEGIN
    ROLLBACK TRANSACTION;
    PRINT 'Dry run: rolled back. Set @Commit = 1 to apply.';
END
