namespace Pos.Domain.Inventory;

/// <summary>Cost policy for a receipt in the product's catalog currency.</summary>
public static class InventoryCostPolicy
{
    public static decimal InitializeMissingCost(decimal? currentUnitCost, decimal initialUnitCost)
    {
        if (currentUnitCost.HasValue)
            throw new InvalidOperationException("inventory_cost_already_initialized");
        if (initialUnitCost < 0m || decimal.Round(initialUnitCost, 4) != initialUnitCost)
            throw new ArgumentOutOfRangeException(nameof(initialUnitCost), "El costo inicial debe ser no negativo y tener hasta cuatro decimales.");
        return initialUnitCost;
    }

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
