using Pos.Domain.Inventory;

namespace Pos.Domain.Tests;

public sealed class InventoryCostPolicyTests
{
    public static TheoryData<decimal, decimal?, decimal, decimal, decimal?> KnownCostCases => new()
    {
        { 0m, null, 5m, 12m, 12m },
        { 3m, 10m, 2m, 15m, 12m }
    };

    public static TheoryData<decimal, decimal, decimal, decimal> InvalidCostCases => new()
    {
        { -1m, 10m, 1m, 10m }, { 1m, -1m, 1m, 10m }, { 1m, 10m, 0m, 10m }, { 1m, 10m, 1m, -1m }
    };

    [Theory]
    [MemberData(nameof(KnownCostCases))]
    public void Receipt_updates_weighted_average_only_when_existing_stock_value_is_known(
        decimal currentQuantity, decimal? currentCost, decimal receivedQuantity, decimal receivedCost, decimal? expected)
        => Assert.Equal(expected, InventoryCostPolicy.WeightedAverageUnitCost(currentQuantity, currentCost, receivedQuantity, receivedCost));

    [Fact]
    public void Receipt_does_not_guess_the_cost_of_existing_unvalued_stock()
        => Assert.Null(InventoryCostPolicy.WeightedAverageUnitCost(4m, null, 2m, 15m));

    [Fact]
    public void Administrator_can_initialize_a_missing_cost_without_overwriting_a_known_cost()
        => Assert.Equal(15.1234m, InventoryCostPolicy.InitializeMissingCost(null, 15.1234m));

    [Fact]
    public void Initial_cost_cannot_be_applied_when_a_cost_is_already_known()
        => Assert.Throws<InvalidOperationException>(() => InventoryCostPolicy.InitializeMissingCost(12m, 15m));

    [Theory]
    [InlineData(-1)]
    [InlineData(1.23456)]
    public void Initial_cost_rejects_negative_values_and_more_than_four_decimals(decimal cost)
        => Assert.Throws<ArgumentOutOfRangeException>(() => InventoryCostPolicy.InitializeMissingCost(null, cost));

    [Theory]
    [MemberData(nameof(InvalidCostCases))]
    public void Receipt_cost_rejects_invalid_quantity_or_amount(decimal currentQuantity, decimal currentCost,
        decimal receivedQuantity, decimal receivedCost)
        => Assert.Throws<ArgumentOutOfRangeException>(() => InventoryCostPolicy.WeightedAverageUnitCost(
            currentQuantity, currentCost, receivedQuantity, receivedCost));

    [Fact]
    public void Receipt_can_only_be_reversed_when_stock_still_matches_the_post_receipt_snapshot()
    {
        Assert.True(InventoryCostPolicy.CanReverseLastReceipt(12m, 12m, hasLaterMovement: false));
        Assert.False(InventoryCostPolicy.CanReverseLastReceipt(10m, 12m, hasLaterMovement: false));
        Assert.False(InventoryCostPolicy.CanReverseLastReceipt(12m, 12m, hasLaterMovement: true));
    }
}
