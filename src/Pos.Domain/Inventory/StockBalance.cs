namespace Pos.Domain.Inventory;

/// <summary>In-memory domain balance used to enforce the default no-negative-stock policy.</summary>
public sealed class StockBalance
{
    public string Sku { get; }
    public decimal QuantityOnHand { get; private set; }

    public StockBalance(string sku, decimal initialQuantity = 0m)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("SKU is required.", nameof(sku));
        if (initialQuantity < 0m) throw new ArgumentOutOfRangeException(nameof(initialQuantity));
        Sku = sku.Trim();
        QuantityOnHand = initialQuantity;
    }

    public void Receive(decimal quantity)
    {
        EnsurePositive(quantity);
        QuantityOnHand += quantity;
    }

    public void Remove(decimal quantity)
    {
        EnsurePositive(quantity);
        if (quantity > QuantityOnHand)
            throw new DomainRuleException("Insufficient stock.");
        QuantityOnHand -= quantity;
    }

    private static void EnsurePositive(decimal quantity)
    {
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
    }
}
