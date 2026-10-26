namespace AssetTracker.Core.Domain;

/// <summary>Site-specific quantity pool (one per SKU + site) for peripherals, webcams, headsets.</summary>
public class Pool
{
    public int Id { get; set; }
    public AssetType Type { get; set; }
    public string Sku { get; set; } = "";
    public string Description { get; set; } = "";

    public int SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Units on the shelf. Never negative.</summary>
    public int Available { get; set; }

    /// <summary>Units currently out on loan. OnHand = Available + Outstanding.</summary>
    public int Outstanding { get; set; }

    public int OnHand => Available + Outstanding;

    public DateTimeOffset CreatedAt { get; set; }
}
