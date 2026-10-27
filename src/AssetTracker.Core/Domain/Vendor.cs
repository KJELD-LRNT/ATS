namespace AssetTracker.Core.Domain;

/// <summary>Manufacturer or brand, defined by admins and chosen from a dropdown on assets and models.</summary>
public class Vendor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public override string ToString() => Name;
}

/// <summary>A model of one vendor for one asset type (e.g. Acme / Laptop / "Pro 14"), defined by admins.</summary>
public class AssetModel
{
    public int Id { get; set; }

    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public int TypeId { get; set; }
    public AssetType? Type { get; set; }

    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;

    /// <summary>"Vendor Model" when the vendor is loaded.</summary>
    public string DisplayName => Vendor is null ? Name : $"{Vendor.Name} {Name}";

    public override string ToString() => DisplayName;
}

/// <summary>Last number handed out by the tag generator for one asset type at one site.</summary>
public class TagCounter
{
    public int TypeId { get; set; }
    public int SiteId { get; set; }
    public int LastNumber { get; set; }
}
