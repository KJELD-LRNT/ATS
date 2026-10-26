using AssetTracker.Core.Directory;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.ViewComponents;

/// <summary>Warns IT users when the people snapshot is stale or the last directory sync failed/was rejected.</summary>
public class SyncStatusViewComponent(DirectorySyncService sync, ICurrentUser user, ILogger<SyncStatusViewComponent> logger)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!user.IsIt) return Content(string.Empty);
        DirectorySyncStatus status;
        try
        {
            status = await sync.GetStatusAsync(HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The layout also renders the error page; a database outage must not break it.
            logger.LogWarning(ex, "Could not read directory sync status.");
            return Content(string.Empty);
        }
        if (!status.IsStale && !status.LastRunUnsuccessful) return Content(string.Empty);
        return View(status);
    }
}
