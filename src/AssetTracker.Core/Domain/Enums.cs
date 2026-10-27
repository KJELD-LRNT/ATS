namespace AssetTracker.Core.Domain;

public enum AssetState
{
    Available = 1,
    CheckedOut = 2,
    Unavailable = 3,
    Retired = 4,
}

public enum LoanEventType
{
    CheckedOut = 1,
    Returned = 2,
    PartiallyReturned = 3,
    Corrected = 4,
}

public enum StockEventType
{
    Receipt = 1,
    Loss = 2,
    CountAdjustment = 3,
    TransferOut = 4,
    TransferIn = 5,
}

public enum NotificationKind
{
    Checkout = 1,
    Return = 2,
    Correction = 3,
    OverdueReminder = 4,
    SystemAlert = 5,
}

public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,
    Failed = 3,
    Skipped = 4,
}

public enum SyncRunStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    /// <summary>Run completed but was rejected by safety checks (e.g. unexpectedly low count); snapshot not applied.</summary>
    Rejected = 4,
}
