using AssetTracker.Core.Domain;
using AssetTracker.Core.Keys;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Keys;

[Authorize(Policy = Policies.FacilityOnly)]
public class DevicesModel(KeyAccessService keys) : KeyPage(keys)
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public IReadOnlyList<Person> People { get; private set; } = [];
    protected override IActionResult AfterSave(string? page) => RedirectToPage(new { id = Id, q = Q });
    public override async Task OnGetAsync(CancellationToken ct)
    {
        await base.OnGetAsync(ct);
        People = await Keys.SearchPeopleAsync(Q, ct);
    }
    public Task<IActionResult> OnPostDeviceAsync(Guid? deviceId, Guid buildingId, Guid? roomId, string name,
        string? manufacturer, string? model, string? externalId, string location, DigitalLockKind kind,
        bool pin, bool bluetooth, bool active, CancellationToken ct) => Save(async () =>
        {
            Id = await Keys.SaveDeviceAsync(deviceId, buildingId, roomId, name, manufacturer, model, externalId, location, kind,
                (pin ? LockOpeningMethod.Pin : LockOpeningMethod.None) | (bluetooth ? LockOpeningMethod.BluetoothApp : LockOpeningMethod.None), active, ct);
        });
    public Task<IActionResult> OnPostRecordAsync(Guid deviceId, int personId, DateTime? start, DateTime? end,
        LockOpeningMethod method, string reason, string? reference, CancellationToken ct) => Save(async () =>
        {
            Id = deviceId;
            await Keys.RecordAccessAsync(deviceId, personId,
                start is {} from ? new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)) : null,
                end is {} until ? new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc)) : null,
                method, reason, reference, ct);
        });
    public Task<IActionResult> OnPostPermissionAsync(Guid permissionId, string action, string? reference, CancellationToken ct) =>
        Save(async () => { await Keys.PermissionActionAsync(permissionId, action, reference, ct); });
}
