namespace AssetTracker.Core.Domain;

public class IndividualAsset
{
    /// <summary>Internal immutable id anchoring history.</summary>
    public int Id { get; set; }

    /// <summary>Unique, immutable asset tag.</summary>
    public string Tag { get; set; } = "";

    public int TypeId { get; set; }
    public AssetType? Type { get; set; }

    /// <summary>Current site. Governs manager visibility of this asset and its entire history.</summary>
    public int SiteId { get; set; }
    public Site? Site { get; set; }

    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public int? ModelId { get; set; }
    public AssetModel? Model { get; set; }

    /// <summary>Free text (distinct from the model); empty when the type does not use it.</summary>
    public string Description { get; set; } = "";
    public string? Condition { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>Values of the type's custom fields (phone numbers, IMEIs, SIM details ...).</summary>
    public List<AssetFieldValue> Values { get; set; } = [];

    public AssetState State { get; set; } = AssetState.Available;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>"Vendor Model" when a model is chosen (loaded), else the description.</summary>
    public string Title => Model is not null ? Model.DisplayName : Description;
}
