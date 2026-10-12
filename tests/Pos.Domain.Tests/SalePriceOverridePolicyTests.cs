using Pos.Domain.Sales;

namespace Pos.Domain.Tests;

public sealed class SalePriceOverridePolicyTests
{
    public static IEnumerable<object?[]> OverrideCases =>
    [
        [8.25m, "Precio acordado", true],
        [0m, "Precio acordado", false],
        [-1m, "Precio acordado", false],
        [8.12345m, "Precio acordado", false],
        [8.25m, "  ", false],
        [null, "", true],
        [null, "No aplica", false]
    ];

    [Theory]
    [MemberData(nameof(OverrideCases))]
    public void Validates_override_amount_and_reason(decimal? price, string? reason, bool expected)
    {
        Assert.Equal(expected, SalePriceOverridePolicy.IsValid(price, reason));
    }
}
