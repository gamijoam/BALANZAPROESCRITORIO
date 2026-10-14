using Pos.Domain.Catalog;
using Pos.Domain.Inventory;
using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public class CatalogAndInventoryTests
{
    [Fact]
    public void Product_requires_sku_name_and_non_negative_price()
    {
        Assert.Throws<ArgumentException>(() => Product.Create(" ", "Arroz", new Money(1m, Currency.USD)));
        Assert.Throws<ArgumentException>(() => Product.Create("A-1", " ", new Money(1m, Currency.USD)));
        Assert.Throws<DomainRuleException>(() => Product.Create("A-1", "Arroz", new Money(-1m, Currency.USD)));
    }

    [Fact]
    public void Product_keeps_trimmed_unique_barcodes_and_cannot_be_sold_when_inactive()
    {
        var product = Product.Create("A-1", "Arroz", new Money(2m, Currency.USD));
        Assert.True(product.IsActive);
        product.AddBarcode("  123456  ");

        Assert.Equal("123456", Assert.Single(product.Barcodes));
        Assert.Throws<DomainRuleException>(() => product.AddBarcode("123456"));
        product.Deactivate();
        Assert.False(product.IsActive);
    }

    [Fact]
    public void Product_allows_zero_price_and_changes_price_only_in_its_existing_currency()
    {
        var product = Product.Create("GIFT", "Bolsa promocional", new Money(0m, Currency.USD));
        Assert.Equal("GIFT", product.Sku);
        Assert.Equal("Bolsa promocional", product.Name);
        product.ChangePrice(new Money(1.25m, Currency.USD));

        Assert.Equal(new Money(1.25m, Currency.USD), product.Price);
        product.ChangePrice(new Money(0m, Currency.USD));
        Assert.Equal(new Money(0m, Currency.USD), product.Price);
        Assert.Throws<DomainRuleException>(() => product.ChangePrice(new Money(40m, Currency.VES)));
        Assert.Throws<DomainRuleException>(() => product.ChangePrice(new Money(-1m, Currency.USD)));
    }

    [Fact]
    public void Product_owns_three_price_tiers_and_price_one_remains_the_legacy_price()
    {
        var product = Product.Create("A-2", "Producto", ProductPriceList.Create(
            new Money(12m, Currency.USD), new Money(10m, Currency.USD), new Money(9m, Currency.USD)),
            TaxTreatment.Exempt);

        Assert.Equal(new Money(12m, Currency.USD), product.Price);
        Assert.Equal(new Money(10m, Currency.USD), product.Prices.GetPrice(PriceTier.Price2));
        product.ChangePrice(PriceTier.Price2, new Money(11m, Currency.USD));
        Assert.Equal(new Money(11m, Currency.USD), product.Prices.GetPrice(PriceTier.Price2));
        Assert.Equal(new Money(12m, Currency.USD), product.Price);
    }

    [Fact]
    public void Product_barcode_collection_cannot_be_used_to_bypass_add_barcode_rules()
    {
        var product = Product.Create("A-1", "Arroz", new Money(2m, Currency.USD));

        Assert.False(product.Barcodes is ISet<string>);
        Assert.Throws<ArgumentException>(() => product.AddBarcode(" "));
    }

    [Fact]
    public void Stock_balance_rejects_overselling_without_changing_quantity()
    {
        var stock = new StockBalance("A-1", 1m);

        Assert.Throws<DomainRuleException>(() => stock.Remove(2m));
        Assert.Equal(1m, stock.QuantityOnHand);
    }

    [Fact]
    public void Stock_balance_accepts_only_positive_receipts_and_removals()
    {
        var stock = new StockBalance("A-1", 3m);
        stock.Receive(2.5m);
        stock.Remove(1.25m);

        Assert.Equal(4.25m, stock.QuantityOnHand);
        Assert.Throws<ArgumentOutOfRangeException>(() => stock.Receive(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => stock.Remove(-1m));
    }

    [Fact]
    public void Stock_balance_allows_removing_the_exact_remaining_quantity()
    {
        var stock = new StockBalance("A-1", 1m);

        stock.Remove(1m);

        Assert.Equal(0m, stock.QuantityOnHand);
    }

    [Fact]
    public void Stock_balance_allows_zero_initial_stock_but_rejects_a_negative_opening_balance()
    {
        Assert.Equal(0m, new StockBalance("A-1").QuantityOnHand);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StockBalance("A-1", -0.1m));
        Assert.Throws<ArgumentException>(() => new StockBalance(" "));
    }

    [Fact]
    public void Inventory_movement_requires_nonzero_delta_reason_actor_and_keeps_signed_quantity()
    {
        var productId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var movement = StockMovement.Create(productId, -2.5m, "Merma", actorId, DateTimeOffset.UtcNow);

        Assert.Equal(productId, movement.ProductId);
        Assert.Equal(-2.5m, movement.QuantityDelta);
        Assert.Equal("Merma", movement.Reason);
        Assert.Equal(actorId, movement.ActorId);
        Assert.Throws<ArgumentOutOfRangeException>(() => StockMovement.Create(productId, 0m, "Ajuste", actorId, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => StockMovement.Create(productId, 1m, " ", actorId, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => StockMovement.Create(productId, 1m, "Recepción", Guid.Empty, DateTimeOffset.UtcNow));
    }
}
