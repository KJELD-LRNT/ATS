using AssetTracker.Core.Keys;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Keys;

[Authorize(Policy = Policies.FacilityOnly)]
public class RegisterModel(KeyAccessService keys) : KeyPage(keys)
{
    public Task<IActionResult> OnPostBuildingAsync(Guid? id, int siteId, string name, bool active, CancellationToken ct) =>
        Save(async () => { await Keys.SaveBuildingAsync(id, siteId, name, active, ct); });
    public Task<IActionResult> OnPostRoomAsync(Guid? id, Guid buildingId, string code, string name, int? floorId, string? drawingRoomId, bool active, CancellationToken ct) =>
        Save(async () => { await Keys.SaveRoomAsync(id, buildingId, code, name, floorId, drawingRoomId, active, ct); });
    public Task<IActionResult> OnPostDefinitionAsync(Guid? id, Guid buildingId, string code, string name, bool active, CancellationToken ct) =>
        Save(async () => { await Keys.SaveDefinitionAsync(id, buildingId, code, name, active, ct); });
}
