using Pos.Domain.Finance;

namespace Pos.Domain.Sales;

/// <summary>Immutable commercial snapshot for a product line on a sale.</summary>
public sealed record SaleLine
{
    public string Sku { get; }
    public string Description { get; }
    public decimal Quantity { get; }
    public Money UnitPrice { get; }
    public decimal TaxRate { get; }
    public decimal DiscountPercent { get; }
    public Money SubtotalAfterDiscount { get; }
    public Money TaxAmount { get; }
    public Money Total { get; }

    private SaleLine(string sku, string description, decimal quantity, Money unitPrice, decimal taxRate,
        decimal discountPercent, Money subtotalAfterDiscount, Money taxAmount, Money total)
    {
        Sku = sku;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        TaxRate = taxRate;
        DiscountPercent = discountPercent;
        SubtotalAfterDiscount = subtotalAfterDiscount;
        TaxAmount = taxAmount;
        Total = total;
    }

    public static SaleLine Create(string sku, string description, decimal quantity, Money unitPrice,
        decimal taxRate, decimal discountPercent, DiscountPolicy discountPolicy,
        bool supervisorAuthorized, int scale)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("SKU is required.", nameof(sku));
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Description is required.", nameof(description));
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (unitPrice.Amount < 0m) throw new DomainRuleException("Unit price cannot be negative.");
        if (taxRate is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(taxRate));
        if (scale is < 0 or > 28) throw new ArgumentOutOfRangeException(nameof(scale));
        ArgumentNullException.ThrowIfNull(discountPolicy);
        discountPolicy.EnsureAllowed(discountPercent, supervisorAuthorized);

        var gross = unitPrice.Amount * quantity;
        var discounted = new Money(gross * (1m - discountPercent / 100m), unitPrice.Currency).Round(scale);
        var tax = new Money(discounted.Amount * taxRate, unitPrice.Currency).Round(scale);
        return new SaleLine(sku.Trim(), description.Trim(), quantity, unitPrice, taxRate, discountPercent,
            discounted, tax, discounted + tax);
    }
}
