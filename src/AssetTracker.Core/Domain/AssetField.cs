namespace AssetTracker.Core.Domain;

public enum FieldKind
{
    Text = 1,
    Number = 2,
    Date = 3,

    /// <summary>One of the admin-defined <see cref="AssetFieldDefinition.Options"/>.</summary>
    Choice = 4,

    /// <summary>E.164 phone number ("+" and 8-15 digits). Unique across every phone number field.</summary>
    PhoneNumber = 5,

    /// <summary>15-digit IMEI with a valid check digit. Unique across every IMEI field.</summary>
    Imei = 6,
}

/// <summary>
/// A custom field an admin attached to an <see cref="AssetType"/>. A field may hold several values
/// (<see cref="MaxValues"/>, e.g. two IMEIs on a dual-SIM phone). Fields are deactivated instead of deleted because
/// assets keep the values they were given.
/// </summary>
public class AssetFieldDefinition
{
    public int Id { get; set; }

    public int TypeId { get; set; }
    public AssetType? Type { get; set; }

    public string Name { get; set; } = "";
    public FieldKind Kind { get; set; } = FieldKind.Text;

    /// <summary>The first value must be filled in (further values of a multi-value field are always optional).</summary>
    public bool IsRequired { get; set; }

    /// <summary>A value may be used by only one non-retired asset.</summary>
    public bool IsUnique { get; set; }

    /// <summary>Only IT sees the value (e.g. a SIM PUC); managers never get it in pages, search or reports.</summary>
    public bool IsSensitive { get; set; }

    /// <summary>How many values the field takes (1 = a normal field).</summary>
    public int MaxValues { get; set; } = 1;

    /// <summary>For <see cref="FieldKind.Choice"/>: the allowed values, one per line.</summary>
    public string? Options { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public override string ToString() => Name;
}

/// <summary>One value of a custom field on an asset. <see cref="Position"/> orders the values of a multi-value field.</summary>
public class AssetFieldValue
{
    public int Id { get; set; }

    public int AssetId { get; set; }
    public IndividualAsset? Asset { get; set; }

    public int FieldId { get; set; }
    public AssetFieldDefinition? Field { get; set; }

    public int Position { get; set; }

    /// <summary>Normalized value (E.164 phone number, 15-digit IMEI, ISO date, canonical choice ...).</summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Set while the field is unique and the asset is not retired; the filtered unique index on this column is what
    /// guarantees uniqueness. Cleared when the asset is retired so the value can be reused.
    /// </summary>
    public string? UniqueKey { get; set; }
}
