namespace AssetTracker.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Today's calendar date in the configured client timezone.</summary>
    DateOnly LocalToday { get; }
}
