using AssetTracker.Core.Domain;
using AssetTracker.Core.Keys;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Keys;

[Authorize(Policy = Policies.FacilityOnly)]
public class IndexModel(KeyAccessService keys) : KeyPage(keys)
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? RoomId { get; set; }
    [BindProperty(SupportsGet = true)] public int? PersonId { get; set; }
    public IReadOnlyList<Person> People { get; private set; } = [];
    public override async Task OnGetAsync(CancellationToken ct)
    {
        await base.OnGetAsync(ct);
        People = await Keys.SearchPeopleAsync(Q, ct);
    }
}
