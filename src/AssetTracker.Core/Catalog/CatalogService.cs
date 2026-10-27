using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Catalog;

/// <param name="TagPattern">See <see cref="Inventory.TagPattern"/>; blank = tags are typed by hand.</param>
public sealed record TypeInput(
    string Name, InventoryMode Mode, bool UsesModel = true, bool UsesDescription = true, bool UsesSerialNumber = true,
    bool UsesCondition = true, string? TagPattern = null, bool IsActive = true);

/// <param name="Options">Choice fields: the allowed values, one per line.</param>
public sealed record FieldInput(
    string Name, FieldKind Kind, bool IsRequired = false, bool IsUnique = false, bool IsSensitive = false,
    int MaxValues = 1, string? Options = null, bool IsActive = true);

/// <summary>
/// The admin-defined catalog that asset forms are built from: asset types (with their standard fields and tag
/// pattern), custom fields per type, vendors and models. Everything here is reference data: anyone signed in may
/// read it (the pages decide who sees what), changes need the admin role and are audited. Nothing is deleted;
/// deactivate instead, because assets keep pointing at what they were created with.
/// </summary>
public class CatalogService(AppDbContext db, AccessScope access, AuditWriter audit)
{
    public const int NameMaxLength = 100;
    public const int ModelNameMaxLength = 200;

