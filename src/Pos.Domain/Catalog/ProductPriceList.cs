using Pos.Domain.Finance;

namespace Pos.Domain.Catalog;

public enum PriceTier
{
    Price1 = 1,
    Price2 = 2,
    Price3 = 3
}

/// <summary>Product price tiers in the product's catalog currency.</summary>
public sealed record ProductPriceList
{
    public Money Price1 { get; }
    public Money? Price2 { get; }
    public Money? Price3 { get; }

    private ProductPriceList(Money price1, Money? price2, Money? price3)
    {
        Price1 = price1;
        Price2 = price2;
        Price3 = price3;
    }

    public static ProductPriceList Create(Money price1, Money? price2 = null, Money? price3 = null)
    {
        ValidateAmount(price1);
        if (price2.HasValue) ValidateAmount(price2.Value);
        if (price3.HasValue) ValidateAmount(price3.Value);

        if ((price2.HasValue && price2.Value.Currency != price1.Currency) ||
            (price3.HasValue && price3.Value.Currency != price1.Currency))
            throw new DomainRuleException("All product price tiers must use the same currency.");

        return new ProductPriceList(price1, price2, price3);
    }

    public Money GetPrice(PriceTier tier) => tier switch
    {
        PriceTier.Price1 => Price1,
        PriceTier.Price2 => Price2 ?? throw new DomainRuleException("Price 2 is not configured for this product."),
        PriceTier.Price3 => Price3 ?? throw new DomainRuleException("Price 3 is not configured for this product."),
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };

    public ProductPriceList WithPrice(PriceTier tier, Money price)
    {
        ValidateAmount(price);
        if (price.Currency != Price1.Currency)
            throw new DomainRuleException("Price currency cannot change without an explicit price-list operation.");

        return tier switch
        {
            PriceTier.Price1 => new ProductPriceList(price, Price2, Price3),
            PriceTier.Price2 => new ProductPriceList(Price1, price, Price3),
            PriceTier.Price3 => new ProductPriceList(Price1, Price2, price),
            _ => throw new ArgumentOutOfRangeException(nameof(tier))
        };
    }

    private static void ValidateAmount(Money price)
    {
        if (price.Amount < 0m)
            throw new DomainRuleException("Product price cannot be negative.");
    }
}
