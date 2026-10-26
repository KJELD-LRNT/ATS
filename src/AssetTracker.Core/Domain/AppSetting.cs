namespace AssetTracker.Core.Domain;

/// <summary>Admin-configurable key/value settings (e.g. IT mailbox).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    public const string ItMailbox = "Mail.ItMailbox";
}
