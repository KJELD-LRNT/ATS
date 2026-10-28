# ATS: Client Asset Tracker

A browser-based internal asset tracker for tagged devices and site quantity pools. It lends assets to people synced from on-prem Active Directory, with audited checkout, return and correction, mail notifications, and site-scoped reporting.

- **Stack:** ASP.NET Core (.NET 10, Razor Pages) on IIS with Windows Authentication, SQL Server (Express), and a Windows Worker Service.
- **Plan:** [`docs/mvp-plan.md`](docs/mvp-plan.md)
- **Development guide:** [`docs/development.md`](docs/development.md)
- **Try it locally (demo mode, fictional data, no server needed):** [`docs/testing-guide.md`](docs/testing-guide.md): `dotnet run --project src/AssetTracker.Web --launch-profile Demo`
- **Sponsor decisions:** [`docs/decisions.md`](docs/decisions.md)
- **Operations runbook** (install, monitoring, release/rollback, backup/restore, troubleshooting): [`docs/runbook.md`](docs/runbook.md)
- **Privacy, retention and deletion** (sponsor decision, data inventory, anonymization): [`docs/privacy-and-retention.md`](docs/privacy-and-retention.md)
- **Deployment artifacts and configuration reference:** [`deploy/README.md`](deploy/README.md); build with `build/publish.ps1`
- **Status:** tracked in the [MVP epic issue](https://github.com/KJELD-LRNT/ATS/issues/1)

> Not authorized for deployment. Production values (AD, SMTP, timezone) come from Stage 0 client preflight (issue #2).
