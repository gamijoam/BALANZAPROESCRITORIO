namespace Pos.Domain.Inventory;

/// <summary>Cost policy for a receipt in the product's catalog currency.</summary>
public static class InventoryCostPolicy
{
    public static bool CanReverseLastReceipt(decimal currentQuantity, decimal quantityAfterReceipt, bool hasLaterMovement)
        => currentQuantity == quantityAfterReceipt && !hasLaterMovement;

    public static decimal? WeightedAverageUnitCost(decimal currentQuantity, decimal? currentUnitCost,
        decimal receivedQuantity, decimal receivedUnitCost)
    {
        if (currentQuantity < 0m || currentUnitCost < 0m || receivedQuantity <= 0m || receivedUnitCost < 0m)
            throw new ArgumentOutOfRangeException(nameof(receivedQuantity), "La cantidad y el costo de recepción deben ser válidos.");

        if (currentQuantity == 0m) return Round(receivedUnitCost);
        if (currentUnitCost is null) return null;
        return Round(((currentQuantity * currentUnitCost.Value) + (receivedQuantity * receivedUnitCost)) /
                     (currentQuantity + receivedQuantity));
    }

    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}
