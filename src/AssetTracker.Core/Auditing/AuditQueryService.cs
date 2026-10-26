using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Auditing;

/// <summary>Audit log filter. Dates are inclusive calendar days in UTC.</summary>
public sealed record AuditFilter(string? Action = null, string? EntityType = null, string? EntityId = null,
    string? Actor = null, DateOnly? From = null, DateOnly? To = null);

/// <summary>
/// Read-only audit log. Visibility: IT only (pending client confirmation; see docs/development.md).
/// There is deliberately no update or delete path; <see cref="AppDbContext"/> rejects modified/deleted audit rows.
/// </summary>
public class AuditQueryService(AppDbContext db, AccessScope scope)
{
    public const int MaxPageSize = 200;

    public async Task<PagedResult<AuditEvent>> SearchAsync(AuditFilter filter, int page, int pageSize = 50,
        CancellationToken ct = default)
    {
        scope.EnsureIt();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var q = db.AuditEvents.AsNoTracking();
        if (Clean(filter.Action) is { } action)
            q = action.EndsWith('*') ? q.Where(e => e.Action.StartsWith(action.TrimEnd('*'))) : q.Where(e => e.Action == action);
        if (Clean(filter.EntityType) is { } entity) q = q.Where(e => e.EntityType == entity);
        if (Clean(filter.EntityId) is { } entityId) q = q.Where(e => e.EntityId == entityId);
        if (Clean(filter.Actor) is { } actor) q = q.Where(e => e.Actor == actor);
        if (filter.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            q = q.Where(e => e.OccurredAt >= start);
        }
        if (filter.To is { } to)
        {
            var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            q = q.Where(e => e.OccurredAt < end);
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<AuditEvent>(items, total, page, pageSize);
    }

    /// <summary>Distinct entity types, for the filter dropdown.</summary>
    public async Task<IReadOnlyList<string>> EntityTypesAsync(CancellationToken ct = default)
    {
        scope.EnsureIt();
        return await db.AuditEvents.Select(e => e.EntityType).Distinct().OrderBy(x => x).ToListAsync(ct);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
