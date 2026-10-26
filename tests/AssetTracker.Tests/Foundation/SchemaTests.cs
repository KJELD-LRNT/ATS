using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Foundation;

public class SchemaTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public SchemaTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Database_enforces_one_open_loan_per_tagged_asset()
    {
        await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, "S1A");
            var person = await Seed.PersonAsync(db, "schema.p1", site);
            var asset = await Seed.AssetAsync(db, "SCH-0001", site);

            Loan NewLoan() => new()
            {
                AssetId = asset.Id, HolderId = person.Id, Quantity = 1, StartedAt = DateTimeOffset.UtcNow,
                HolderNameSnapshot = person.DisplayName, CreatedBy = "test",
            };

            db.Loans.Add(NewLoan());
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            db.Loans.Add(NewLoan());
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Closed_loans_do_not_block_a_new_open_loan()
    {
        await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, "S1B");
            var person = await Seed.PersonAsync(db, "schema.p2", site);
            var asset = await Seed.AssetAsync(db, "SCH-0002", site);

            db.Loans.Add(new Loan
            {
                AssetId = asset.Id, HolderId = person.Id, Quantity = 1, ReturnedQuantity = 1,
                StartedAt = DateTimeOffset.UtcNow, ClosedAt = DateTimeOffset.UtcNow,
                HolderNameSnapshot = "x", CreatedBy = "test",
            });
            db.Loans.Add(new Loan
            {
                AssetId = asset.Id, HolderId = person.Id, Quantity = 1, StartedAt = DateTimeOffset.UtcNow,
                HolderNameSnapshot = "x", CreatedBy = "test",
            });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Database_rejects_negative_pool_availability()
    {
        await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, "S1C");
            var pool = await Seed.PoolAsync(db, "HS-100", site, 2);
            var rows = await db.Pools.Where(p => p.Id == pool.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Available, p => p.Available - 3))
                .ContinueWith(t => t.IsFaulted ? -1 : t.Result);
            Assert.Equal(-1, rows);
        });
    }

    [Fact]
    public async Task Active_phone_numbers_are_unique_but_retired_ones_may_repeat()
    {
        await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, "S1D");
            db.Assets.Add(new IndividualAsset { Tag = "PN-1", Type = AssetType.PhoneNumber, SiteId = site.Id, PhoneNumber = "+31201234567", State = AssetState.Retired, RetiredAt = DateTimeOffset.UtcNow });
            db.Assets.Add(new IndividualAsset { Tag = "PN-2", Type = AssetType.PhoneNumber, SiteId = site.Id, PhoneNumber = "+31201234567" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            db.Assets.Add(new IndividualAsset { Tag = "PN-3", Type = AssetType.PhoneNumber, SiteId = site.Id, PhoneNumber = "+31201234567" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }
}
