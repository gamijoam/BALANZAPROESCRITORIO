using Pos.Modules.Finance.Desktop;

namespace Pos.Modules.Finance.Desktop.Tests;

public sealed class FinanceMarginInputPolicyTests
{
    [Theory]
    [InlineData("25,5", 25.5)]
    [InlineData("25.5", 25.5)]
    [InlineData("0", 0)]
    public void Margin_percent_accepts_comma_or_dot_without_locale_ambiguity(string input, decimal expected)
    {
        Assert.True(FinanceMarginInputPolicy.TryParsePercent(input, out var percent));
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_margin_disables_that_price_tier(string input)
    {
        Assert.True(FinanceMarginInputPolicy.TryParsePercent(input, out var percent));
        Assert.Null(percent);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("25,555")]
    [InlineData("1.234,56")]
    [InlineData("abc")]
    public void Margin_percent_rejects_out_of_range_or_ambiguous_values(string input)
        => Assert.False(FinanceMarginInputPolicy.TryParsePercent(input, out _));
}
