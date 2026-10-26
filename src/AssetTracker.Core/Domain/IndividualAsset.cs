namespace AssetTracker.Core.Domain;

public class IndividualAsset
{
    /// <summary>Internal immutable id anchoring history.</summary>
    public int Id { get; set; }

    /// <summary>Unique, immutable asset tag.</summary>
    public string Tag { get; set; } = "";

    public AssetType Type { get; set; }

    /// <summary>Current site. Governs manager visibility of this asset and its entire history.</summary>
    public int SiteId { get; set; }
    public Site? Site { get; set; }

    public string Description { get; set; } = "";
    public string? Condition { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>Normalized IMEI (digits only). Unique among non-retired assets.</summary>
    public string? Imei { get; set; }

    /// <summary>Normalized E.164 phone number. Unique among non-retired assets.</summary>
    public string? PhoneNumber { get; set; }

    public AssetState State { get; set; } = AssetState.Available;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}
