using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Keys;

public partial class KeyAccessService
{
    public Task<Guid> SaveDeviceAsync(Guid? id, Guid buildingId, Guid? roomId, string name, string? manufacturer,
        string? model, string? externalId, string location, DigitalLockKind kind, LockOpeningMethod methods,
        bool active = true, CancellationToken ct = default) => Change(async () =>
    {
        var building = await Building(buildingId, ct);
        externalId = OptionalText(externalId);
        manufacturer = OptionalText(manufacturer, 100);
        model = OptionalText(model, 100);
        Require(Enum.IsDefined(kind) && methods != LockOpeningMethod.None && (methods & ~(LockOpeningMethod.Pin | LockOpeningMethod.BluetoothApp)) == 0,
            "Select a device type and at least one supported opening method.");
        Require(kind != DigitalLockKind.Door || roomId is not null, "A door lock needs a room.");
        if (roomId is not null) Require(await db.AccessRooms.AnyAsync(x => x.Id == roomId && x.BuildingId == buildingId && x.IsActive, ct), "Select an active room in the same building.");
        var item = id is null ? new DigitalLock { BuildingId = buildingId } : await db.DigitalLocks.RequiredAsync(x => x.Id == id, ct);
        Require(item.BuildingId == buildingId, "A device cannot move between buildings.");
        if (id is null) db.DigitalLocks.Add(item);
        if (id is not null && (item.Kind != kind || item.RoomId != roomId || item.Methods != methods || item.ExternalId != externalId.Trim()
            || item.Manufacturer != manufacturer.Trim() || item.Model != model.Trim() || !active))
        {
            Require(!await db.DigitalPermissions.AnyAsync(x => x.DeviceId == item.Id && x.State != DigitalPermissionState.Revoked, ct), "Verify all digital revocations before changing device access or identity.");
            Require(!await db.KeyCopies.AnyAsync(x => x.LockboxId == item.Id, ct), "Move stored key copies before changing device access or archiving it.");
        }
        item.Name = Text(name); item.Manufacturer = manufacturer; item.Model = model;
        item.ExternalId = externalId; item.Location = Text(location); item.RoomId = roomId;
        item.Kind = kind; item.Methods = methods; item.IsActive = active;
        audit.Record("keys.device.save", nameof(DigitalLock), item.Id, building.SiteId,
            details: new { item.Name, item.Manufacturer, item.Model, item.ExternalId, roomId, kind, methods, active });
        return item.Id;
    }, ct);

    private async Task<DigitalPermission> NewPermission(Guid deviceId, int personId, DateTimeOffset start, DateTimeOffset? end,
        LockOpeningMethod method, string reason, CancellationToken ct, bool recordExisting = false, string? reference = null)
    {
        var device = await db.DigitalLocks.RequiredAsync(x => x.Id == deviceId, ct);
        await Building(device.BuildingId, ct);
        Require(device.IsActive && method is LockOpeningMethod.Pin or LockOpeningMethod.BluetoothApp && device.Methods.HasFlag(method), "This opening method is not supported by the active device.");
        Require(start != default && (end is null || end > start && end > clock.UtcNow), "An expiry, if entered, must be after the start and after now.");
        var person = recordExisting
            ? await db.People.RequiredAsync(x => x.Id == personId && !x.IsAnonymized, ct)
            : await Borrower(personId, ct);
        var permission = new DigitalPermission
        {
            DeviceId = deviceId, PersonId = personId, StartsAt = start.ToUniversalTime(), EndsAt = end?.ToUniversalTime(),
            Method = method, State = recordExisting ? DigitalPermissionState.Confirmed : DigitalPermissionState.Pending,
            ConfirmedAt = recordExisting ? clock.UtcNow : null,
            VerificationReference = string.IsNullOrWhiteSpace(reference) ? null : Text(reference),
            IssuedBy = user.Name, Reason = Text(reason, 500),
        };
        db.DigitalPermissions.Add(permission);
        audit.Record(recordExisting ? "keys.permission.record" : "keys.permission.request", nameof(DigitalPermission), permission.Id, null,
            details: new { deviceId, personId, permission.StartsAt, permission.EndsAt, method, permission.VerificationReference });
        QueueNotice(person, $"permission:{permission.Id}:created", recordExisting ? "Digital access recorded" : "Digital access planned",
            $"Facility {(recordExisting ? "recorded" : "planned")} your {method} access from {permission.StartsAt:u}. Expiry: {permission.EndsAt?.ToString("u") ?? "ongoing"}. This is an access record; changes to the lock are handled separately.");
        return permission;
    }

