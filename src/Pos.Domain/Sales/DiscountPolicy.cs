namespace Pos.Domain.Sales;

public sealed class DiscountPolicy
{
    public decimal MaximumPercent { get; }

    public DiscountPolicy(decimal maximumPercent)
    {
        if (maximumPercent is < 0m or > 100m) throw new ArgumentOutOfRangeException(nameof(maximumPercent));
        MaximumPercent = maximumPercent;
    }

    public void EnsureAllowed(decimal percent, bool supervisorAuthorized)
    {
        if (percent is < 0m or > 100m) throw new ArgumentOutOfRangeException(nameof(percent));
        if (percent > MaximumPercent && !supervisorAuthorized)
            throw new DomainRuleException("Discount exceeds the operator limit and requires supervisor approval.");
    }
}
