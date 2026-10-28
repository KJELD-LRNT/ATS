using AssetTracker.Core.Data;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using AssetTracker.Infrastructure.Demo;
using AssetTracker.Web.Demo;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Web.Pages.Demo;

/// <summary>
/// Demo control panel (demo mode only; the page has no route otherwise): "act as person", the role switcher (its ticks
/// replace the current demo person's roles in <c>RoleAssignments</c>), time travel and data reset. Open to every demo
/// identity, including one without roles.
/// </summary>
public class IndexModel(
    DemoOptions options,
    DemoIdentityCookie cookie,
    DemoDataManager data,
    DemoClock clock,
    DemoMailbox mailbox,
    DemoRoleSwitcher switcher,
    AppDbContext db,
    SiteCalendar calendar) : PageModel
{
    public sealed record PersonChoice(string Sid, string Label);

    public sealed record SiteTime(string Key, string Name, string TimeZone, DateTimeOffset LocalNow);

    public DemoIdentity Current { get; private set; } = null!;

    /// <summary>The current demo person's roles (as given in <c>RoleAssignments</c>).</summary>
    public AppRoles CurrentRoles { get; private set; }
    public IReadOnlyList<PersonChoice> People { get; private set; } = [];
    public IReadOnlyList<SiteTime> SiteTimes { get; private set; } = [];
    public DateTimeOffset DemoUtcNow { get; private set; }
    public TimeSpan Offset { get; private set; }
    public string DataDirectory => options.DataDirectory;
    public int MailCount { get; private set; }

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Current = cookie.Read(HttpContext);
        CurrentRoles = await switcher.RolesOfAsync(Current.Sid, ct);
        var grants = await db.ManagerSiteGrants.AsNoTracking()
            .Join(db.Sites, g => g.SiteId, s => s.Id, (g, s) => new { g.PrincipalDirectoryId, s.Key })
            .ToListAsync(ct);
        var roles = await db.RoleAssignments.AsNoTracking().Select(r => new { r.PrincipalDirectoryId, r.Role }).ToListAsync(ct);
        var people = await db.People.AsNoTracking().Where(p => p.Sid != null && p.IsActive)
            .Select(p => new { p.Sid, p.Username, p.DisplayName, p.DirectoryId, SiteKey = p.Site != null ? p.Site.Key : null })
            .ToListAsync(ct);
        People = people
            .Select(p => new
            {
                Person = p,
                Roles = roles.Where(r => r.PrincipalDirectoryId == p.DirectoryId).Aggregate(AppRoles.None, (a, r) => a | r.Role),
                Grants = grants.Where(x => x.PrincipalDirectoryId == p.DirectoryId).Select(x => x.Key).Order().ToList(),
            })
            .OrderBy(x => x.Person.Username == DemoDefaults.YouUsername ? 0 : x.Person.Username == DemoDefaults.ManagerUsername ? 1
                : x.Roles != AppRoles.None ? 2 : 3)
            .ThenBy(x => x.Person.DisplayName)
            .Select(x =>
            {
                var p = x.Person;
                var label = $"{p.DisplayName} ({DemoDefaults.Domain}\\{p.Username}, site {p.SiteKey ?? "none"})" +
                            (x.Roles != AppRoles.None ? $" — roles: {RoleNames(x.Roles)}" : "") +
                            (x.Grants.Count > 0 ? $" — manager sites: {string.Join(", ", x.Grants)}" : "");
                return new PersonChoice(p.Sid!, label);
            })
            .ToList();

        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        SiteTimes = (await calendar.AllSitesAsync(ct))
            .Select(d => new SiteTime(sites[d.SiteId].Key, sites[d.SiteId].Name, d.TimeZone.Id, d.LocalNow))
            .OrderBy(s => s.Key).ToList();
        DemoUtcNow = clock.UtcNow;
        Offset = clock.Offset;
        MailCount = mailbox.Count();
    }

    /// <summary>Act as another demo person (with the roles they have).</summary>
    public async Task<IActionResult> OnPostIdentityAsync(string? personSid, CancellationToken ct)
    {
        var person = string.IsNullOrEmpty(personSid)
            ? null
            : await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Sid == personSid && p.IsActive, ct);
        var identity = person is null
            ? cookie.Default
            : new DemoIdentity($"{DemoDefaults.Domain}\\{person.Username}", person.Sid!);
        cookie.Write(HttpContext, identity);
        var roles = await switcher.RolesOfAsync(identity.Sid, ct);
        Message = $"You are now {identity.UserName} with roles: {RoleNames(roles)}.";
        return RedirectToPage();
    }

    /// <summary>The ticks replace the current demo person's roles.</summary>
    public async Task<IActionResult> OnPostRolesAsync(bool it, bool manager, bool admin, bool facility, CancellationToken ct)
    {
        var identity = cookie.Read(HttpContext);
        var roles = (it ? AppRoles.It : AppRoles.None) | (manager ? AppRoles.Manager : AppRoles.None) |
                    (admin ? AppRoles.Admin : AppRoles.None) | (facility ? AppRoles.Facility : AppRoles.None);
        if (!await db.People.AnyAsync(p => p.Sid == identity.Sid && p.IsActive, ct))
        {
            Message = $"{identity.UserName} is not an active person in the demo directory, so they can't get roles.";
            return RedirectToPage();
        }
        await switcher.ReplaceAsync(identity.Sid, roles, ct);
        Message = $"{identity.UserName} now has roles: {RoleNames(roles)}.";
        return RedirectToPage();
    }

    public static string RoleNames(AppRoles roles) => roles == AppRoles.None ? "none" : Ui.RolesLabel(roles).Replace(" · ", ", ");

    public async Task<IActionResult> OnPostTravelAsync(int days, CancellationToken ct)
    {
        if (days is not (1 or 7)) return BadRequest();
        await data.TravelAsync(TimeSpan.FromDays(days), ct);
        Message = $"Moved the demo clock forward by {days} day(s). Overdue reminders for sites past 08:00 local time are queued now and appear in the demo mailbox within about 10 seconds.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTravelResetAsync(CancellationToken ct)
    {
        await data.TravelAsync(null, ct);
        Message = "The demo clock is back at the real time.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        await data.ResetAsync(ct);
        Message = "Demo data was deleted and seeded again (the demo user has IT and Admin again); the mailbox was emptied and the clock reset.";
        return RedirectToPage();
    }
}
