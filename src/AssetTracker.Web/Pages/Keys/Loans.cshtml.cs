using AssetTracker.Core.Domain;
using AssetTracker.Core.Keys;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Keys;

[Authorize(Policy = Policies.FacilityOnly)]
public class LoansModel(KeyAccessService keys) : KeyPage(keys)
{
    [BindProperty(SupportsGet = true)] public Guid? CopyId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? RoomId { get; set; }
    public IReadOnlyList<Person> People { get; private set; } = [];
    public override async Task OnGetAsync(CancellationToken ct)
    {
        await base.OnGetAsync(ct);
        People = await Keys.SearchPeopleAsync(Q, ct);
    }
    public Task<IActionResult> OnPostLendAsync(Guid copyId, int personId, DateOnly? dueDate, string reason, bool reserve,
        DateTime? collectionStart, DateTime? collectionEnd, LockOpeningMethod method, long revision, CancellationToken ct) =>
        Save(async () => { await Keys.LendAsync(copyId, personId, dueDate, reason, reserve,
            collectionStart is {} start ? new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)) : null,
            collectionEnd is {} end ? new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)) : null, method, revision, ct); });
    public Task<IActionResult> OnPostActionAsync(Guid id, string action, string? note, DateOnly? dueDate, CancellationToken ct) =>
        Save(async () => { await Keys.LoanActionAsync(id, action, note, dueDate, ct); });
}
