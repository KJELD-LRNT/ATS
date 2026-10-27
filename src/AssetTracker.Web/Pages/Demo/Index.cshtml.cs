using AssetTracker.Core.Data;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using AssetTracker.Infrastructure.Demo;
using AssetTracker.Web.Demo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Web.Pages.Demo;

/// <summary>
/// Demo control panel (demo mode only; the page has no route otherwise): role switcher, "act as person",
/// time travel and data reset. Open to every demo identity, including one without roles.
/// </summary>
public class IndexModel(
    DemoOptions options,
    DemoIdentityCookie cookie,
    DemoDataManager data,
    DemoClock clock,
    DemoMailbox mailbox,
    AppDbContext db,
    SiteCalendar calendar) : PageModel
{
    public sealed record PersonChoice(string Sid, string Label);

    public sealed record SiteTime(string Key, string Name, string TimeZone, DateTimeOffset LocalNow);

    public DemoIdentity Current { get; private set; } = null!;
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
        var grants = await db.ManagerSiteGrants.AsNoTracking()
            .Join(db.Sites, g => g.SiteId, s => s.Id, (g, s) => new { g.PrincipalDirectoryId, s.Key })
            .ToListAsync(ct);
        var facility = await db.RoleAssignments.AsNoTracking().Where(r => r.Role == AppRoles.Facility)
            .Select(r => r.PrincipalDirectoryId).ToListAsync(ct);
        var people = await db.People.AsNoTracking().Where(p => p.Sid != null && p.IsActive)
            .Select(p => new { p.Sid, p.Username, p.DisplayName, p.DirectoryId, SiteKey = p.Site != null ? p.Site.Key : null })
            .ToListAsync(ct);
        People = people
            .OrderBy(p => p.Username == DemoDefaults.YouUsername ? 0 : p.Username == DemoDefaults.ManagerUsername ? 1
                : facility.Contains(p.DirectoryId) ? 2 : 3)
            .ThenBy(p => p.DisplayName)
            .Select(p =>
            {
                var g = grants.Where(x => x.PrincipalDirectoryId == p.DirectoryId).Select(x => x.Key).ToList();
                var label = $"{p.DisplayName} ({DemoDefaults.Domain}\\{p.Username}, site {p.SiteKey ?? "none"})" +
                            (g.Count > 0 ? $" — manager grant: {string.Join(", ", g)}" : "") +
                            (facility.Contains(p.DirectoryId) ? " — Facility role (given in the app)" : "");
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

    public async Task<IActionResult> OnPostIdentityAsync(bool it, bool manager, bool admin, string? personSid,
        CancellationToken ct)
    {
        var person = string.IsNullOrEmpty(personSid)
            ? null
            : await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Sid == personSid, ct);
        var roles = (it ? AppRoles.It : AppRoles.None) | (manager ? AppRoles.Manager : AppRoles.None) |
                    (admin ? AppRoles.Admin : AppRoles.None);
        var identity = person is null
            ? cookie.Default with { Roles = roles }
            : new DemoIdentity($"{DemoDefaults.Domain}\\{person.Username}", person.Sid!, roles);
        cookie.Write(HttpContext, identity);
        Message = $"You are now {identity.UserName} with roles: {(roles == AppRoles.None ? "none" : roles.ToString())}.";
        return RedirectToPage();
    }

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
        Message = "Demo data was deleted and seeded again; the mailbox was emptied and the clock reset.";
        return RedirectToPage();
    }
}
