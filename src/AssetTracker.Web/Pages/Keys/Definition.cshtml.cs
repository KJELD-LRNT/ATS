using AssetTracker.Core.Domain;
using AssetTracker.Core.Keys;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Keys;

[Authorize(Policy = Policies.FacilityOnly)]
public class DefinitionModel(KeyAccessService keys) : KeyPage(keys)
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    public Task<IActionResult> OnPostRoomAsync(Guid roomId, bool enabled, long revision, CancellationToken ct) =>
        Save(async () => { await Keys.SetRoomAsync(Id, roomId, enabled, revision, ct); });
    public Task<IActionResult> OnPostMasterAsync(Guid subordinateId, bool enabled, long revision, CancellationToken ct) =>
        Save(async () => { await Keys.SetMasterAsync(Id, subordinateId, enabled, revision, ct); });
    public Task<IActionResult> OnPostCopyAsync(Guid? copyId, string tag, string location, Guid? lockboxId, KeyCopyState state, CancellationToken ct) =>
        Save(async () => { await Keys.SaveCopyAsync(copyId, Id, tag, location, lockboxId, state, ct); });
    public Task<IActionResult> OnPostEditAsync(Guid buildingId, string code, string name, bool active, CancellationToken ct) =>
        Save(async () => { await Keys.SaveDefinitionAsync(Id, buildingId, code, name, active, ct); });
}
