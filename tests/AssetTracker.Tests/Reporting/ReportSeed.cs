using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;

namespace AssetTracker.Tests.Reporting;

/// <summary>
/// Loan fixtures written straight to the database (lending services belong to another work package).
/// Fictional data only.
/// </summary>
public static class ReportSeed
{
    public static readonly DateTimeOffset Started = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static async Task<Person> HolderAsync(AppDbContext db, string username, string displayName,
        Site? site = null, string? jobTitle = "Analyst", bool active = true)
    {
        var p = new Person
        {
            DirectoryId = Guid.NewGuid().ToString(), Username = username, DisplayName = displayName,
            JobTitle = jobTitle, Email = $"{username}@example.test", SiteId = site?.Id, IsActive = active,
            FirstSeenAt = Started, LastSyncedAt = Started,
        };
        db.People.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    public static async Task<Loan> LoanAsync(AppDbContext db, Person holder, IndividualAsset? asset = null,
        Pool? pool = null, int quantity = 1, int returned = 0, DateOnly? due = null, DateTimeOffset? started = null,
        DateTimeOffset? closedAt = null)
    {
        var start = started ?? Started;
        var loan = new Loan
        {
            AssetId = asset?.Id, PoolId = pool?.Id, HolderId = holder.Id, Quantity = quantity,
            ReturnedQuantity = closedAt is null ? returned : quantity, StartedAt = start, DueDate = due,
            ClosedAt = closedAt, HolderNameSnapshot = holder.DisplayName, HolderEmailSnapshot = holder.Email,
            CreatedBy = "test",
        };
        loan.Events.Add(new LoanEvent
        {
            Loan = loan, Type = LoanEventType.CheckedOut, OccurredAt = start, RecordedAt = start, Actor = "test",
            HolderId = holder.Id, Quantity = quantity, DueDate = due,
        });
        if (returned > 0 && closedAt is null)
            loan.Events.Add(new LoanEvent
            {
                Loan = loan, Type = LoanEventType.PartiallyReturned, OccurredAt = start.AddDays(1),
                RecordedAt = start.AddDays(1), Actor = "test", HolderId = holder.Id, Quantity = returned, DueDate = due,
            });
        if (closedAt is { } closed)
            loan.Events.Add(new LoanEvent
            {
                Loan = loan, Type = LoanEventType.Returned, OccurredAt = closed, RecordedAt = closed, Actor = "test",
                HolderId = holder.Id, Quantity = quantity, DueDate = due,
            });
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        return loan;
    }

    /// <summary>Appends a correction superseding the loan's first event (effective loan columns are left as seeded).</summary>
    public static async Task<LoanEvent> CorrectionAsync(AppDbContext db, Loan loan, string reason, string actor = @"CORP\it.user")
    {
        var original = db.LoanEvents.Where(e => e.LoanId == loan.Id).OrderBy(e => e.Id).First();
        var e = new LoanEvent
        {
            LoanId = loan.Id, Type = LoanEventType.Corrected, OccurredAt = original.OccurredAt,
            RecordedAt = Started.AddDays(3), Actor = actor, HolderId = loan.HolderId, Quantity = loan.Quantity,
            DueDate = loan.DueDate, Reason = reason, SupersedesEventId = original.Id,
        };
        db.LoanEvents.Add(e);
        await db.SaveChangesAsync();
        return e;
    }
}
