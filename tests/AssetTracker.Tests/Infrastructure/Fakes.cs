using System.Collections.Concurrent;
using AssetTracker.Core.Abstractions;

namespace AssetTracker.Tests.Infrastructure;

public class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Tests use UTC as the client timezone unless they set one explicitly.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    public DateOnly LocalToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, TimeZone).DateTime);

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

public class FakeDirectorySource : IDirectorySource
{
    public List<DirectoryEntry> Entries { get; set; } = [];

    /// <summary>When set, the next fetch throws this exception.</summary>
    public Exception? FailWith { get; set; }

    public Task<IReadOnlyList<DirectoryEntry>> FetchAllUsersAsync(CancellationToken ct)
    {
        if (FailWith is { } ex) throw ex;
        return Task.FromResult<IReadOnlyList<DirectoryEntry>>(Entries.ToList());
    }
}

public class FakeMailRelay : IMailRelay
{
    public ConcurrentQueue<MailMessage> Sent { get; } = new();

    /// <summary>When set, sends throw this exception (simulates a relay outage).</summary>
    public Exception? FailWith { get; set; }

    public Task SendAsync(MailMessage message, CancellationToken ct)
    {
        if (FailWith is { } ex) throw ex;
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
