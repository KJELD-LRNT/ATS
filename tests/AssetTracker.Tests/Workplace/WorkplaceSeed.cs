using System.Text;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

/// <summary>Floor plan fixtures. Fictional data only.</summary>
public static class WorkplaceSeed
{
    /// <summary>
    /// A drawing in the format of the facility team's renderer: one room with one table and <paramref name="seats"/>
    /// seats, plus a style element, a style attribute and a pattern fill (all of which the importer rewrites).
    /// </summary>
    public static string Annotated(int seats = 4, string room = "R1", string roomName = "Room One", string type = "office")
    {
        var sb = new StringBuilder();
        sb.Append("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 200" class="floorplan">""");
        sb.Append("<style>.bg{fill:#f7f6f2}.seat{fill:#2b6cb0}.room.office{fill:#fdebcf;stroke:#cf9437}.nf{fill:url(#hatch)}</style>");
        sb.Append("""<defs><pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse"><rect width="6" height="6" fill="#ecebe7"/></pattern></defs>""");
        sb.Append("""<rect class="bg" width="400" height="200"/><rect class="nf" x="300" y="10" width="80" height="80"/>""");
        sb.Append($"""<g class="room {type}" id="f1-room-{room}" data-kind="room" data-id="{room}" data-type="{type}" data-name="{roomName}"><rect class="shape" x="10" y="10" width="280" height="180"/></g>""");
        sb.Append($"""<g class="table {type}" id="f1-table-{room}-T01" data-kind="table" data-id="{room}-T01" data-type="{type}" data-parent="{room}"><rect x="40" y="90" width="200" height="12"/>""");
        for (var i = 1; i <= seats; i++)
            sb.Append($"""<circle class="seat" id="f1-seat-{room}-T01-S{i:00}" data-kind="seat" data-id="{room}-T01-S{i:00}" cx="{50 + i * 20}" cy="84" r="3" style="stroke-width:.5px"/>""");
        sb.Append("</g>");
        sb.Append("""<text x="20" y="25" style="font-size:7px;pointer-events:none">Label</text></svg>""");
        return sb.ToString();
    }

    /// <summary>A drawing without seats declared (three plain desks), as a CAD export would be.</summary>
    public static string Plain() =>
        """<svg xmlns="http://www.w3.org/2000/svg" width="300" height="100"><rect x="1" y="1" width="298" height="98" fill="#fff"/><rect x="10" y="10" width="20" height="10"/><rect x="40" y="10" width="20" height="10"/><rect x="70" y="10" width="20" height="10"/></svg>""";

    /// <summary>A person with a Windows account who has the Facility role (given in the application).</summary>
    public static async Task FacilityAsync(AppFactory f, TestUser user)
    {
        await f.WithDbAsync(async db =>
        {
            var person = await db.People.FirstOrDefaultAsync(p => p.Sid == user.Sid)
                         ?? await Seed.PersonAsync(db, user.Name.Split('\\').Last(), sid: user.Sid);
            if (!await db.RoleAssignments.AnyAsync(r => r.Role == AppRoles.Facility && r.PrincipalDirectoryId == person.DirectoryId))
            {
                db.RoleAssignments.Add(new RoleAssignment
                {
                    Role = AppRoles.Facility, PrincipalDirectoryId = person.DirectoryId, PrincipalName = person.Username,
                    GrantedAt = DateTimeOffset.UtcNow, GrantedBy = "test",
                });
                await db.SaveChangesAsync();
            }
        });
    }

    public static Task<ImportResult> ImportAsync(AppFactory f, Site site, string name, string svg) =>
        f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<FloorService>().ImportAsync(site.Id, name, svg));
}
