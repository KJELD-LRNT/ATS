using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;

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

    /// <summary>Creates the Person record for a test user (so manager grants can resolve by SID) and grants sites.</summary>
    public static async Task<Person> ManagerWithGrantsAsync(AppDbContext db, TestUser user, params Site[] sites)
    {
        var p = await PersonAsync(db, user.Name.Split('\\').Last(), sid: user.Sid);
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
        AssetType type = AssetType.Laptop, AssetState state = AssetState.Available)
    {
        var a = new IndividualAsset
        {
            Tag = tag, Type = type, SiteId = site.Id, Description = $"{type} {tag}", State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Assets.Add(a);
        await db.SaveChangesAsync();
        return a;
    }

    public static async Task<Pool> PoolAsync(AppDbContext db, string sku, Site site, int available,
        AssetType type = AssetType.Headset)
    {
        var p = new Pool
        {
            Sku = sku, Type = type, SiteId = site.Id, Description = sku, Available = available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Pools.Add(p);
        await db.SaveChangesAsync();
        return p;
    }
}
