# ATS: Client Asset Tracker

A browser-based internal asset tracker for tagged devices and site quantity pools. It lends assets to people synced from on-prem Active Directory, with audited checkout, return and correction, mail notifications, and site-scoped reporting.

- **Stack:** ASP.NET Core (.NET 10, Razor Pages) on IIS with Windows Authentication, SQL Server (Express), and a Windows Worker Service.
- **Plan:** [`docs/mvp-plan.md`](docs/mvp-plan.md)
- **Development guide:** [`docs/development.md`](docs/development.md)
- **Status:** tracked in the [MVP epic issue](https://github.com/KJELD-LRNT/ATS/issues/1)

> Not authorized for deployment. Production values (AD, SMTP, timezone, group SIDs) come from Stage 0 client preflight (issue #2).
