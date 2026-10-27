using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Overview;

/// <summary>
/// Which overview tiles each role sees. Stored as <c>AppSettings</c> rows (<c>Overview.Tile.{key}</c> = the roles, e.g.
/// "It, Manager" or "None"); a tile without a row uses its default (<see cref="TileDefinition.DefaultRoles"/>). A role can
/// never be given a tile outside <see cref="TileDefinition.AllowedRoles"/>. Reading is open to every signed-in user (the
/// page decides what to show); changing needs the admin role and is audited.
/// </summary>
public class OverviewTileService(AppDbContext db, AccessScope access, AuditWriter audit, ICurrentUser user, IClock clock)
{
    public const string KeyPrefix = "Overview.Tile.";

    /// <summary>The roles each tile is shown to right now (defaults applied, limited to the allowed roles).</summary>
    public async Task<IReadOnlyDictionary<string, AppRoles>> GetRolesAsync(CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(KeyPrefix))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return OverviewTiles.All.ToDictionary(t => t.Key, t =>
            rows.TryGetValue(KeyPrefix + t.Key, out var v) && Enum.TryParse<AppRoles>(v, true, out var roles)
                ? roles & t.AllowedRoles
                : t.DefaultRoles & t.AllowedRoles);
    }

    /// <summary>The tiles a principal with these roles sees (a tile is shown when any of their roles is enabled for it).</summary>
    public async Task<IReadOnlySet<string>> VisibleForAsync(AppRoles roles, CancellationToken ct = default)
    {
        var config = await GetRolesAsync(ct);
        return config.Where(kv => (kv.Value & roles) != AppRoles.None).Select(kv => kv.Key).ToHashSet();
    }

    public Task<IReadOnlySet<string>> VisibleForCurrentUserAsync(CancellationToken ct = default) => VisibleForAsync(user.Roles, ct);

    /// <summary>Saves the roles per tile (admin only). Tiles missing from <paramref name="roles"/> keep their current setting.</summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, AppRoles> roles, CancellationToken ct = default)
    {
        access.EnsureAdmin();
        foreach (var key in roles.Keys)
            if (OverviewTiles.Find(key) is null) throw new DomainException("overview.tile_unknown", $"Unknown tile '{key}'.");

        var before = await GetRolesAsync(ct);
        var rows = await db.Settings.Where(s => s.Key.StartsWith(KeyPrefix)).ToDictionaryAsync(s => s.Key, ct);
        var now = clock.UtcNow;
        foreach (var (key, requested) in roles)
        {
            var tile = OverviewTiles.Find(key)!;
            var value = (requested & tile.AllowedRoles & (AppRoles.It | AppRoles.Manager)).ToString();
            if (rows.TryGetValue(KeyPrefix + key, out var row))
            {
                row.Value = value;
                row.UpdatedAt = now;
                row.UpdatedBy = user.Name;
            }
            else
            {
                db.Settings.Add(new AppSetting { Key = KeyPrefix + key, Value = value, UpdatedAt = now, UpdatedBy = user.Name });
            }
        }
        var after = OverviewTiles.All.ToDictionary(t => t.Key, t =>
            roles.TryGetValue(t.Key, out var r) ? r & t.AllowedRoles & (AppRoles.It | AppRoles.Manager) : before[t.Key]);
        audit.Record("overview.tiles", "OverviewTiles", null, null, details: new
        {
            before = before.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
            after = after.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Back to the defaults for every tile (admin only).</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        access.EnsureAdmin();
        var rows = await db.Settings.Where(s => s.Key.StartsWith(KeyPrefix)).ToListAsync(ct);
        db.Settings.RemoveRange(rows);
        audit.Record("overview.tiles.reset", "OverviewTiles", null, null, details: new { cleared = rows.Count });
        await db.SaveChangesAsync(ct);
    }
}
