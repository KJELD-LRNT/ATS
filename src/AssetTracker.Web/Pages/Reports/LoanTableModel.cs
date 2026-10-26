using AssetTracker.Core.Reporting;

namespace AssetTracker.Web.Pages.Reports;

/// <summary>Model for the <c>_LoanTable</c> partial shared by the assignments, overdue and history reports.</summary>
public sealed record LoanTableModel(IReadOnlyList<LoanRow> Rows, bool History = false, bool Overdue = false);
