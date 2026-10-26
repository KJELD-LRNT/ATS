using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages;

public class IndexModel(ICurrentUser user) : PageModel
{
    public string UserName => user.Name;
    public AppRoles Roles => user.Roles;

    public void OnGet() { }
}
