namespace AssetTracker.Core.Domain;

public class StockEvent
{
    public long Id { get; set; }

    public int PoolId { get; set; }
    public Pool? Pool { get; set; }

    public StockEventType Type { get; set; }

    /// <summary>Signed change to on-hand (and available) quantity.</summary>
    public int QuantityDelta { get; set; }

    public int AvailableAfter { get; set; }
    public int OutstandingAfter { get; set; }

    public string Reason { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>For transfers: the linked counterpart event at the other site.</summary>
    public long? CounterpartEventId { get; set; }
}
