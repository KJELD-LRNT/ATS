using System.ComponentModel.DataAnnotations;
using AssetTracker.Core.Common;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using AssetTracker.Web.Pages.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Workplace;

/// <summary>Import an SVG floor plan for a site. IT and facility only.</summary>
[Authorize(Policy = Policies.WorkplaceEdit)]
[RequestSizeLimit(FloorplanImporter.MaxBytes + 512 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = FloorplanImporter.MaxBytes + 512 * 1024)]
public class ImportModel(FloorService floors) : PageModel
{
    public const int MaxMegabytes = FloorplanImporter.MaxBytes / 1024 / 1024;

    public List<SelectListItem> Sites { get; private set; } = [];

    [BindProperty, Display(Name = "Site")] public int? SiteId { get; set; }
    [BindProperty, Display(Name = "Floor name")] public string? Name { get; set; }
    [BindProperty, Display(Name = "SVG drawing")] public IFormFile? Drawing { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            if (SiteId is null) throw new DomainException("floor.site", "Choose a site for this floor.");
            var xml = await ReadAsync(Drawing, ct);
            var result = await floors.ImportAsync(SiteId.Value, Name ?? "", xml, ct);
            TempData["Message"] = result.Floor.Workspots.Count > 0
                ? $"Imported '{result.Floor.Name}' with {result.Floor.Workspots.Count} workspots."
                : $"Imported '{result.Floor.Name}'. The drawing does not declare seats: click the shapes that are seats and use Mark as workspots.";
            return RedirectToPage("Floor", new { id = result.Floor.Id });
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    internal static async Task<string> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new DomainException("floor.file", "Choose an SVG file.");
        if (file.Length > FloorplanImporter.MaxBytes)
            throw new DomainException("floor.too-large", $"The drawing is larger than {MaxMegabytes} MB.");
        using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    private async Task LoadAsync(CancellationToken ct) =>
        Sites = (await floors.SitesAsync(ct)).Select(s => new SelectListItem($"{s.Key} – {s.Name}", s.Id.ToString())).ToList();
}
