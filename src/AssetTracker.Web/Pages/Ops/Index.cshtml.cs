using AssetTracker.Core.Ops;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Ops;

/// <summary>IT operations dashboard: directory sync, mail outbox, database, worker heartbeat and app version.</summary>
[Authorize(Policy = Policies.ItOnly)]
public class IndexModel(OpsStatusService ops) : PageModel
{
    public static readonly string WebVersion = AppVersion.Of(typeof(IndexModel).Assembly);

    public OpsDashboard Dashboard { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct) => Dashboard = await ops.GetDashboardAsync(WebVersion, ct);

    public static string Age(TimeSpan? age) => age switch
    {
        null => "n/a",
        { TotalMinutes: < 1 } => "under a minute",
        { TotalHours: < 2 } a => $"{(int)a.TotalMinutes} min",
        { TotalDays: < 2 } a => $"{a.TotalHours:0.#} h",
        { } a => $"{a.TotalDays:0.#} days",
    };

    public static string Utc(DateTimeOffset? t) => t is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " UTC" : "never";
}
