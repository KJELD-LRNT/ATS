namespace AssetTracker.Core.Domain;

/// <summary>How items of a type are tracked: one tag per item, or a quantity pool per site.</summary>
public enum InventoryMode
{
    /// <summary>Every item is an <see cref="IndividualAsset"/> with its own tag.</summary>
    Tagged = 1,

    /// <summary>Items are counted in a site <see cref="Pool"/>.</summary>
    Pooled = 2,
}

/// <summary>
/// Admin-defined kind of inventory (laptop, phone, phone number, headset ...). Decides how items are tracked, which
/// standard fields their form shows, how their tags are generated and which custom fields they carry
/// (<see cref="AssetFieldDefinition"/>). Types are never deleted: deactivate to hide one from new items.
/// </summary>
public class AssetType
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public InventoryMode Mode { get; set; } = InventoryMode.Tagged;

    /// <summary>Inactive types cannot be chosen for new assets or pools; existing items keep working.</summary>
    public bool IsActive { get; set; } = true;

    // Standard fields of a tagged asset's form (a phone number, for instance, has none of them).
    public bool UsesModel { get; set; } = true;
    public bool UsesDescription { get; set; } = true;
    public bool UsesSerialNumber { get; set; } = true;
    public bool UsesCondition { get; set; } = true;

    /// <summary>Tag generator pattern (see <c>TagPattern</c>), e.g. <c>{SITE}D{000}OMA</c>. Null = tags are typed by hand.</summary>
    public string? TagPattern { get; set; }

    public List<AssetFieldDefinition> Fields { get; set; } = [];

    // Rendering a type (mail, lists, reports) shows its name.
    public override string ToString() => Name;
}

/// <summary>
/// Ids of the types and fields every installation starts with (inserted by the first migration and by
/// <c>CatalogDefaults</c> for databases created from the model). They are ordinary rows afterwards: admins may rename,
/// reconfigure or deactivate them. Code that needs "the phone number field" looks at <see cref="FieldKind"/>, not at ids.
/// </summary>
public static class BuiltIn
{
    public const int Desktop = 1;
    public const int Laptop = 2;
    public const int Phone = 3;
    public const int PhoneNumber = 4;
    public const int Peripheral = 5;
    public const int Webcam = 6;
    public const int Headset = 7;

    public const int PhoneImeiField = 1;
    public const int PhonePhoneNumberField = 2;
    public const int LinePhoneNumberField = 3;
    public const int LineProviderField = 4;
    public const int LineSimTypeField = 5;
    public const int LineSimStatusField = 6;
    public const int LineSimNumberField = 7;
    public const int LinePucField = 8;
}
