using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Notifications;

/// <summary>
/// Loan fixtures written straight to the database (the checkout service belongs to another work package).
/// Fictional data only.
/// </summary>
public static class NotificationSeed
{
    public static readonly DateTimeOffset Started = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static async Task SetItMailboxAsync(AppDbContext db, string? value)
    {
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == AppSetting.ItMailbox);
        if (value is null)
        {
            if (existing is not null) db.Settings.Remove(existing);
        }
        else if (existing is null)
        {
            db.Settings.Add(new AppSetting { Key = AppSetting.ItMailbox, Value = value, UpdatedAt = Started, UpdatedBy = "test" });
        }
        else
        {
            existing.Value = value;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Unsaved tagged loan + CheckedOut event, as a checkout service would stage them.</summary>
    public static (Loan Loan, LoanEvent Event) NewTaggedLoan(IndividualAsset asset, Person holder, DateOnly? due)
    {
        var loan = new Loan
        {
            AssetId = asset.Id, HolderId = holder.Id, Quantity = 1, StartedAt = Started, DueDate = due,
            HolderNameSnapshot = holder.DisplayName, HolderEmailSnapshot = holder.Email, CreatedBy = "test",
        };
        var evt = new LoanEvent
        {
            Loan = loan, Type = LoanEventType.CheckedOut, OccurredAt = Started, RecordedAt = Started, Actor = "test",
            HolderId = holder.Id, Quantity = 1, DueDate = due,
        };
        loan.Events.Add(evt);
        return (loan, evt);
    }

    public static async Task<Loan> TaggedLoanAsync(AppDbContext db, Site site, Person holder, string tag, DateOnly? due,
        int type = BuiltIn.Laptop, string? phoneNumber = null)
    {
        var asset = new IndividualAsset
        {
            Tag = tag, TypeId = type, SiteId = site.Id, Description = $"{tag} description", State = AssetState.CheckedOut,
            CreatedAt = Started,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        if (phoneNumber is not null)
            await Infrastructure.Seed.ValueAsync(db, asset, type == BuiltIn.PhoneNumber ? BuiltIn.LinePhoneNumberField : BuiltIn.PhonePhoneNumberField, phoneNumber);
        var (loan, _) = NewTaggedLoan(asset, holder, due);
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        return loan;
    }

    public static async Task<Loan> PoolLoanAsync(AppDbContext db, Site site, Person holder, string sku, int quantity,
        int returned, DateOnly? due)
    {
        var pool = new Pool
        {
            Sku = sku, TypeId = BuiltIn.Headset, SiteId = site.Id, Description = $"Headset {sku}",
            Available = 10, Outstanding = quantity - returned, CreatedAt = Started,
        };
        db.Pools.Add(pool);
        await db.SaveChangesAsync();
        var loan = new Loan
        {
            PoolId = pool.Id, HolderId = holder.Id, Quantity = quantity, ReturnedQuantity = returned,
            StartedAt = Started, DueDate = due, HolderNameSnapshot = holder.DisplayName,
            HolderEmailSnapshot = holder.Email, CreatedBy = "test",
        };
        loan.Events.Add(new LoanEvent
        {
            Loan = loan, Type = LoanEventType.CheckedOut, OccurredAt = Started, RecordedAt = Started, Actor = "test",
            HolderId = holder.Id, Quantity = quantity, DueDate = due,
        });
        if (returned > 0)
            loan.Events.Add(new LoanEvent
            {
                Loan = loan, Type = LoanEventType.PartiallyReturned, OccurredAt = Started, RecordedAt = Started,
                Actor = "test", HolderId = holder.Id, Quantity = returned, DueDate = due,
            });
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        return loan;
    }

    /// <summary>A Pending outbox row due at <paramref name="due"/>, for sender tests.</summary>
    public static async Task<NotificationOutbox> OutboxRowAsync(AppDbContext db, string recipient, DateTimeOffset due)
    {
        var row = new NotificationOutbox
        {
            Kind = NotificationKind.Checkout, Status = NotificationStatus.Pending,
            DedupeKey = $"test:{Guid.NewGuid():N}:{recipient}", RecipientEmail = recipient,
            Subject = $"Subject for {recipient}", Body = "Body", CreatedAt = due, NextAttemptAt = due,
        };
        db.Outbox.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
}
