using System.ComponentModel.DataAnnotations;
using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

[Authorize(Policy = Policies.AdminOnly)]
public class SitesModel(AdminService admin, SiteCalendar calendar) : PageModel
{
    public IReadOnlyList<Site> Sites { get; private set; } = [];

    /// <summary>Suggestions for the timezone field (free text is allowed: any IANA or Windows id).</summary>
    public static IReadOnlyList<string> TimeZoneIds { get; } =
        TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Order(StringComparer.Ordinal).ToList();

    /// <summary>The default timezone id shown for sites without their own, or a hint when it is not configured.</summary>
    public string DefaultTimeZoneLabel
    {
        get
        {
            try { return calendar.DefaultTimeZone.Id; }
            catch (InvalidOperationException) { return "not configured (Clock:TimeZoneId)"; }
        }
    }

    /// <summary>Site being edited (from ?editId=), or null for the create form.</summary>
    public Site? Editing { get; private set; }

    [BindProperty]
    public SiteInput Input { get; set; } = new();

    [TempData]
    public string? Message { get; set; }

    public class SiteInput
    {
        [StringLength(32)]
        public string? Key { get; set; }

        [Required, StringLength(200)]
        public string Name { get; set; } = "";

        [StringLength(200), Display(Name = "AD site value")]
        public string? DirectoryValue { get; set; }

        [Display(Name = "Active")]
        public bool IsActive { get; set; } = true;

        [StringLength(100), Display(Name = "Timezone")]
        public string? TimeZoneId { get; set; }
    }

    public async Task OnGetAsync(int? editId, CancellationToken ct)
    {
        if (editId is { } id)
        {
            Editing = await admin.GetSiteAsync(id, ct);
            Input = new SiteInput
            {
                Key = Editing.Key, Name = Editing.Name, DirectoryValue = Editing.DirectoryValue, IsActive = Editing.IsActive,
                TimeZoneId = Editing.TimeZoneId,
            };
        }
        Sites = await admin.ListSitesAsync(ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var site = await admin.CreateSiteAsync(Input.Key ?? "", Input.Name, Input.DirectoryValue, Input.IsActive,
                    Input.TimeZoneId, ct);
                Message = $"Site {site.Key} created.";
                return RedirectToPage();
            }
            catch (DomainException ex) { this.Show(ex); }
        }
        else Response.StatusCode = StatusCodes.Status400BadRequest;
        Sites = await admin.ListSitesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int id, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var site = await admin.UpdateSiteAsync(id, Input.Name, Input.DirectoryValue, Input.IsActive, Input.TimeZoneId, ct);
                Message = $"Site {site.Key} updated.";
                return RedirectToPage();
            }
            catch (DomainException ex) { this.Show(ex); }
        }
        else Response.StatusCode = StatusCodes.Status400BadRequest;
        Editing = await admin.GetSiteAsync(id, ct);
        Sites = await admin.ListSitesAsync(ct);
        return Page();
    }
}
