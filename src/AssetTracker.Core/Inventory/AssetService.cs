using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Inventory;

/// <summary>
/// A new tagged asset. A blank <see cref="Tag"/> asks the tag generator for the next tag (the type needs a pattern).
/// <see cref="Fields"/> maps custom field ids to their values (several for multi-value fields such as two IMEIs).
/// Standard fields the type does not use (<see cref="AssetType.UsesModel"/> ...) are ignored.
/// </summary>
public sealed record CreateAssetInput(
    string? Tag, int TypeId, int SiteId, string? Description = null, string? Condition = null, string? SerialNumber = null,
    int? VendorId = null, int? ModelId = null, IReadOnlyDictionary<int, IReadOnlyList<string?>>? Fields = null);

/// <summary>
/// Editable fields. Tag, type and site are not editable here (site moves via <see cref="AssetService.TransferSiteAsync"/>).
/// A custom field missing from <see cref="Fields"/> keeps its values; one that is present replaces them.
/// </summary>
public sealed record EditAssetInput(
    string? Description = null, string? Condition = null, string? SerialNumber = null,
    int? VendorId = null, int? ModelId = null, IReadOnlyDictionary<int, IReadOnlyList<string?>>? Fields = null);

public sealed record AssetSearch(
    string? Query = null, int? SiteId = null, int? TypeId = null, AssetState? State = null,
    int Page = 1, int PageSize = Paging.DefaultPageSize);

/// <summary>
/// Individually tagged assets: create (with generated tags and custom fields), edit, search, retire, availability and site transfer.
/// Reads are scoped by <see cref="AccessScope"/>; mutations are IT-only and audited in the same transaction.
/// State transitions use conditional atomic updates so they cannot race a concurrent checkout.
/// </summary>
public class AssetService(AppDbContext db, AccessScope access, AuditWriter audit, IClock clock, TagGenerator tags)
{
    public const int TagMaxLength = 64;

    /// <summary>Assets with what lists need: type, site, vendor, model and the values of the fields the principal may see.</summary>
    private IQueryable<IndividualAsset> WithDetails(IQueryable<IndividualAsset> q)
    {
        var sensitive = access.CanSeeSensitiveFields;
        return q.AsNoTracking().Include(a => a.Site).Include(a => a.Type!).ThenInclude(t => t.Fields)
            .Include(a => a.Vendor).Include(a => a.Model).ThenInclude(m => m!.Vendor)
            .Include(a => a.Values.Where(v => sensitive || !v.Field!.IsSensitive)).ThenInclude(v => v.Field)
            .AsSplitQuery();
    }

    public async Task<PagedResult<IndividualAsset>> SearchAsync(AssetSearch s, CancellationToken ct = default)
    {
        var q = await access.VisibleAssetsAsync(ct);
        var sensitive = access.CanSeeSensitiveFields;

        if (s.SiteId is { } siteId) q = q.Where(a => a.SiteId == siteId);
        if (s.TypeId is { } typeId) q = q.Where(a => a.TypeId == typeId);
        if (s.State is { } state) q = q.Where(a => a.State == state);

        var term = s.Query?.Trim();
        if (!string.IsNullOrEmpty(term)) q = MatchingTerm(q, term, sensitive);

        return await WithDetails(q).OrderBy(a => a.Tag).ToPageAsync(s.Page, s.PageSize, ct);
    }

