using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Inventory;

/// <summary>What the generator would hand out next: a tag, or the reason it cannot generate one.</summary>
public sealed record TagPreview(string? Tag, string? Problem);

/// <summary>
/// Generates asset tags from the type's <see cref="TagPattern"/>, counting up per type and site
/// (<see cref="TagCounter"/>). A number is skipped when its tag already exists (typed by hand, or left over from an
/// older pattern), so a generated tag is always free. <see cref="AllocateAsync"/> must run inside the transaction
/// that inserts the asset: the counter then moves only when the asset is saved.
/// </summary>
public class TagGenerator(AppDbContext db)
{
    private const int MaxSkips = 1000;

    /// <summary>The tag the next new asset of this type and site would get. Nothing is reserved.</summary>
    public async Task<TagPreview> PreviewAsync(int typeId, int siteId, CancellationToken ct = default)
    {
        var (pattern, siteCode, problem) = await ResolveAsync(typeId, siteId, ct);
        if (pattern is null) return new TagPreview(null, problem);

        var last = await db.TagCounters.AsNoTracking().Where(c => c.TypeId == typeId && c.SiteId == siteId)
            .Select(c => (int?)c.LastNumber).FirstOrDefaultAsync(ct) ?? 0;
        for (var n = last + 1; n <= last + MaxSkips; n++)
        {
            var tag = TagPattern.Render(pattern, siteCode, n);
            if (!await db.Assets.AnyAsync(a => a.Tag == tag, ct)) return new TagPreview(tag, null);
        }
        return new TagPreview(null, "No free number was found for this tag pattern.");
    }

    /// <summary>Reserves and returns the next free tag. Call inside the transaction that saves the asset.</summary>
    public async Task<string> AllocateAsync(int typeId, int siteId, CancellationToken ct = default)
    {
        var (pattern, siteCode, problem) = await ResolveAsync(typeId, siteId, ct);
        if (pattern is null) throw new DomainException("asset.tag_generation", problem!);

        for (var attempt = 0; attempt < MaxSkips; attempt++)
        {
            var last = await db.TagCounters.AsNoTracking().Where(c => c.TypeId == typeId && c.SiteId == siteId)
                .Select(c => (int?)c.LastNumber).FirstOrDefaultAsync(ct);
            var next = (last ?? 0) + 1;

            if (last is null)
            {
                var counter = new TagCounter { TypeId = typeId, SiteId = siteId, LastNumber = next };
                db.TagCounters.Add(counter);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Another request created the counter first: read it again.
                    db.Entry(counter).State = EntityState.Detached;
                    continue;
                }
            }
            else
            {
                // Claim the number only if nobody else did meanwhile (compare-and-set on the last number).
                var rows = await db.TagCounters.Where(c => c.TypeId == typeId && c.SiteId == siteId && c.LastNumber == last)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastNumber, next), ct);
                if (rows == 0) continue;
            }

            var tag = TagPattern.Render(pattern, siteCode, next);
            if (tag.Length > AssetService.TagMaxLength)
                throw new DomainException("asset.tag_length", $"The generated tag {tag} is too long.");
            if (!await db.Assets.AnyAsync(a => a.Tag == tag, ct)) return tag;
        }
        throw new ConflictException("asset.tag_generation", "No free number was found for this tag pattern.");
    }

    private async Task<(string? Pattern, string? SiteCode, string? Problem)> ResolveAsync(int typeId, int siteId,
        CancellationToken ct)
    {
        var type = await db.AssetTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId, ct);
        if (type is null) return (null, null, "Choose a type.");
        if (type.Mode != InventoryMode.Tagged) return (null, null, $"{type.Name} is a quantity pool, not tagged.");
        if (string.IsNullOrEmpty(type.TagPattern))
            return (null, null, $"{type.Name} has no tag pattern: type the asset tag by hand (admins set a pattern under Admin → Asset types).");

        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site is null) return (null, null, "Choose a site.");
        if (TagPattern.UsesSite(type.TagPattern) && string.IsNullOrEmpty(site.TagCode))
            return (null, null, $"Site {site.Key} has no tag code: an admin sets it under Admin → Sites, or type the tag by hand.");
        return (type.TagPattern, site.TagCode, null);
    }
}
