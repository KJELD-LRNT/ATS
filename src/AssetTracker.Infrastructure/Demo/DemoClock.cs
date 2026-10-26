using System.Globalization;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// Demo mode clock: the real time plus a "time travel" offset. The offset is stored in the <c>AppSettings</c> row
/// <see cref="OffsetKey"/> (so it survives a restart) and cached in memory: the demo runs in a single process and
/// every change goes through <see cref="SetOffsetAsync"/>. Everything that uses <see cref="IClock"/> and
/// <c>SiteCalendar</c> (due dates, overdue, reminders, reports) follows the shifted time.
/// <para>Seeding pins the clock to historical instants for the current async flow only (<see cref="RunAtAsync{T}"/>),
/// so background jobs keep seeing the normal demo time meanwhile.</para>
/// </summary>
public sealed class DemoClock(IServiceScopeFactory scopes) : IClock
{
    public const string OffsetKey = "Demo.ClockOffset";

    private readonly AsyncLocal<DateTimeOffset?> _pinned = new();
    private long _offsetTicks;

    public DateTimeOffset UtcNow => _pinned.Value ?? DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>Runs <paramref name="action"/> with the clock fixed at <paramref name="instant"/> (this async flow only).</summary>
    public async Task<T> RunAtAsync<T>(DateTimeOffset instant, Func<Task<T>> action)
    {
        var previous = _pinned.Value;
        _pinned.Value = instant;
        try { return await action(); }
        finally { _pinned.Value = previous; }
    }

    public Task RunAtAsync(DateTimeOffset instant, Func<Task> action) =>
        RunAtAsync<object?>(instant, async () => { await action(); return null; });

    /// <summary>Reads the stored offset (start-up and after a data reset).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == OffsetKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var offset = TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero;
        Interlocked.Exchange(ref _offsetTicks, offset.Ticks);
    }

    /// <summary>
    /// Sets the offset (zero = real time) and stores it. When time moves backwards, pending outbox rows scheduled
    /// in the abandoned "future" are made due now, so they are not stuck until the real clock catches up.
    /// </summary>
    public async Task SetOffsetAsync(TimeSpan offset, CancellationToken ct = default)
    {
        if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var realNow = DateTimeOffset.UtcNow;
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == OffsetKey, ct);
        if (row is null)
        {
            row = new AppSetting { Key = OffsetKey };
            db.Settings.Add(row);
        }
        row.Value = offset.ToString("c", CultureInfo.InvariantCulture);
        row.UpdatedAt = realNow;
        row.UpdatedBy = "demo:time-travel";
        await db.SaveChangesAsync(ct);
        var previous = Offset;
        Interlocked.Exchange(ref _offsetTicks, offset.Ticks);

        if (offset < previous)
        {
            var now = UtcNow;
            // Load and save (not ExecuteUpdate): SQLite stores DateTimeOffset as a converted value.
            var stuck = await db.Outbox.Where(o => o.Status == NotificationStatus.Pending).ToListAsync(ct);
            foreach (var o in stuck.Where(o => o.NextAttemptAt > now)) o.NextAttemptAt = now;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Forgets the offset in memory (the database row was wiped by a demo reset).</summary>
    internal void ResetInMemory() => Interlocked.Exchange(ref _offsetTicks, 0);
}