    /// <summary>
    /// Assets matching a typed search term: tag, description, serial number, vendor and model names and the value of any
    /// custom field (phone numbers and IMEIs also by the digits typed, whatever the formatting). Sensitive field values
    /// only count when <paramref name="includeSensitive"/> is set, so searching cannot reveal them. Shared by the asset
    /// list and the check-out item finder.
    /// </summary>
    internal static IQueryable<IndividualAsset> MatchingTerm(IQueryable<IndividualAsset> q, string term, bool includeSensitive)
    {
        var pattern = Paging.ContainsPattern(term);
        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
        // Phone numbers and IMEIs are stored as digits, so a term that looks like a number ("+31 6 1234 5678", "35-999...")
        // also matches on the digits typed, whatever the formatting. A tag such as "AMS-LT-001" does not: its "001" must not
        // find every phone number containing it.
        var looksLikeNumber = term.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '(' or ')' or '.' or '/');
        var digitPattern = looksLikeNumber && digits.Length >= 3 ? Paging.ContainsPattern(digits) : null;
        return q.Where(a =>
            EF.Functions.Like(a.Tag, pattern, @"\") ||
            EF.Functions.Like(a.Description, pattern, @"\") ||
            (a.SerialNumber != null && EF.Functions.Like(a.SerialNumber, pattern, @"\")) ||
            (a.Model != null && (EF.Functions.Like(a.Model.Name, pattern, @"\") || EF.Functions.Like(a.Model.Vendor!.Name, pattern, @"\"))) ||
            (a.Vendor != null && EF.Functions.Like(a.Vendor.Name, pattern, @"\")) ||
            a.Values.Any(v => (includeSensitive || !v.Field!.IsSensitive) &&
                (EF.Functions.Like(v.Value, pattern, @"\") ||
                 (digitPattern != null && (v.Field!.Kind == FieldKind.Imei || v.Field.Kind == FieldKind.PhoneNumber) &&
                  EF.Functions.Like(v.Value, digitPattern, @"\")))));
    }

    /// <summary>Returns the asset if visible to the current principal; otherwise <see cref="AccessDeniedException"/> (404).</summary>
    public async Task<IndividualAsset> GetAsync(int id, CancellationToken ct = default)
    {
        var asset = await WithDetails(await access.VisibleAssetsAsync(ct)).FirstOrDefaultAsync(a => a.Id == id, ct);
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

        var type = await LoadTypeAsync(input.TypeId, ct);
        if (type.Mode != InventoryMode.Tagged)
            throw new DomainException("asset.type_pooled",
                $"{type.Name} is a quantity pool, not a tagged type. Create a pool for it instead.");
        if (!type.IsActive) throw new DomainException("asset.type_inactive", $"{type.Name} is inactive.");
        await EnsureActiveSiteAsync(input.SiteId, ct);

        var tag = Normalization.Code(input.Tag);
        var generate = tag.Length == 0;
        if (generate && string.IsNullOrEmpty(type.TagPattern))
            throw new DomainException("asset.tag_required", "Asset tag is required.");
        if (tag.Length > TagMaxLength)
            throw new DomainException("asset.tag_length", $"Asset tag may be at most {TagMaxLength} characters.");
        if (!generate && await db.Assets.AnyAsync(a => a.Tag == tag, ct))
            throw new ConflictException("asset.tag_taken", $"Asset tag {tag} is already in use.");

        var asset = new IndividualAsset { Tag = tag, TypeId = type.Id, SiteId = input.SiteId, State = AssetState.Available, CreatedAt = clock.UtcNow };
        await ApplyEditableFieldsAsync(asset, type, input.Description, input.Condition, input.SerialNumber,
            input.VendorId, input.ModelId, ct);
        var values = BuildValues(type, input.Fields, []);
        await EnsureValuesFreeAsync(values, asset.Id, ct);

        // Two saves in one transaction so the audit row carries the generated id (and a generated tag is only
        // counted when the asset is saved).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (generate) asset.Tag = await tags.AllocateAsync(type.Id, input.SiteId, ct);
        foreach (var v in values) asset.Values.Add(new AssetFieldValue { FieldId = v.Field.Id, Position = v.Position, Value = v.Value, UniqueKey = v.UniqueKey });
        db.Assets.Add(asset);
        await SaveAsync(ct);
        audit.Record("asset.create", nameof(IndividualAsset), asset.Id, asset.SiteId, details: await SnapshotAsync(asset, type, values, ct));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return asset;
    }

    public async Task<IndividualAsset> EditAsync(int id, EditAssetInput input, CancellationToken ct = default)
    {
        access.EnsureIt();
        var asset = await db.Assets.Include(a => a.Values).FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new AccessDeniedException();
        if (asset.State == AssetState.Retired)
            throw new DomainException("asset.retired", "A retired asset cannot be edited.");
        var type = await LoadTypeAsync(asset.TypeId, ct);

        var existing = asset.Values.Select(v => (v.FieldId, v.Position, v.Value)).ToList();
        var before = await SnapshotAsync(asset, type, ExistingAsValues(type, asset.Values), ct);
        await ApplyEditableFieldsAsync(asset, type, input.Description, input.Condition, input.SerialNumber,
            input.VendorId, input.ModelId, ct);
        var values = BuildValues(type, input.Fields, existing);
        await EnsureValuesFreeAsync(values, asset.Id, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Replace the values of every field whose values changed. Old rows go first (saved separately) so a value that
        // moves between positions cannot collide with itself in the unique index.
        var changedFields = type.Fields.Where(f => f.IsActive && input.Fields is not null && input.Fields.ContainsKey(f.Id))
            .Where(f => !asset.Values.Where(v => v.FieldId == f.Id).OrderBy(v => v.Position).Select(v => v.Value)
                .SequenceEqual(values.Where(v => v.Field.Id == f.Id).OrderBy(v => v.Position).Select(v => v.Value)))
            .Select(f => f.Id).ToHashSet();
        if (changedFields.Count > 0)
        {
            db.FieldValues.RemoveRange(asset.Values.Where(v => changedFields.Contains(v.FieldId)).ToList());
            await SaveAsync(ct);
            foreach (var v in values.Where(v => changedFields.Contains(v.Field.Id)))
                asset.Values.Add(new AssetFieldValue { AssetId = asset.Id, FieldId = v.Field.Id, Position = v.Position, Value = v.Value, UniqueKey = v.UniqueKey });
        }

        audit.Record("asset.edit", nameof(IndividualAsset), asset.Id, asset.SiteId,
            details: new { before, after = await SnapshotAsync(asset, type, values, ct) });
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
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
        // A retired asset no longer holds its unique values (phone number, IMEI ...): they can be reused.
        if (to == AssetState.Retired)
            await db.FieldValues.Where(v => v.AssetId == id && v.UniqueKey != null)
                .ExecuteUpdateAsync(u => u.SetProperty(v => v.UniqueKey, (string?)null), ct);

        audit.Record(action, nameof(IndividualAsset), id, asset.SiteId, Normalization.OptionalText(reason),
            new { from = asset.State.ToString(), to = to.ToString() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<AssetType> LoadTypeAsync(int typeId, CancellationToken ct)
    {
        var type = await db.AssetTypes.AsNoTracking().Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new DomainException("asset.type_invalid", "Choose an asset type.");
        type.Fields = type.Fields.OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToList();
        return type;
    }

    /// <summary>Standard fields (description, condition, serial number, vendor, model), limited to what the type uses.</summary>
    private async Task ApplyEditableFieldsAsync(IndividualAsset asset, AssetType type, string? description, string? condition,
        string? serialNumber, int? vendorId, int? modelId, CancellationToken ct)
    {
        var text = type.UsesDescription ? description?.Trim() ?? "" : "";
        if (text.Length > 500)
            throw new DomainException("asset.description_length", "Description may be at most 500 characters.");

        asset.Description = text;
        asset.Condition = type.UsesCondition ? Limit(Normalization.OptionalText(condition), 200, "Condition") : null;
        asset.SerialNumber = type.UsesSerialNumber ? Limit(Normalization.OptionalText(serialNumber), 128, "Serial number") : null;

        if (!type.UsesModel)
        {
            asset.VendorId = asset.ModelId = null;
            return;
        }
        if (modelId is { } mid)
        {
            var model = await db.Models.AsNoTracking().Include(m => m.Vendor).FirstOrDefaultAsync(m => m.Id == mid, ct)
                ?? throw new DomainException("asset.model_invalid", "Choose a model from the list.");
            if (model.TypeId != type.Id)
                throw new DomainException("asset.model_type", $"{model.DisplayName} is not a model of {type.Name}.");
            if (vendorId is { } v && v != model.VendorId)
                throw new DomainException("asset.model_vendor", $"{model.DisplayName} is not a model of the chosen vendor.");
            if ((!model.IsActive || !model.Vendor!.IsActive) && asset.ModelId != mid)
                throw new DomainException("asset.model_inactive", $"{model.DisplayName} is inactive.");
            asset.ModelId = mid;
            asset.VendorId = model.VendorId;
        }
        else if (vendorId is { } vid)
        {
            var vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct)
                ?? throw new DomainException("asset.vendor_invalid", "Choose a vendor from the list.");
            if (!vendor.IsActive && asset.VendorId != vid)
                throw new DomainException("asset.vendor_inactive", $"{vendor.Name} is inactive.");
            asset.VendorId = vid;
            asset.ModelId = null;
        }
        else
        {
            asset.VendorId = asset.ModelId = null;
        }
    }

    private sealed record NewValue(AssetFieldDefinition Field, int Position, string Value, string? UniqueKey);

    /// <summary>
    /// Validates the submitted custom field values against the type's active fields. A field absent from
    /// <paramref name="submitted"/> keeps <paramref name="existing"/> (so an edit that does not mention it leaves it alone).
    /// </summary>
    private static List<NewValue> BuildValues(AssetType type, IReadOnlyDictionary<int, IReadOnlyList<string?>>? submitted,
        IReadOnlyCollection<(int FieldId, int Position, string Value)> existing)
    {
        var result = new List<NewValue>();
        foreach (var field in type.Fields.Where(f => f.IsActive))
        {
            var current = existing.Where(e => e.FieldId == field.Id).OrderBy(e => e.Position).Select(e => e.Value).ToList();
            List<string> values;
            if (submitted is not null && submitted.TryGetValue(field.Id, out var raw))
            {
                values = [];
                foreach (var r in raw)
                {
                    // A stored choice that has since been removed from the options stays valid until changed.
                    var kept = field.Kind == FieldKind.Choice
                        ? current.FirstOrDefault(c => string.Equals(c, r?.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
                    var value = kept ?? FieldValueRules.Normalize(field, r);
                    if (value is not null) values.Add(value);
                }
            }
            else values = current;

            if (values.Count > field.MaxValues)
                throw new DomainException("asset.field_values", $"{field.Name} takes at most {field.MaxValues} value(s).");
            if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
                throw new DomainException("asset.field_duplicate", $"{field.Name}: the values must be different from each other.");
            if (field.IsRequired && values.Count == 0)
                throw new DomainException("asset.field_required", $"{field.Name} is required.");

            for (var i = 0; i < values.Count; i++)
                result.Add(new NewValue(field, i, values[i], FieldValueRules.UniqueKey(field, values[i])));
        }
        return result;
    }

    private static List<NewValue> ExistingAsValues(AssetType type, IEnumerable<AssetFieldValue> stored) =>
        stored.Select(v => (Value: v, Field: type.Fields.FirstOrDefault(f => f.Id == v.FieldId)))
            .Where(x => x.Field is not null)
            .Select(x => new NewValue(x.Field!, x.Value.Position, x.Value.Value, x.Value.UniqueKey)).ToList();

    private static string? Limit(string? value, int max, string field) =>
        value is not null && value.Length > max
            ? throw new DomainException("asset.field_length", $"{field} may be at most {max} characters.")
            : value;

    /// <summary>Friendly pre-check; the filtered unique index on <c>UniqueKey</c> remains the real guarantee (see <see cref="SaveAsync"/>).</summary>
    private async Task EnsureValuesFreeAsync(IEnumerable<NewValue> values, int assetId, CancellationToken ct)
    {
        foreach (var v in values.Where(v => v.UniqueKey is not null))
        {
            var key = v.UniqueKey;
            if (!await db.FieldValues.AnyAsync(x => x.UniqueKey == key && x.AssetId != assetId, ct)) continue;
            throw v.Field.Kind switch
            {
                FieldKind.PhoneNumber => new ConflictException("asset.phone_taken", $"Phone number {v.Value} is already assigned to another active asset."),
                FieldKind.Imei => new ConflictException("asset.imei_taken", $"IMEI {v.Value} is already assigned to another active asset."),
                _ => new ConflictException("asset.value_taken", $"{v.Field.Name} {v.Value} is already in use by another active asset."),
            };
        }
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
            // Lost a race on a unique index (tag or a unique field value) after the pre-checks passed.
            throw new ConflictException("asset.duplicate",
                "The asset tag or one of its unique values (such as a phone number or IMEI) is already in use by another asset.");
        }
    }

    private async Task<object> SnapshotAsync(IndividualAsset a, AssetType type, IEnumerable<NewValue> values, CancellationToken ct)
    {
        var vendor = a.VendorId is { } vid ? await db.Vendors.AsNoTracking().Where(v => v.Id == vid).Select(v => v.Name).FirstOrDefaultAsync(ct) : null;
        var model = a.ModelId is { } mid ? await db.Models.AsNoTracking().Where(m => m.Id == mid).Select(m => m.Name).FirstOrDefaultAsync(ct) : null;
        // Sensitive values (a SIM PUC) are never copied into the audit trail.
        var fields = values.GroupBy(v => v.Field.Name).ToDictionary(g => g.Key,
            g => g.First().Field.IsSensitive ? ["(sensitive)"] : g.OrderBy(v => v.Position).Select(v => v.Value).ToArray());
        return new { a.Tag, Type = type.Name, a.SiteId, Vendor = vendor, Model = model, a.Description, a.Condition, a.SerialNumber, Fields = fields };
    }
}
