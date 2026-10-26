using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Inventory;

public sealed record CreateAssetInput(
    string Tag, AssetType Type, int SiteId, string Description,
    string? Condition = null, string? SerialNumber = null, string? Imei = null, string? PhoneNumber = null);

/// <summary>Editable fields. Tag, type and site are not editable here (site moves via <see cref="AssetService.TransferSiteAsync"/>).</summary>
public sealed record EditAssetInput(
    string Description, string? Condition = null, string? SerialNumber = null, string? Imei = null, string? PhoneNumber = null);

public sealed record AssetSearch(
    string? Query = null, int? SiteId = null, AssetType? Type = null, AssetState? State = null,
    int Page = 1, int PageSize = Paging.DefaultPageSize);

/// <summary>
/// Individually tagged assets: create, edit, search, retire, availability and site transfer.
/// Reads are scoped by <see cref="AccessScope"/>; mutations are IT-only and audited in the same transaction.
/// State transitions use conditional atomic updates so they cannot race a concurrent checkout.
/// </summary>
public class AssetService(AppDbContext db, AccessScope access, AuditWriter audit, IClock clock)
{
    public const int TagMaxLength = 64;

    public async Task<PagedResult<IndividualAsset>> SearchAsync(AssetSearch s, CancellationToken ct = default)
    {
        var q = (await access.VisibleAssetsAsync(ct)).AsNoTracking().Include(a => a.Site).AsQueryable();

        if (s.SiteId is { } siteId) q = q.Where(a => a.SiteId == siteId);
        if (s.Type is { } type) q = q.Where(a => a.Type == type);
        if (s.State is { } state) q = q.Where(a => a.State == state);

        var term = s.Query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var pattern = Paging.ContainsPattern(term);
            var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
            // Phone numbers are stored as "+digits", so match on the digits typed regardless of formatting.
            var digitPattern = digits.Length >= 3 ? Paging.ContainsPattern(digits) : null;
            q = q.Where(a =>
                EF.Functions.Like(a.Tag, pattern, @"\") ||
                EF.Functions.Like(a.Description, pattern, @"\") ||
                (a.SerialNumber != null && EF.Functions.Like(a.SerialNumber, pattern, @"\")) ||
                (digitPattern != null && a.Imei != null && EF.Functions.Like(a.Imei, digitPattern, @"\")) ||
                (digitPattern != null && a.PhoneNumber != null && EF.Functions.Like(a.PhoneNumber, digitPattern, @"\")));
        }

        return await q.OrderBy(a => a.Tag).ToPageAsync(s.Page, s.PageSize, ct);
    }

    /// <summary>Returns the asset if visible to the current principal; otherwise <see cref="AccessDeniedException"/> (404).</summary>
    public async Task<IndividualAsset> GetAsync(int id, CancellationToken ct = default)
    {
        var asset = await (await access.VisibleAssetsAsync(ct)).AsNoTracking().Include(a => a.Site)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        return asset ?? throw new AccessDeniedException();
    }

    /// <summary>Full assignment history of the asset (visible by its current site), newest first.</summary>
    public async Task<IReadOnlyList<Loan>> GetLoanHistoryAsync(int id, CancellationToken ct = default)
    {
        await GetAsync(id, ct);
        return await (await access.VisibleLoansAsync(ct)).AsNoTracking().Include(l => l.Holder)
            .Where(l => l.AssetId == id).OrderByDescending(l => l.StartedAt).ToListAsync(ct);
    }

    public async Task<IndividualAsset> CreateAsync(CreateAssetInput input, CancellationToken ct = default)
    {
        access.EnsureIt();

        var tag = Normalization.Code(input.Tag);
        if (tag.Length == 0) throw new DomainException("asset.tag_required", "Asset tag is required.");
        if (tag.Length > TagMaxLength)
            throw new DomainException("asset.tag_length", $"Asset tag may be at most {TagMaxLength} characters.");
        if (!Enum.IsDefined(input.Type)) throw new DomainException("asset.type_invalid", "Unknown asset type.");
        await EnsureActiveSiteAsync(input.SiteId, ct);

        var asset = new IndividualAsset
        {
            Tag = tag,
            Type = input.Type,
            SiteId = input.SiteId,
            State = AssetState.Available,
            CreatedAt = clock.UtcNow,
        };
        ApplyEditableFields(asset, new EditAssetInput(input.Description, input.Condition, input.SerialNumber,
            input.Imei, input.PhoneNumber));

        if (await db.Assets.AnyAsync(a => a.Tag == tag, ct))
            throw new ConflictException("asset.tag_taken", $"Asset tag {tag} is already in use.");
        await EnsureIdentifiersFreeAsync(asset, ct);

        // Two saves in one transaction so the audit row carries the generated id.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Assets.Add(asset);
        await SaveAsync(ct);
        audit.Record("asset.create", nameof(IndividualAsset), asset.Id, asset.SiteId, details: Snapshot(asset));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return asset;
    }

    public async Task<IndividualAsset> EditAsync(int id, EditAssetInput input, CancellationToken ct = default)
    {
        access.EnsureIt();
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new AccessDeniedException();
        if (asset.State == AssetState.Retired)
            throw new DomainException("asset.retired", "A retired asset cannot be edited.");

        var before = Snapshot(asset);
        ApplyEditableFields(asset, input);
        await EnsureIdentifiersFreeAsync(asset, ct);

        audit.Record("asset.edit", nameof(IndividualAsset), asset.Id, asset.SiteId,
            details: new { before, after = Snapshot(asset) });
        await SaveAsync(ct);
        return asset;
    }

    /// <summary>Retires an asset. Requires no open loan; the tag stays reserved and history is kept.</summary>
    public Task RetireAsync(int id, string? reason = null, CancellationToken ct = default) =>
        TransitionAsync(id, "asset.retire", reason,
            from: [AssetState.Available, AssetState.Unavailable], to: AssetState.Retired,
            blockedMessage: "Only an asset with no open loan can be retired.", ct);

    /// <summary>Takes an available asset out of circulation (repair, pending retirement...).</summary>
    public Task MarkUnavailableAsync(int id, string? reason = null, CancellationToken ct = default) =>
        TransitionAsync(id, "asset.mark_unavailable", reason,
            from: [AssetState.Available], to: AssetState.Unavailable,
            blockedMessage: "Only an available asset with no open loan can be marked unavailable.", ct);

    /// <summary>Explicit IT action returning an unavailable asset to circulation.</summary>
    public Task MarkAvailableAsync(int id, string? reason = null, CancellationToken ct = default) =>
        TransitionAsync(id, "asset.mark_available", reason,
            from: [AssetState.Unavailable], to: AssetState.Available,
            blockedMessage: "Only an unavailable asset can be marked available.", ct);

    /// <summary>Moves the asset to another site. Blocked while it has an open loan.</summary>
    public async Task TransferSiteAsync(int id, int toSiteId, string? reason = null, CancellationToken ct = default)
    {
        access.EnsureIt();
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new AccessDeniedException();
        if (asset.SiteId == toSiteId)
            throw new DomainException("asset.same_site", "The asset is already at that site.");
        await EnsureActiveSiteAsync(toSiteId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Assets
            .Where(a => a.Id == id && a.SiteId == asset.SiteId && a.State != AssetState.CheckedOut &&
                        !db.Loans.Any(l => l.AssetId == a.Id && l.ClosedAt == null))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.SiteId, toSiteId), ct);
        if (rows == 0)
            throw new ConflictException("asset.transfer_blocked",
                "The asset cannot be moved while it has an open loan (or it was changed concurrently).");

        audit.Record("asset.transfer_site", nameof(IndividualAsset), id, toSiteId, Normalization.OptionalText(reason),
            new { fromSiteId = asset.SiteId, toSiteId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task TransitionAsync(int id, string action, string? reason, AssetState[] from, AssetState to,
        string blockedMessage, CancellationToken ct)
    {
        access.EnsureIt();
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new AccessDeniedException();
        if (!from.Contains(asset.State))
            throw new DomainException("asset.state", $"{blockedMessage} Current state: {asset.State}.");

        var now = clock.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Conditional on the observed state and on no open loan, so a concurrent checkout cannot be overwritten.
        var rows = await db.Assets
            .Where(a => a.Id == id && a.State == asset.State &&
                        !db.Loans.Any(l => l.AssetId == a.Id && l.ClosedAt == null))
            .ExecuteUpdateAsync(u =>
            {
                u.SetProperty(a => a.State, to);
                if (to == AssetState.Retired) u.SetProperty(a => a.RetiredAt, now);
            }, ct);
        if (rows == 0) throw new ConflictException("asset.state_conflict", blockedMessage);

        audit.Record(action, nameof(IndividualAsset), id, asset.SiteId, Normalization.OptionalText(reason),
            new { from = asset.State.ToString(), to = to.ToString() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void ApplyEditableFields(IndividualAsset asset, EditAssetInput input)
    {
        var description = input.Description?.Trim() ?? "";
        if (description.Length == 0)
            throw new DomainException("asset.description_required", "Description/model is required.");
        if (description.Length > 500)
            throw new DomainException("asset.description_length", "Description may be at most 500 characters.");

        var phone = Normalization.OptionalPhoneNumber(input.PhoneNumber);
        var imei = Normalization.OptionalImei(input.Imei);
        if (phone is not null && asset.Type is not (AssetType.Phone or AssetType.PhoneNumber))
            throw new DomainException("asset.phone_not_applicable", "A phone number only applies to phones and phone numbers.");
        if (phone is null && asset.Type == AssetType.PhoneNumber)
            throw new DomainException("asset.phone_required", "A phone number asset requires the phone number.");
        if (imei is not null && asset.Type != AssetType.Phone)
            throw new DomainException("asset.imei_not_applicable", "An IMEI only applies to phones.");

        asset.Description = description;
        asset.Condition = Limit(Normalization.OptionalText(input.Condition), 200, "Condition");
        asset.SerialNumber = Limit(Normalization.OptionalText(input.SerialNumber), 128, "Serial number");
        asset.PhoneNumber = phone;
        asset.Imei = imei;
    }

    private static string? Limit(string? value, int max, string field) =>
        value is not null && value.Length > max
            ? throw new DomainException("asset.field_length", $"{field} may be at most {max} characters.")
            : value;

    /// <summary>Friendly pre-check; the filtered unique indexes remain the real guarantee (see <see cref="SaveAsync"/>).</summary>
    private async Task EnsureIdentifiersFreeAsync(IndividualAsset asset, CancellationToken ct)
    {
        if (asset.PhoneNumber is { } phone &&
            await db.Assets.AnyAsync(a => a.Id != asset.Id && a.PhoneNumber == phone && a.RetiredAt == null, ct))
            throw new ConflictException("asset.phone_taken", $"Phone number {phone} is already assigned to another active asset.");
        if (asset.Imei is { } imei &&
            await db.Assets.AnyAsync(a => a.Id != asset.Id && a.Imei == imei && a.RetiredAt == null, ct))
            throw new ConflictException("asset.imei_taken", $"IMEI {imei} is already assigned to another active asset.");
    }

    private async Task EnsureActiveSiteAsync(int siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId && s.IsActive, ct))
            throw new DomainException("asset.site_invalid", "Choose an active site.");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race on a unique index (tag, active phone number or active IMEI) after the pre-checks passed.
            throw new ConflictException("asset.duplicate",
                "The asset tag, phone number or IMEI is already in use by another asset.");
        }
    }

    private static object Snapshot(IndividualAsset a) => new
    {
        a.Tag, Type = a.Type.ToString(), a.SiteId, a.Description, a.Condition, a.SerialNumber, a.Imei, a.PhoneNumber,
    };
}
