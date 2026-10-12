using Pos.Domain.Finance;

namespace Pos.Domain.Tests;

public sealed class PaymentSettlementPolicyTests
{
    [Fact]
    public void One_cent_usd_precision_allows_and_records_a_small_ves_conversion_difference()
    {
        var result = PaymentSettlementPolicy.Evaluate(1_000m, "VES", [new PaymentTender(27.40m, "USD")], 36.50m);

        Assert.Equal(1_000.10m, result.PaidInSaleCurrency);
        Assert.Equal(-0.10m, result.RoundingAdjustment);
        Assert.Equal(0.1825m, result.AllowedDifference);
        Assert.True(result.IsSettled);
    }

    [Fact]
    public void Difference_larger_than_half_smallest_tender_unit_is_not_accepted()
    {
        var result = PaymentSettlementPolicy.Evaluate(1_000m, "VES", [new PaymentTender(27.39m, "USD")], 36.50m);

        Assert.Equal(999.74m, result.PaidInSaleCurrency);
        Assert.Equal(0.26m, result.RoundingAdjustment);
        Assert.False(result.IsSettled);
    }

    [Fact]
    public void A_complementary_ves_tender_can_settle_the_exact_remainder()
    {
        var result = PaymentSettlementPolicy.Evaluate(1_000m, "VES",
            [new PaymentTender(27.39m, "USD"), new PaymentTender(0.26m, "VES")], 36.50m);

        Assert.Equal(1_000m, result.PaidInSaleCurrency);
        Assert.Equal(0m, result.RoundingAdjustment);
        Assert.True(result.IsSettled);
    }

    [Fact]
    public void Screenshot_payment_mix_is_settled_by_the_current_rounding_policy()
    {
        // VES 3.500 + USD 6,11 × tasa 900 = VES 8.999; la diferencia de VES 1 está dentro del límite de conversión.
        var result = PaymentSettlementPolicy.Evaluate(9_000m, "VES",
            [new PaymentTender(3_500m, "VES"), new PaymentTender(6.11m, "USD")], 900m);

        Assert.Equal(8_999m, result.PaidInSaleCurrency);
        Assert.Equal(1m, result.RoundingAdjustment);
        Assert.Equal(4.5m, result.AllowedDifference);
        Assert.True(result.IsSettled);
    }
}
