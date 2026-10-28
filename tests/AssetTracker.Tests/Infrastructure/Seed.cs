using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>Fixture builders with fictional data only (no real personal data).</summary>
public static class Seed
{
    /// <param name="timeZoneId">Site timezone; null = the default timezone (UTC in tests, see <c>AppFactory</c>).</param>
    public static async Task<Site> SiteAsync(AppDbContext db, string key, string? directoryValue = null,
        string? timeZoneId = null)
    {
        var s = new Site { Key = key, Name = $"Site {key}", DirectoryValue = directoryValue ?? key, TimeZoneId = timeZoneId };
        db.Sites.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    public static async Task<Person> PersonAsync(AppDbContext db, string username, Site? site = null,
        bool active = true, string? email = "default", string? sid = null)
    {
        var p = new Person
        {
            DirectoryId = Guid.NewGuid().ToString(),
            Sid = sid,
            Username = username,
            DisplayName = username,
            Email = email == "default" ? $"{username}@example.test" : email,
            SiteId = site?.Id,
            IsActive = active,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSyncedAt = DateTimeOffset.UtcNow,
        };
        db.People.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>
    /// The Person record of a test user (so manager grants resolve by SID; the one <c>AppFactory</c> made when the user
    /// already signed in, else a new one) with grants for the sites. The Manager role comes from the user's
    /// <see cref="TestUser.Roles"/> when it signs in.
    /// </summary>
    public static async Task<Person> ManagerWithGrantsAsync(AppDbContext db, TestUser user, params Site[] sites)
    {
        var p = await db.People.SingleOrDefaultAsync(x => x.Sid == user.Sid)
                ?? await PersonAsync(db, user.Username, sid: user.Sid);
        foreach (var s in sites)
            db.ManagerSiteGrants.Add(new ManagerSiteGrant
            {
                PrincipalDirectoryId = p.DirectoryId, PrincipalName = user.Name, SiteId = s.Id,
                GrantedAt = DateTimeOffset.UtcNow, GrantedBy = "test",
            });
        await db.SaveChangesAsync();
        return p;
    }

    public static async Task<IndividualAsset> AssetAsync(AppDbContext db, string tag, Site site,
        int type = BuiltIn.Laptop, AssetState state = AssetState.Available)
    {
        var a = new IndividualAsset
        {
            Tag = tag, TypeId = type, SiteId = site.Id, Description = $"Asset {tag}", State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Assets.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    /// <summary>
    /// Stores a custom field value directly (for arranging tests), with the unique key the service would set when the
    /// field is unique and the asset is not retired.
    /// </summary>
    public static async Task<AssetFieldValue> ValueAsync(AppDbContext db, IndividualAsset asset, int fieldId, string value,
        int position = 0)
    {
        var field = await db.FieldDefinitions.AsNoTracking().SingleAsync(f => f.Id == fieldId);
        var v = new AssetFieldValue
        {
            AssetId = asset.Id, FieldId = fieldId, Position = position, Value = value,
            UniqueKey = asset.RetiredAt is null ? AssetTracker.Core.Inventory.FieldValueRules.UniqueKey(field, value) : null,
        };
        db.FieldValues.Add(v);
        await db.SaveChangesAsync();
        return v;
    }

    /// <summary>Custom field values for <c>CreateAssetInput</c>/<c>EditAssetInput</c>: field id → values.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string?>> Fields(params (int FieldId, string?[] Values)[] fields) =>
        fields.ToDictionary(f => f.FieldId, f => (IReadOnlyList<string?>)f.Values);

    public static async Task<Pool> PoolAsync(AppDbContext db, string sku, Site site, int available,
        int type = BuiltIn.Headset)
    {
        var p = new Pool
        {
            Sku = sku, TypeId = type, SiteId = site.Id, Description = sku, Available = available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Pools.Add(p);
        await db.SaveChangesAsync();
        return p;
    }
}
