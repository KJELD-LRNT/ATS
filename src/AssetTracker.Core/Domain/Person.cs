namespace AssetTracker.Core.Domain;

/// <summary>Read-only snapshot of an AD user, keyed on stable AD object identity.</summary>
public class Person
{
    public int Id { get; set; }

    /// <summary>Stable AD identity (e.g. objectGUID). Never changes on rename.</summary>
    public string DirectoryId { get; set; } = "";

    /// <summary>Windows SID (objectSid), used to match the signed-in Windows principal to this person.</summary>
    public string? Sid { get; set; }

    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Email { get; set; }

    public int? SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>Active per the latest successful sync. Missing/disabled in AD → false.</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Personal fields were replaced by a pseudonym under client direction (see <c>PrivacyService</c>).
    /// Directory sync never updates or reactivates an anonymized person.
    /// </summary>
    public bool IsAnonymized { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }

    public bool CanReceiveNewLoan => IsActive && !string.IsNullOrWhiteSpace(Email);
}
