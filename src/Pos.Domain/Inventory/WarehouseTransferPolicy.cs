namespace Pos.Domain.Inventory;

/// <summary>Defines quantity effects for the dispatch/receipt lifecycle of an inventory transfer.</summary>
public static class WarehouseTransferPolicy
{
    public static WarehouseTransferEffect Dispatch(string status, decimal availableAtSource, decimal quantity)
    {
        if (!string.Equals(status, "Draft", StringComparison.Ordinal) || quantity <= 0m ||
            decimal.Round(quantity, 3, MidpointRounding.AwayFromZero) != quantity || availableAtSource < quantity)
            throw new ArgumentException("El traslado no se puede despachar con el estado, cantidad o existencia indicados.");
        return new WarehouseTransferEffect("Dispatched", -quantity, quantity, 0m);
    }

    public static WarehouseTransferEffect Receive(string status, decimal inTransitQuantity, decimal receivedQuantity)
    {
        if (!string.Equals(status, "Dispatched", StringComparison.Ordinal) || inTransitQuantity <= 0m ||
            decimal.Round(inTransitQuantity, 3, MidpointRounding.AwayFromZero) != inTransitQuantity ||
            receivedQuantity < 0m || receivedQuantity > inTransitQuantity ||
            decimal.Round(receivedQuantity, 3, MidpointRounding.AwayFromZero) != receivedQuantity)
            throw new ArgumentException("La cantidad recibida debe estar entre cero y la cantidad despachada.");
        return new WarehouseTransferEffect("Received", 0m, -inTransitQuantity, receivedQuantity, receivedQuantity - inTransitQuantity);
    }
}

public sealed record WarehouseTransferEffect(string Status, decimal SourceDelta, decimal InTransitDelta, decimal DestinationDelta, decimal Variance = 0m);
