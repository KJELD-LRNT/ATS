using System.Diagnostics;
using System.Text.Json;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;

namespace AssetTracker.Core.Auditing;

/// <summary>
/// Stages an <see cref="AuditEvent"/> on the current DbContext. It is persisted by the caller's
/// SaveChanges, i.e. in the same transaction as the change it describes.
/// </summary>
public class AuditWriter(AppDbContext db, ICurrentUser user, IClock clock)
{
    public AuditEvent Record(string action, string entityType, object? entityId, int? siteId,
        string? reason = null, object? details = null)
    {
        var e = new AuditEvent
        {
            OccurredAt = clock.UtcNow,
            Actor = user.Name,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            SiteId = siteId,
            Reason = reason,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            CorrelationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
        };
        db.AuditEvents.Add(e);
        return e;
    }
}
