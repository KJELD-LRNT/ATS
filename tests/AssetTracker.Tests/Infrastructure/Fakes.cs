using System.Collections.Concurrent;
using AssetTracker.Core.Abstractions;

namespace AssetTracker.Tests.Infrastructure;

public class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The UTC calendar day. <c>AppFactory</c> sets the default timezone (<c>Clock:TimeZoneId</c>) to UTC, so this is
    /// every site's local day unless a test gives the site its own <c>Site.TimeZoneId</c>.
    /// </summary>
    public DateOnly UtcToday => DateOnly.FromDateTime(UtcNow.UtcDateTime);

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
