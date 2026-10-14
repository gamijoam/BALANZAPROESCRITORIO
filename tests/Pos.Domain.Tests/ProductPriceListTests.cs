using Pos.Domain.Catalog;
using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public sealed class ProductPriceListTests
{
    [Fact]
    public void Create_keeps_price_one_and_leaves_unconfigured_tiers_empty()
    {
        var prices = ProductPriceList.Create(new Money(12.50m, Currency.USD));

        Assert.Equal(new Money(12.50m, Currency.USD), prices.GetPrice(PriceTier.Price1));
        Assert.Null(prices.Price2);
        Assert.Null(prices.Price3);
    }

    [Fact]
    public void Selecting_an_unconfigured_tier_is_rejected_without_falling_back_to_price_one()
    {
        var prices = ProductPriceList.Create(new Money(12.50m, Currency.USD));

        Assert.Throws<DomainRuleException>(() => prices.GetPrice(PriceTier.Price2));
        Assert.Throws<DomainRuleException>(() => prices.GetPrice(PriceTier.Price3));
    }

    [Fact]
    public void All_configured_tiers_use_the_product_currency_and_nonnegative_amounts()
    {
        Assert.Throws<DomainRuleException>(() => ProductPriceList.Create(
            new Money(12m, Currency.USD), new Money(10m, Currency.VES)));
        Assert.Throws<DomainRuleException>(() => ProductPriceList.Create(
            new Money(12m, Currency.USD), price3: new Money(-1m, Currency.USD)));
    }

    [Fact]
    public void With_price_updates_only_the_requested_tier_and_keeps_the_other_snapshots()
    {
        var original = ProductPriceList.Create(
            new Money(12m, Currency.USD), new Money(10m, Currency.USD), new Money(9m, Currency.USD));

        var changed = original.WithPrice(PriceTier.Price2, new Money(11m, Currency.USD));

        Assert.Equal(new Money(12m, Currency.USD), changed.Price1);
        Assert.Equal(new Money(11m, Currency.USD), changed.Price2);
        Assert.Equal(new Money(9m, Currency.USD), changed.Price3);
        Assert.Equal(new Money(10m, Currency.USD), original.Price2);
    }
}
