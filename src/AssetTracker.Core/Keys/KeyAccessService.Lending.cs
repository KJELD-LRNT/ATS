using System.Text.Json;
using AssetTracker.Core.Time;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Keys;

public partial class KeyAccessService
{
    public Task<Guid> SaveCopyAsync(Guid? id, Guid definitionId, string tag, string location, Guid? lockboxId,
        KeyCopyState state = KeyCopyState.Available, CancellationToken ct = default) => Change(async () =>
    {
        var definition = await db.KeyDefinitions.RequiredAsync(x => x.Id == definitionId, ct);
        Require(definition.IsActive, "Select an active key definition.");
        var building = await Building(definition.BuildingId, ct);
        Require(state is KeyCopyState.Available or KeyCopyState.Unavailable or KeyCopyState.Retired, "Use the loan workflow to issue or mark a key lost.");
        if (lockboxId is not null)
            Require(await db.DigitalLocks.AnyAsync(x => x.Id == lockboxId && x.BuildingId == building.Id && x.Kind == DigitalLockKind.Lockbox && x.IsActive, ct), "Select an active lockbox in the same building.");
        var item = id is null ? new KeyCopy { KeyDefinitionId = definitionId } : await db.KeyCopies.RequiredAsync(x => x.Id == id, ct);
        Require(item.KeyDefinitionId == definitionId, "A physical copy cannot change its key definition.");
        Require(!await db.KeyLoans.AnyAsync(x => x.CopyId == item.Id && x.ClosedAt == null, ct), "Close the current reservation or loan before changing this copy.");
        if (id is null) db.KeyCopies.Add(item);
        item.Tag = Text(tag, 64).ToUpperInvariant(); item.StorageLocation = Text(location); item.LockboxId = lockboxId; item.State = state;
        audit.Record("keys.copy.save", nameof(KeyCopy), item.Id, building.SiteId, details: new { item.Tag, location, lockboxId, state });
        return item.Id;
    }, ct);

    public Task<Guid> LendAsync(Guid copyId, int personId, DateOnly? dueDate, string reason, bool reserve,
        DateTimeOffset? collectionStart = null, DateTimeOffset? collectionEnd = null,
        LockOpeningMethod method = LockOpeningMethod.Pin, long? expectedRevision = null, CancellationToken ct = default) => Change(async () =>
    {
        Require(expectedRevision is not null, "Review the rooms opened by this key before issuing it.");
        await CheckRevision(expectedRevision!.Value, ct);
        var copy = await db.KeyCopies.Include(x => x.Definition).RequiredAsync(x => x.Id == copyId, ct);
        var building = await Building(copy.Definition!.BuildingId, ct);
        Require(copy.State == KeyCopyState.Available && copy.Definition.IsActive, "This copy is unavailable.");
        Require(!await db.KeyLoans.AnyAsync(x => x.CopyId == copyId && x.ClosedAt == null, ct), "This copy is already reserved or lent.");
        var person = await Borrower(personId, ct);
        Require(dueDate is null || dueDate >= await calendar.TodayForAsync(building.SiteId, ct), "Due date cannot be in the past.");
        var rooms = await EffectiveRooms(copy.KeyDefinitionId, ct);
        Require(rooms.Count > 0, "Assign at least one room to this key before lending it.");
        var loan = new KeyLoan
        {
            CopyId = copyId, PersonId = personId, DueDate = dueDate, Reason = Text(reason, 500),
            CreatedAt = clock.UtcNow, CollectedAt = reserve ? null : clock.UtcNow,
            State = reserve ? KeyLoanState.Reserved : KeyLoanState.OnLoan, IssuedBy = user.Name,
            AccessSnapshotJson = JsonSerializer.Serialize(rooms),
        };
        if (reserve)
        {
            Require(copy.LockboxId is not null && collectionStart is not null && collectionEnd is not null,
                "A lockbox reservation needs a collection start and end in UTC.");
            Require(dueDate is null || dueDate >= SiteCalendar.LocalDate(collectionStart!.Value,
                await calendar.TimeZoneForAsync(building.SiteId, ct)), "The key cannot be due before its collection date.");
            var permission = await NewPermission(copy.LockboxId!.Value, personId, collectionStart!.Value, collectionEnd!.Value, method, loan.Reason, ct);
            loan.PermissionId = permission.Id;
        }
        else copy.State = KeyCopyState.Issued;
        db.KeyLoans.Add(loan);
        QueueNotice(person, $"loan:{loan.Id}:created", reserve ? "Key reserved" : "Key issued", $"Key {copy.Tag}. Due: {dueDate?.ToString("yyyy-MM-dd") ?? "long-term issue"}. No digital credential is included.");
        audit.Record(reserve ? "keys.reserve" : "keys.checkout", nameof(KeyLoan), loan.Id, building.SiteId,
            details: new { copyId, personId, dueDate, rooms, loan.PermissionId });
        return loan.Id;
    }, ct);

