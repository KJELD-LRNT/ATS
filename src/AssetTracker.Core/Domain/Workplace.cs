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
    public List<FloorZone> Zones { get; set; } = [];
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

    /// <summary>
    /// The drawing's seat id (or the element id of a marked shape), unique per floor. Internal only: people know a desk by
    /// who sits there, its desktop, its zone and its room (<see cref="Place"/>), never by this id.
    /// </summary>
    public string Label { get; set; } = "";

    public string? RoomId { get; set; }
    public string? RoomName { get; set; }
    public string? RoomType { get; set; }
    public string? TableId { get; set; }

    public int? ZoneId { get; set; }
    public FloorZone? Zone { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// A desk that people are given (and that counts as free while nobody sits there). Seats in canteens and meeting rooms
    /// are not desks by default; editors can change that per seat.
    /// </summary>
    public bool IsDesk { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    private static readonly HashSet<string> NonDeskRoomTypes = new(StringComparer.OrdinalIgnoreCase)
        { "canteen", "meeting", "support", "core", "workshop", "kitchen", "lounge" };

    /// <summary>Whether a seat in a room of this type is a desk by default: offices, unknown types and seats outside a room.</summary>
    public static bool IsDeskRoomType(string? roomType) => string.IsNullOrEmpty(roomType) || !NonDeskRoomTypes.Contains(roomType);

    /// <summary>Where a seat is, the way people say it: its zone and its room ("Finance", "Sales · Open space"); empty when neither is known.</summary>
    public string Place(string? zoneName) => Place(zoneName, RoomName ?? RoomId);

    public static string Place(string? zoneName, string? room) =>
        string.Join(" · ", new[] { zoneName, room }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// A cluster of workspots on one floor, drawn in a colour with its name. Display only: it has no meaning outside the
/// map (no link to the directory or to loans) and can be renamed at any time.
/// </summary>
public class FloorZone
{
    public int Id { get; set; }

    public int FloorId { get; set; }
    public Floor? Floor { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Index into the map's zone palette (<c>Workplace.ZoneService.Colors</c>).</summary>
    public int ColorIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What a <see cref="WorkspotAssignment"/> places at a seat.</summary>
public enum PlacementKind
{
    Person = 1,
    Desktop = 2,
    Screen = 3,
}

/// <summary>
/// Something placed at a workspot: a person (who sits there) or a tagged asset whose type is a desktop or a screen. It
/// records <b>where</b> things are, for planning; it is separate from loans (who is responsible). At most one open row
/// per person and per asset (a thing is in one place), one person and one desktop per workspot. Releasing sets
/// <see cref="ReleasedAt"/>: the row stays as history.
/// </summary>
public class WorkspotAssignment
{
    public int Id { get; set; }

    public int WorkspotId { get; set; }
    public Workspot? Workspot { get; set; }

    public int? PersonId { get; set; }
    public Person? Person { get; set; }

    public int? AssetId { get; set; }
    public IndividualAsset? Asset { get; set; }

    /// <summary>What was placed: a person, or an asset as desktop or screen (from its type when it was placed).</summary>
    public PlacementKind Kind { get; set; }

    public DateTimeOffset AssignedAt { get; set; }
    public string AssignedBy { get; set; } = "";

    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleasedBy { get; set; }
}
