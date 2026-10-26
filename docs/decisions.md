# Sponsor decisions

Product decisions taken after the MVP plan (`docs/mvp-plan.md`). Each one is enforced by the tests named below.

| # | Date | Decision | Where enforced |
| --- | --- | --- | --- |
| D1 | 2026-10-26 | **Every committed loan correction sends mail, including due-date-only corrections.** Recipients are the previous holder, the corrected holder and IT, deduplicated. Holders who can't be mailed are recorded as skipped and IT is still notified. | `CorrectionService.CommitAsync`; test `CorrectionServiceTests.Correction_for_the_same_holder_notifies_holder_once_plus_it` |
| D2 | 2026-10-26 | **The audit log is visible to IT only.** App admins and managers cannot read it, and nobody can edit or delete it. | `/Audit` (`Policies.ItOnly`), `AuditQueryService` (`EnsureIt`); tests in `tests/AssetTracker.Tests/Admin/AuditTests.cs` |
| D3 | 2026-10-26 | **A stale or long-stopped worker reports `Degraded`, not `Unhealthy`,** on `/health/ready`. Only a worker that has never written a heartbeat is `Unhealthy`. Monitoring should alert on `worker` being `Degraded` (see `docs/runbook.md`). | `WorkerHealthCheck` (`src/AssetTracker.Web/Health/ReadinessChecks.cs`); test `HealthEndpointTests.Worker_heartbeat_goes_stale_after_missed_intervals` |