    // ---- Types ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AssetType>> ListTypesAsync(InventoryMode? mode = null, bool includeInactive = false,
        CancellationToken ct = default)
    {
        var q = db.AssetTypes.AsNoTracking().Include(t => t.Fields).AsQueryable();
        if (mode is { } m) q = q.Where(t => t.Mode == m);
        if (!includeInactive) q = q.Where(t => t.IsActive);
        return await q.OrderBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>A type with its fields (active and inactive) in display order.</summary>
    public async Task<AssetType> GetTypeAsync(int id, CancellationToken ct = default)
    {
        var type = await db.AssetTypes.AsNoTracking().Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new DomainException("type.not_found", "Asset type not found.");
        type.Fields = type.Fields.OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToList();
        return type;
    }

    public async Task<AssetType> CreateTypeAsync(TypeInput input, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var name = RequireName(input.Name, "Type name");
        if (!Enum.IsDefined(input.Mode)) throw new DomainException("type.mode", "Choose how the type is tracked.");
        await EnsureTypeNameFreeAsync(name, null, ct);

        var type = new AssetType { Name = name };
        Apply(type, input);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AssetTypes.Add(type);
        await SaveAsync(ct, "type.duplicate", $"An asset type named {name} already exists.");
        audit.Record("type.create", nameof(AssetType), type.Id, null, details: Snapshot(type));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return type;
    }

    public async Task<AssetType> UpdateTypeAsync(int id, TypeInput input, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var type = await db.AssetTypes.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new DomainException("type.not_found", "Asset type not found.");
        var name = RequireName(input.Name, "Type name");
        await EnsureTypeNameFreeAsync(name, id, ct);

        if (input.Mode != type.Mode)
        {
            if (!Enum.IsDefined(input.Mode)) throw new DomainException("type.mode", "Choose how the type is tracked.");
            var inUse = await db.Assets.AnyAsync(a => a.TypeId == id, ct) || await db.Pools.AnyAsync(p => p.TypeId == id, ct) ||
                        await db.FieldDefinitions.AnyAsync(f => f.TypeId == id, ct) || await db.Models.AnyAsync(m => m.TypeId == id, ct);
            if (inUse)
                throw new DomainException("type.mode_locked",
                    "How a type is tracked cannot change once it has assets, pools, fields or models. Create a new type instead.");
        }

        var before = Snapshot(type);
        type.Name = name;
        Apply(type, input);
        audit.Record("type.update", nameof(AssetType), type.Id, null, details: new { before, after = Snapshot(type) });
        await SaveAsync(ct, "type.duplicate", $"An asset type named {name} already exists.");
        return type;
    }

    private static void Apply(AssetType type, TypeInput input)
    {
        type.Mode = input.Mode;
        type.IsActive = input.IsActive;
        if (input.Mode == InventoryMode.Pooled)
        {
            // Pools carry only an SKU and a description: no tags, models or fields.
            type.UsesModel = type.UsesDescription = type.UsesSerialNumber = type.UsesCondition = false;
            type.TagPattern = null;
            return;
        }
        type.UsesModel = input.UsesModel;
        type.UsesDescription = input.UsesDescription;
        type.UsesSerialNumber = input.UsesSerialNumber;
        type.UsesCondition = input.UsesCondition;
        type.TagPattern = TagPattern.Validate(input.TagPattern);
    }

    // ---- Custom fields -------------------------------------------------------------------------------------

    public async Task<AssetFieldDefinition> AddFieldAsync(int typeId, FieldInput input, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var type = await db.AssetTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new DomainException("type.not_found", "Asset type not found.");
        if (type.Mode != InventoryMode.Tagged)
            throw new DomainException("field.pooled", "Custom fields only apply to types tracked by asset tag.");

        var field = new AssetFieldDefinition { TypeId = typeId };
        await ApplyFieldAsync(field, input, creating: true, ct);
        var last = await db.FieldDefinitions.Where(f => f.TypeId == typeId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        field.SortOrder = last + 10;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.FieldDefinitions.Add(field);
        await SaveAsync(ct, "field.duplicate", $"{type.Name} already has a field named {field.Name}.");
        audit.Record("field.create", nameof(AssetFieldDefinition), field.Id, null, details: Snapshot(type, field));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return field;
    }

    public async Task<AssetFieldDefinition> UpdateFieldAsync(int fieldId, FieldInput input, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var field = await db.FieldDefinitions.Include(f => f.Type).FirstOrDefaultAsync(f => f.Id == fieldId, ct)
            ?? throw new DomainException("field.not_found", "Field not found.");
        var before = Snapshot(field.Type!, field);
        await ApplyFieldAsync(field, input, creating: false, ct);
        audit.Record("field.update", nameof(AssetFieldDefinition), field.Id, null,
            details: new { before, after = Snapshot(field.Type!, field) });
        await SaveAsync(ct, "field.duplicate", $"{field.Type!.Name} already has a field named {field.Name}.");
        return field;
    }

    /// <summary>Moves a field one place up or down in its type's form.</summary>
    public async Task MoveFieldAsync(int fieldId, bool up, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var field = await db.FieldDefinitions.FirstOrDefaultAsync(f => f.Id == fieldId, ct)
            ?? throw new DomainException("field.not_found", "Field not found.");
        var siblings = await db.FieldDefinitions.Where(f => f.TypeId == field.TypeId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        var index = siblings.FindIndex(f => f.Id == fieldId);
        var other = up ? index - 1 : index + 1;
        if (other < 0 || other >= siblings.Count) return;

        // Renumber so equal sort orders (older rows) cannot make a move a no-op.
        (siblings[index], siblings[other]) = (siblings[other], siblings[index]);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = (i + 1) * 10;
        audit.Record("field.reorder", nameof(AssetFieldDefinition), fieldId, null, details: new { direction = up ? "up" : "down" });
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyFieldAsync(AssetFieldDefinition field, FieldInput input, bool creating, CancellationToken ct)
    {
        var name = RequireName(input.Name, "Field name");
        if (!Enum.IsDefined(input.Kind)) throw new DomainException("field.kind", "Choose the kind of field.");
        if (input.MaxValues is < 1 or > FieldValueRules.MaxValuesLimit)
            throw new DomainException("field.max_values", $"A field takes 1 to {FieldValueRules.MaxValuesLimit} values.");
        if (input.IsUnique && input.Kind is FieldKind.Choice or FieldKind.Date)
            throw new DomainException("field.unique_kind", "Only text, number, phone number and IMEI fields can be unique.");

        string? options = null;
        if (input.Kind == FieldKind.Choice)
        {
            var list = FieldValueRules.ParseOptions(input.Options);
            if (list.Count == 0) throw new DomainException("field.options", "A choice field needs at least one option (one per line).");
            if (list.Count > FieldValueRules.MaxOptions)
                throw new DomainException("field.options", $"A choice field may have at most {FieldValueRules.MaxOptions} options.");
            if (list.Any(o => o.Length > NameMaxLength))
                throw new DomainException("field.options", $"An option may be at most {NameMaxLength} characters.");
            options = string.Join('\n', list);
        }

        if (!creating)
        {
            var hasValues = await db.FieldValues.AnyAsync(v => v.FieldId == field.Id, ct);
            if (hasValues)
            {
                if (input.Kind != field.Kind)
                    throw new DomainException("field.kind_locked", "The kind of a field cannot change once assets have a value for it.");
                if (input.IsUnique != field.IsUnique)
                    throw new DomainException("field.unique_locked", "Uniqueness cannot change once assets have a value for the field.");
                if (input.MaxValues < field.MaxValues)
                    throw new DomainException("field.max_values_locked", "A field cannot take fewer values once assets have values for it.");
            }
        }

        var clash = await db.FieldDefinitions.AsNoTracking().Where(f => f.TypeId == field.TypeId && f.Id != field.Id)
            .Select(f => f.Name).ToListAsync(ct);
        if (clash.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("field.duplicate", $"There is already a field named {name} on this type.");

        field.Name = name;
        field.Kind = input.Kind;
        field.IsRequired = input.IsRequired;
        field.IsUnique = input.IsUnique;
        field.IsSensitive = input.IsSensitive;
        field.MaxValues = input.MaxValues;
        field.Options = options;
        field.IsActive = input.IsActive;
    }

    // ---- Vendors and models --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Vendor>> ListVendorsAsync(bool includeInactive = false, CancellationToken ct = default) =>
        await db.Vendors.AsNoTracking().Where(v => includeInactive || v.IsActive).OrderBy(v => v.Name).ToListAsync(ct);

    public async Task<Vendor> CreateVendorAsync(string name, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        name = RequireName(name, "Vendor name");
        await EnsureVendorNameFreeAsync(name, null, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var vendor = new Vendor { Name = name };
        db.Vendors.Add(vendor);
        await SaveAsync(ct, "vendor.duplicate", $"A vendor named {name} already exists.");
        audit.Record("vendor.create", nameof(Vendor), vendor.Id, null, details: new { vendor.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return vendor;
    }

    public async Task<Vendor> UpdateVendorAsync(int id, string name, bool isActive, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new DomainException("vendor.not_found", "Vendor not found.");
        name = RequireName(name, "Vendor name");
        await EnsureVendorNameFreeAsync(name, id, ct);

        var before = new { vendor.Name, vendor.IsActive };
        vendor.Name = name;
        vendor.IsActive = isActive;
        audit.Record("vendor.update", nameof(Vendor), vendor.Id, null, details: new { before, after = new { vendor.Name, vendor.IsActive } });
        await SaveAsync(ct, "vendor.duplicate", $"A vendor named {name} already exists.");
        return vendor;
    }

    /// <summary>Models with their vendor and type, by vendor and name. Filters are optional.</summary>
    public async Task<IReadOnlyList<AssetModel>> ListModelsAsync(int? typeId = null, int? vendorId = null,
        bool includeInactive = false, CancellationToken ct = default)
    {
        var q = db.Models.AsNoTracking().Include(m => m.Vendor).Include(m => m.Type).AsQueryable();
        if (typeId is { } t) q = q.Where(m => m.TypeId == t);
        if (vendorId is { } v) q = q.Where(m => m.VendorId == v);
        if (!includeInactive) q = q.Where(m => m.IsActive && m.Vendor!.IsActive);
        return await q.OrderBy(m => m.Vendor!.Name).ThenBy(m => m.Name).ToListAsync(ct);
    }

    public async Task<AssetModel> CreateModelAsync(int typeId, int vendorId, string name, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        name = RequireName(name, "Model name", ModelNameMaxLength);
        // Tracked, so the new model's Vendor and Type navigations are filled in by the context itself.
        var type = await db.AssetTypes.FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new DomainException("model.type", "Choose the asset type of the model.");
        if (type.Mode != InventoryMode.Tagged)
            throw new DomainException("model.pooled", "Models only apply to types tracked by asset tag.");
        var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.Id == vendorId, ct)
            ?? throw new DomainException("model.vendor", "Choose the vendor of the model.");
        await EnsureModelNameFreeAsync(typeId, vendorId, name, null, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var model = new AssetModel { TypeId = typeId, VendorId = vendorId, Name = name };
        db.Models.Add(model);
        await SaveAsync(ct, "model.duplicate", $"{vendor.Name} {name} already exists for {type.Name}.");
        audit.Record("model.create", nameof(AssetModel), model.Id, null,
            details: new { Vendor = vendor.Name, Type = type.Name, model.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return model;
    }

    /// <summary>Renames or (de)activates a model. Its vendor and type are fixed: assets were chosen against them.</summary>
    public async Task<AssetModel> UpdateModelAsync(int id, string name, bool isActive, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var model = await db.Models.Include(m => m.Vendor).Include(m => m.Type).FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new DomainException("model.not_found", "Model not found.");
        name = RequireName(name, "Model name", ModelNameMaxLength);
        await EnsureModelNameFreeAsync(model.TypeId, model.VendorId, name, id, ct);

        var before = new { model.Name, model.IsActive };
        model.Name = name;
        model.IsActive = isActive;
        audit.Record("model.update", nameof(AssetModel), model.Id, null,
            details: new { before, after = new { model.Name, model.IsActive } });
        await SaveAsync(ct, "model.duplicate", $"{model.Vendor!.Name} {name} already exists for {model.Type!.Name}.");
        return model;
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static string RequireName(string? value, string what, int max = NameMaxLength)
    {
        var s = value?.Trim() ?? "";
        if (s.Length == 0) throw new DomainException("catalog.name_required", $"{what} is required.");
        if (s.Length > max) throw new DomainException("catalog.name_length", $"{what} may be at most {max} characters.");
        return s;
    }

    private async Task EnsureTypeNameFreeAsync(string name, int? exceptId, CancellationToken ct)
    {
        var names = await db.AssetTypes.AsNoTracking().Where(t => t.Id != exceptId).Select(t => t.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("type.duplicate", $"An asset type named {name} already exists.");
    }

    private async Task EnsureVendorNameFreeAsync(string name, int? exceptId, CancellationToken ct)
    {
        var names = await db.Vendors.AsNoTracking().Where(v => v.Id != exceptId).Select(v => v.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("vendor.duplicate", $"A vendor named {name} already exists.");
    }

    private async Task EnsureModelNameFreeAsync(int typeId, int vendorId, string name, int? exceptId, CancellationToken ct)
    {
        var names = await db.Models.AsNoTracking()
            .Where(m => m.TypeId == typeId && m.VendorId == vendorId && m.Id != exceptId).Select(m => m.Name).ToListAsync(ct);
        if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("model.duplicate", "That model already exists for this vendor and type.");
    }

    private async Task SaveAsync(CancellationToken ct, string code, string message)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race on a unique name after the pre-check passed.
            throw new ConflictException(code, message);
        }
    }

    private static object Snapshot(AssetType t) => new
    {
        t.Name, Mode = t.Mode.ToString(), t.IsActive, t.UsesModel, t.UsesDescription, t.UsesSerialNumber, t.UsesCondition, t.TagPattern,
    };

    private static object Snapshot(AssetType type, AssetFieldDefinition f) => new
    {
        Type = type.Name, f.Name, Kind = f.Kind.ToString(), f.IsRequired, f.IsUnique, f.IsSensitive, f.MaxValues, f.Options, f.IsActive,
    };
}
