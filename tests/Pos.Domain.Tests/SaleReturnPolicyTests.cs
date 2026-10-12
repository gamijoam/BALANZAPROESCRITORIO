using Pos.Domain.Sales;

namespace Pos.Domain.Tests;

public sealed class SaleReturnPolicyTests
{
    [Fact]
    public void Calculates_partial_return_from_the_original_unit_price_and_preserves_restock_decision()
    {
        var lineId = Guid.NewGuid();

        var result = SaleReturnPolicy.Validate(
            [new OriginalSaleLine(lineId, 3m, 10m)],
            [],
            [new RequestedSaleReturnLine(lineId, 1.5m, Restock: true)]);

        var returned = Assert.Single(result);
        Assert.Equal(1.5m, returned.Quantity);
        Assert.Equal(15m, returned.RefundAmount);
        Assert.True(returned.Restock);
    }

    [Fact]
    public void Rejects_returning_more_than_the_unreturned_quantity()
    {
        var lineId = Guid.NewGuid();

        Assert.Throws<DomainRuleException>(() => SaleReturnPolicy.Validate(
            [new OriginalSaleLine(lineId, 3m, 10m)],
            [new ReturnedSaleLine(lineId, 2m)],
            [new RequestedSaleReturnLine(lineId, 1.001m, Restock: false)]));
    }

    [Fact]
    public void Rejects_duplicate_or_unknown_sale_lines()
    {
        var lineId = Guid.NewGuid();

        Assert.Throws<DomainRuleException>(() => SaleReturnPolicy.Validate(
            [new OriginalSaleLine(lineId, 1m, 10m)],
            [],
            [new RequestedSaleReturnLine(lineId, 0.5m, false), new RequestedSaleReturnLine(lineId, 0.5m, true)]));

        Assert.Throws<DomainRuleException>(() => SaleReturnPolicy.Validate(
            [new OriginalSaleLine(lineId, 1m, 10m)],
            [],
            [new RequestedSaleReturnLine(Guid.NewGuid(), 1m, true)]));
    }
}
