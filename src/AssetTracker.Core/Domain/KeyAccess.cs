namespace AssetTracker.Core.Domain;

public class AccessBuilding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int SiteId { get; set; }
    public Site? Site { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class AccessRoom
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public AccessBuilding? Building { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? FloorId { get; set; }
    public Floor? Floor { get; set; }
    public string? DrawingRoomId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class KeyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public AccessBuilding? Building { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class KeyRoom
{
    public Guid KeyDefinitionId { get; set; }
    public Guid RoomId { get; set; }
}

/// <summary>Master opens everything the subordinate opens; a DAG, allowing overlapping masters.</summary>
public class KeyLink
{
    public Guid MasterId { get; set; }
    public Guid SubordinateId { get; set; }
}

public enum KeyCopyState { Available = 1, Issued = 2, Lost = 3, Unavailable = 4, Retired = 5 }
public class KeyCopy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid KeyDefinitionId { get; set; }
    public KeyDefinition? Definition { get; set; }
    public string Tag { get; set; } = "";
    public string StorageLocation { get; set; } = "";
    public Guid? LockboxId { get; set; }
    public DigitalLock? Lockbox { get; set; }
    public KeyCopyState State { get; set; } = KeyCopyState.Available;
}

public enum KeyLoanState { Reserved = 1, OnLoan = 2, ReturnReported = 3, Returned = 4, LostClosed = 5, Cancelled = 6 }
public class KeyLoan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CopyId { get; set; }
    public KeyCopy? Copy { get; set; }
    public int PersonId { get; set; }
    public Person? Person { get; set; }
    public Guid? PermissionId { get; set; }
    public DigitalPermission? Permission { get; set; }
    public KeyLoanState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CollectedAt { get; set; }
    public DateTimeOffset? ReturnReportedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateOnly? DueDate { get; set; }
    public string IssuedBy { get; set; } = "";
    public string? ReceivedBy { get; set; }
    public string Reason { get; set; } = "";
    public string? ReturnCondition { get; set; }
    public string AccessSnapshotJson { get; set; } = "[]";
}

public enum DigitalLockKind { Door = 1, Lockbox = 2 }
[Flags]
public enum LockOpeningMethod { None = 0, BluetoothApp = 1, Pin = 2 }
public class DigitalLock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public AccessBuilding? Building { get; set; }
    public Guid? RoomId { get; set; }
    public AccessRoom? Room { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Location { get; set; } = "";
    public DigitalLockKind Kind { get; set; }
    public LockOpeningMethod Methods { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum DigitalPermissionState { Pending = 1, Confirmed = 2, RevocationPending = 3, Revoked = 4, Failed = 5 }
public class DigitalPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DigitalLock? Device { get; set; }
    public int PersonId { get; set; }
    public Person? Person { get; set; }
    public LockOpeningMethod Method { get; set; }
    public DigitalPermissionState State { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string IssuedBy { get; set; } = "";
    public string Reason { get; set; } = "";
    // Operational reference only. PIN secrets and provider passwords never enter ATS.
    public string? VerificationReference { get; set; }
}

/// <summary>Write gate: serializes topology, inventory and permission changes in one DB transaction.</summary>
public class KeyAccessRevision
{
    public int Id { get; set; }
    public long Version { get; set; }
}
