using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using AssetTracker.Infrastructure.Demo;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.ViewComponents;

/// <summary>Model of the demo banner.</summary>
public sealed record DemoBannerModel(string UserName, AppRoles Roles, DateTimeOffset DemoNow, TimeSpan Offset,
    TimeZoneInfo Zone, int MailCount);

/// <summary>"DEMO MODE — fictional data" banner with the current identity, roles and demo date. Empty when demo mode is off.</summary>
public class DemoBannerViewComponent(IServiceProvider services, ICurrentUser user) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        if (services.GetService<DemoOptions>() is null) return Content(string.Empty);
        var clock = services.GetRequiredService<DemoClock>();
        var zone = SiteCalendar.TryFindTimeZone(DemoDefaults.TimeZoneId, out var z) ? z! : TimeZoneInfo.Utc;
        return View(new DemoBannerModel(user.Name, user.Roles, TimeZoneInfo.ConvertTime(clock.UtcNow, zone), clock.Offset,
            zone, services.GetRequiredService<DemoMailbox>().Count()));
    }
}
