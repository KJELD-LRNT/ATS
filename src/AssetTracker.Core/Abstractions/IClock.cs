namespace AssetTracker.Core.Abstractions;

/// <summary>
/// The current instant. Deliberately has no "local today": calendar days depend on the site
/// (see <see cref="Time.SiteCalendar"/>), so nothing can silently use one global timezone for loan logic.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