    /// <summary>Records access already arranged outside ATS. No provisioning or provider connection.</summary>
    public Task<Guid> RecordAccessAsync(Guid deviceId, int personId, DateTimeOffset? start, DateTimeOffset? end,
        LockOpeningMethod method, string reason, string? reference = null, CancellationToken ct = default) => Change(async () =>
    {
        var p = await NewPermission(deviceId, personId, start ?? clock.UtcNow, end, method, reason, ct, true, reference);
        return p.Id;
    }, ct);

    private static string OptionalText(string? value, int max = 200) => string.IsNullOrWhiteSpace(value) ? "" : Text(value, max);

    public Task<Guid> GrantAsync(Guid deviceId, int personId, DateTimeOffset start, DateTimeOffset end,
        LockOpeningMethod method, string reason, CancellationToken ct = default) => Change(async () =>
    {
        var p = await NewPermission(deviceId, personId, start, end, method, reason, ct);
        return p.Id;
    }, ct);

    public Task<bool> PermissionActionAsync(Guid id, string action, string? reference, CancellationToken ct = default) => Change(async () =>
    {
        var p = await db.DigitalPermissions.Include(x => x.Person).Include(x => x.Device).RequiredAsync(x => x.Id == id, ct);
        Require(p.State != DigitalPermissionState.Revoked, "Revoked permissions cannot be reopened. Create a new grant.");
        var before = p.State;
        switch (action)
        {
            case "confirm":
                Require(p.State is DigitalPermissionState.Pending or DigitalPermissionState.Failed, "Only pending or failed provisioning can be confirmed.");
                Require((p.EndsAt is null || p.EndsAt > clock.UtcNow) && p.Device!.IsActive, "This access period has expired or the device is inactive.");
                await Borrower(p.PersonId, ct);
                p.State = DigitalPermissionState.Confirmed; p.ConfirmedAt = clock.UtcNow;
                p.VerificationReference = string.IsNullOrWhiteSpace(reference) ? null : Text(reference);
                break;
            case "request-revoke": p.State = DigitalPermissionState.RevocationPending; break;
            case "verify-revoke":
                Require(p.State == DigitalPermissionState.RevocationPending, "Request revocation before verifying it.");
                goto case "remove";
            case "remove":
                p.State = DigitalPermissionState.Revoked; p.RevokedAt = clock.UtcNow;
                p.VerificationReference = string.IsNullOrWhiteSpace(reference) ? null : Text(reference);
                break;
            case "fail":
                // A failed revoke must remain visibly unresolved, never return to a provisioning state.
                if (p.State != DigitalPermissionState.RevocationPending) p.State = DigitalPermissionState.Failed;
                p.VerificationReference = Text(reference);
                break;
            default: Require(false, "Unknown permission action."); break;
        }
        audit.Record($"keys.permission.{action}", nameof(DigitalPermission), id, null,
            details: new { p.PersonId, p.DeviceId, before, after = p.State, p.VerificationReference });
        QueueNotice(p.Person!, $"permission:{id}:{action}:{Guid.NewGuid():N}", "Digital access update",
            $"Digital access record: {p.State}. Starts: {p.StartsAt:u}. Expiry: {p.EndsAt?.ToString("u") ?? "ongoing"}. ATS records access; changes to the lock are handled separately.");
        return true;
    }, ct);

    private void QueueNotice(Person person, string key, string subject, string body)
    {
        var email = person.IsActive && !person.IsAnonymized ? EmailAddresses.Normalize(person.Email) : null;
        db.Outbox.Add(new NotificationOutbox
        {
            Kind = NotificationKind.KeyAccess, DedupeKey = $"key-access:{person.Id}:{key}",
            RecipientEmail = email, Subject = subject, Body = body, CreatedAt = clock.UtcNow, NextAttemptAt = clock.UtcNow,
            Status = email is null ? NotificationStatus.Skipped : NotificationStatus.Pending,
            SkipReason = email is null ? "Inactive person or invalid email" : null,
        });
    }
}
