using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pos.Infrastructure.Persistence;

namespace Pos.Integration.Tests;

public sealed class ProductFinanceSchemaTests
{
    [Fact]
    public void Product_schema_keeps_legacy_price_as_price_one_and_adds_optional_tiers_and_pending_tax_state()
    {
        using var db = CreateModelOnlyContext();
        var product = db.Model.FindEntityType(typeof(ProductRecord))!;
        var table = StoreObjectIdentifier.Table("products", null);

        Assert.Equal("UnitPrice", product.FindProperty(nameof(ProductRecord.UnitPrice))!.GetColumnName(table));
        Assert.Equal("Price2", product.FindProperty(nameof(ProductRecord.Price2))!.GetColumnName(table));
        Assert.Equal("Price3", product.FindProperty(nameof(ProductRecord.Price3))!.GetColumnName(table));
        Assert.Equal("TaxTreatment", product.FindProperty(nameof(ProductRecord.TaxTreatment))!.GetColumnName(table));
        Assert.Null(product.FindProperty(nameof(ProductRecord.Price2))!.GetDefaultValue());
        Assert.Null(product.FindProperty(nameof(ProductRecord.Price3))!.GetDefaultValue());
        Assert.Equal("Unclassified", product.FindProperty(nameof(ProductRecord.TaxTreatment))!.GetDefaultValue());
        Assert.Equal(18, product.FindProperty(nameof(ProductRecord.Price2))!.GetPrecision());
        Assert.Equal(4, product.FindProperty(nameof(ProductRecord.Price2))!.GetScale());
    }

    [Fact]
    public void Sale_line_schema_has_immutable_price_tax_and_optional_cost_snapshots()
    {
        using var db = CreateModelOnlyContext();
        var line = db.Model.FindEntityType(typeof(SaleLineRecord))!;
        var table = StoreObjectIdentifier.Table("sale_lines", null);

        Assert.Equal("PriceTier", line.FindProperty(nameof(SaleLineRecord.PriceTier))!.GetColumnName(table));
        Assert.Equal("TaxTreatment", line.FindProperty(nameof(SaleLineRecord.TaxTreatment))!.GetColumnName(table));
        Assert.Equal("NotCaptured", line.FindProperty(nameof(SaleLineRecord.TaxTreatment))!.GetDefaultValue());
        Assert.Equal("TaxRate", line.FindProperty(nameof(SaleLineRecord.TaxRate))!.GetColumnName(table));
        Assert.Equal("TaxAmount", line.FindProperty(nameof(SaleLineRecord.TaxAmount))!.GetColumnName(table));
        Assert.Equal("UnitCost", line.FindProperty(nameof(SaleLineRecord.UnitCost))!.GetColumnName(table));
        Assert.True(line.FindProperty(nameof(SaleLineRecord.UnitCost))!.IsNullable);
        Assert.Equal(18, line.FindProperty(nameof(SaleLineRecord.TaxAmount))!.GetPrecision());
        Assert.Equal(4, line.FindProperty(nameof(SaleLineRecord.TaxAmount))!.GetScale());
    }

    [Fact]
    public void Price_history_identifies_which_price_tier_changed()
    {
        using var db = CreateModelOnlyContext();
        var history = db.Model.FindEntityType(typeof(PriceHistoryRecord))!;
        var table = StoreObjectIdentifier.Table("price_history", null);

        Assert.Equal("PriceTier", history.FindProperty(nameof(PriceHistoryRecord.PriceTier))!.GetColumnName(table));
        Assert.Equal("Price1", history.FindProperty(nameof(PriceHistoryRecord.PriceTier))!.GetDefaultValue());
        Assert.True(history.FindProperty(nameof(PriceHistoryRecord.OldPrice))!.IsNullable);
    }

    [Fact]
    public void Purchase_receipt_effect_uses_the_receipt_as_an_idempotency_key()
    {
        using var db = CreateModelOnlyContext();
        var effect = db.Model.FindEntityType(typeof(PurchaseReceiptEffectRecord))!;
        var table = StoreObjectIdentifier.Table("purchase_receipt_effects", null);

        Assert.Equal("ReceiptId", effect.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal("actor_id", effect.FindProperty(nameof(PurchaseReceiptEffectRecord.ActorId))!.GetColumnName(table));
        Assert.Equal("terminal_id", effect.FindProperty(nameof(PurchaseReceiptEffectRecord.TerminalId))!.GetColumnName(table));
        Assert.Equal("applied_at_utc", effect.FindProperty(nameof(PurchaseReceiptEffectRecord.AppliedAtUtc))!.GetColumnName(table));
        Assert.Equal("reversed_at_utc", effect.FindProperty(nameof(PurchaseReceiptEffectRecord.ReversedAtUtc))!.GetColumnName(table));
        var lines = db.Model.FindEntityType(typeof(PurchaseReceiptEffectLineRecord))!;
        Assert.Equal("purchase_receipt_effect_lines", lines.GetTableName());
        Assert.Equal("quantity_after_receipt", lines.FindProperty(nameof(PurchaseReceiptEffectLineRecord.QuantityAfterReceipt))!
            .GetColumnName(StoreObjectIdentifier.Table("purchase_receipt_effect_lines", null)));
    }

    private static PosDbContext CreateModelOnlyContext() => new(
        new DbContextOptionsBuilder<PosDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model_only;Username=model_only;Password=model_only")
            .Options);
}
