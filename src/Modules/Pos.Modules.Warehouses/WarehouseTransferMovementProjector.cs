namespace Pos.Modules.Warehouses;

/// <summary>Maps immutable transfer events to the quantity that actually left or entered a warehouse.</summary>
public static class WarehouseTransferMovementProjector
{
    public static decimal? GetQuantityDelta(string status, string action, decimal sentQuantity, decimal? receivedQuantity)
    {
        if (action == "transfer.dispatched" && status is "Dispatched" or "Received" && sentQuantity > 0m)
            return -sentQuantity;
        if (action == "transfer.received" && status == "Received" && receivedQuantity is > 0m)
            return receivedQuantity.Value;
        return null;
    }
}
