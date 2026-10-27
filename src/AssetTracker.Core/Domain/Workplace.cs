namespace AssetTracker.Core.Domain;

/// <summary>
/// An imported floor plan of one site. <see cref="Svg"/> is the drawing after sanitising (no scripts, no inline styles,
/// class names prefixed with <c>fp-</c>); <see cref="Css"/> its stylesheet, scoped to <c>.floorplan</c>. Floors are
/// archived (<see cref="IsActive"/>), never deleted, because later assignments point at their workspots.
/// </summary>
public class Floor
{
    public int Id { get; set; }

    public int SiteId { get; set; }
    public Site? Site { get; set; }

    public string Name { get; set; } = "";
    public int SortOrder { get; set; }

    public string Svg { get; set; } = "";
    public string Css { get; set; } = "";

    /// <summary>The drawing's <c>viewBox</c> ("minX minY width height").</summary>
    public string ViewBox { get; set; } = "";

    /// <summary>Counts the imports of this floor (1 = first import); also the cache-buster of its stylesheet.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset ImportedAt { get; set; }
    public string ImportedBy { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public List<Workspot> Workspots { get; set; } = [];
    public List<FloorTeam> Teams { get; set; } = [];
}

/// <summary>
/// A seat on a floor plan: one element of the drawing (<see cref="ElementId"/>). Seats come from the drawing's
/// <c>data-kind="seat"</c> elements or are marked by hand. A seat that disappears from a re-imported drawing is only
/// deactivated, so what is assigned to it is not lost.
/// </summary>
public class Workspot
{
    public int Id { get; set; }

    public int FloorId { get; set; }
    public Floor? Floor { get; set; }

    /// <summary>The element's <c>id</c> in <see cref="Floor.Svg"/>; stable across re-imports of the same drawing.</summary>
    public string ElementId { get; set; } = "";

    /// <summary>What people search for and see; defaults to the drawing's seat id and can be renamed.</summary>
    public string Label { get; set; } = "";

    public string? RoomId { get; set; }
    public string? RoomName { get; set; }
    public string? RoomType { get; set; }
    public string? TableId { get; set; }

    public int? TeamId { get; set; }
    public FloorTeam? Team { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A cluster of workspots on one floor, drawn in a colour with its name. Display only: it has no meaning outside the
/// map (no link to the directory or to loans) and can be renamed at any time.
/// </summary>
public class FloorTeam
{
    public int Id { get; set; }

    public int FloorId { get; set; }
    public Floor? Floor { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Index into the map's team palette (<c>Workplace.TeamService.Colors</c>).</summary>
    public int ColorIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