    public Task<bool> LoanActionAsync(Guid id, string action, string? note = null, DateOnly? dueDate = null, CancellationToken ct = default) => Change(async () =>
    {
        var loan = await db.KeyLoans.Include(x => x.Copy).ThenInclude(x => x!.Definition).ThenInclude(x => x!.Building)
            .Include(x => x.Person).Include(x => x.Permission).RequiredAsync(x => x.Id == id, ct);
        var copy = loan.Copy!; var site = copy.Definition!.Building!.SiteId;
        Require(loan.ClosedAt is null, "This loan is already closed.");
        switch (action)
        {
            case "collect":
                Require(loan.State == KeyLoanState.Reserved, "Only a reservation can be collected.");
                await Borrower(loan.PersonId, ct);
                var p = loan.Permission!;
                Require(p.State == DigitalPermissionState.Confirmed && p.StartsAt <= clock.UtcNow && p.EndsAt > clock.UtcNow,
                    "Collection requires verified digital access within its collection window.");
                loan.State = KeyLoanState.OnLoan; loan.CollectedAt = clock.UtcNow; copy.State = KeyCopyState.Issued;
                p.State = DigitalPermissionState.RevocationPending;
                break;
            case "report-return":
                Require(loan.State == KeyLoanState.OnLoan, "Only an issued key can be reported returned.");
                loan.State = KeyLoanState.ReturnReported; loan.ReturnReportedAt = clock.UtcNow;
                break;
            case "verify-return":
                Require(loan.State is KeyLoanState.OnLoan or KeyLoanState.ReturnReported, "Only a collected key can be returned.");
                loan.ReturnCondition = Text(note); loan.ReceivedBy = user.Name;
                loan.State = KeyLoanState.Returned; loan.ClosedAt = clock.UtcNow; copy.State = KeyCopyState.Available;
                RequestRevocation(loan.Permission);
                break;
            case "lost":
                Require(loan.State is KeyLoanState.OnLoan or KeyLoanState.ReturnReported, "Only a collected key can be marked lost.");
                Text(note); copy.State = KeyCopyState.Lost; RequestRevocation(loan.Permission);
                break;
            case "close-loss":
                Require(copy.State == KeyCopyState.Lost, "Mark the key lost first.");
                Text(note); loan.State = KeyLoanState.LostClosed; loan.ClosedAt = clock.UtcNow;
                break;
            case "cancel":
                Require(loan.State == KeyLoanState.Reserved, "Only a reservation can be cancelled.");
                loan.State = KeyLoanState.Cancelled; loan.ClosedAt = clock.UtcNow; RequestRevocation(loan.Permission);
                break;
            case "extend":
                Require(dueDate is not null && dueDate >= await calendar.TodayForAsync(site, ct), "Enter a current or future due date.");
                Text(note); loan.DueDate = dueDate;
                break;
            default: Require(false, "Unknown loan action."); break;
        }
        audit.Record($"keys.{action}", nameof(KeyLoan), loan.Id, site, details: new { loan.PersonId, loan.CopyId, loan.State, loan.DueDate, note });
        QueueNotice(loan.Person!, $"loan:{loan.Id}:{action}:{Guid.NewGuid():N}", $"Key {action}", $"Key {copy.Tag}: {action}. Due: {loan.DueDate?.ToString("yyyy-MM-dd") ?? "long-term issue"}.");
        return true;
    }, ct);

    private static void RequestRevocation(DigitalPermission? permission)
    {
        if (permission is not null && permission.State != DigitalPermissionState.Revoked)
            permission.State = DigitalPermissionState.RevocationPending;
    }
}
