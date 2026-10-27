using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Workplace;

/// <summary>
/// Teams on a floor plan: clusters of workspots drawn in one colour with a name. They exist only on the map (no link to
/// the directory or to loans) and can be renamed at will. A workspot is in at most one team.
/// </summary>
public class TeamService(AppDbContext db, AccessScope scope, AuditWriter audit, WorkspotService spotService, IClock clock)
{
    /// <summary>Size of the map's colour palette (<c>fp-team-0</c>…<c>fp-team-7</c> in site.css).</summary>
    public const int Colors = 8;
    public const int NameMax = 100;

    public async Task<FloorTeam> CreateAsync(string name, IReadOnlyCollection<int> workspotIds, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        name = ValidName(name);
        var spots = await spotService.LoadSameFloorAsync(workspotIds, ct);
        if (spots.Any(s => !s.IsActive)) throw new DomainException("team.inactive", "A switched-off workspot cannot join a team.");
        var floor = spots[0].Floor!;
        var teams = await db.FloorTeams.Where(t => t.FloorId == floor.Id).ToListAsync(ct);
        EnsureNameFree(teams, name, null);

        var used = teams.Select(t => t.ColorIndex).ToHashSet();
        var color = Enumerable.Range(0, Colors).FirstOrDefault(i => !used.Contains(i), teams.Count % Colors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var team = new FloorTeam { FloorId = floor.Id, Name = name, ColorIndex = color, CreatedAt = clock.UtcNow };
        db.FloorTeams.Add(team);
        await db.SaveChangesAsync(ct);
        foreach (var s in spots) s.TeamId = team.Id;
        audit.Record("team.create", nameof(FloorTeam), team.Id, floor.SiteId,
            details: new { FloorId = floor.Id, Floor = floor.Name, team.Name, Workspots = spots.Select(s => s.Label).ToList() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return team;
    }

    /// <summary>Puts workspots into a team (moving them out of any other team).</summary>
    public async Task<int> AddAsync(int teamId, IReadOnlyCollection<int> workspotIds, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var team = await LoadAsync(teamId, ct);
        var spots = await spotService.LoadSameFloorAsync(workspotIds, ct);
        if (spots[0].FloorId != team.FloorId) throw new DomainException("workspot.floors", "The workspots are on another floor than the team.");
        if (spots.Any(s => !s.IsActive)) throw new DomainException("team.inactive", "A switched-off workspot cannot join a team.");
        var moving = spots.Where(s => s.TeamId != team.Id).ToList();
        if (moving.Count == 0) return 0;
        foreach (var s in moving) s.TeamId = team.Id;
        audit.Record("team.add", nameof(FloorTeam), team.Id, team.Floor!.SiteId,
            details: new { team.Name, Workspots = moving.Select(s => s.Label).ToList() });
        await db.SaveChangesAsync(ct);
        return moving.Count;
    }

    /// <summary>Takes workspots out of whatever team they are in.</summary>
    public async Task<int> RemoveAsync(IReadOnlyCollection<int> workspotIds, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var spots = await spotService.LoadSameFloorAsync(workspotIds, ct);
        var moving = spots.Where(s => s.TeamId != null).ToList();
        if (moving.Count == 0) return 0;
        var teams = await db.FloorTeams.Where(t => moving.Select(s => s.TeamId).Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var s in moving) s.TeamId = null;
        var floor = spots[0].Floor!;
        audit.Record("team.remove", nameof(Floor), floor.Id, floor.SiteId, details: new
        {
            floor.Name, Workspots = moving.Select(s => s.Label).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return moving.Count;
    }

    public async Task RenameAsync(int teamId, string name, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        name = ValidName(name);
        var team = await LoadAsync(teamId, ct);
        if (team.Name == name) return;
        var teams = await db.FloorTeams.Where(t => t.FloorId == team.FloorId).ToListAsync(ct);
        EnsureNameFree(teams, name, team.Id);
        var before = team.Name;
        team.Name = name;
        audit.Record("team.rename", nameof(FloorTeam), team.Id, team.Floor!.SiteId, details: new { Before = before, After = name });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetColorAsync(int teamId, int colorIndex, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        if (colorIndex is < 0 or >= Colors) throw new DomainException("team.color", "Unknown colour.");
        var team = await LoadAsync(teamId, ct);
        if (team.ColorIndex == colorIndex) return;
        var before = team.ColorIndex;
        team.ColorIndex = colorIndex;
        audit.Record("team.color", nameof(FloorTeam), team.Id, team.Floor!.SiteId, details: new { team.Name, Before = before, After = colorIndex });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the team; its workspots stay on the floor, ungrouped.</summary>
    public async Task DeleteAsync(int teamId, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var team = await LoadAsync(teamId, ct);
        var spots = await db.Workspots.Where(w => w.TeamId == teamId).ToListAsync(ct);
        foreach (var s in spots) s.TeamId = null;
        await db.SaveChangesAsync(ct);
        db.FloorTeams.Remove(team);
        audit.Record("team.delete", nameof(FloorTeam), team.Id, team.Floor!.SiteId, details: new { team.Name, Workspots = spots.Count });
        await db.SaveChangesAsync(ct);
    }

    private async Task<FloorTeam> LoadAsync(int id, CancellationToken ct) =>
        await db.FloorTeams.Include(t => t.Floor).SingleOrDefaultAsync(t => t.Id == id, ct)
        ?? throw new DomainException("team.not-found", "Team not found.");

    private static string ValidName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new DomainException("team.name", "Give the team a name.");
        if (name.Length > NameMax) throw new DomainException("team.name", $"The name can have at most {NameMax} characters.");
        return name;
    }

    private static void EnsureNameFree(IEnumerable<FloorTeam> teams, string name, int? exceptId)
    {
        if (teams.Any(t => t.Id != exceptId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("team.exists", $"This floor already has a team called '{name}'.");
    }
}
