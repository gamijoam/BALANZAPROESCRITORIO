using Pos.Domain.Finance;

namespace Pos.Domain.Customers;

/// <summary>In-memory customer receivable balance; persistence and immutable ledger entries belong to Infrastructure.</summary>
public sealed class ReceivableAccount
{
    public Guid CustomerId { get; }
    public Money CreditLimit { get; }
    public Money Balance { get; private set; }

    private ReceivableAccount(Guid customerId, Money creditLimit)
    {
        CustomerId = customerId;
        CreditLimit = creditLimit;
        Balance = new Money(0m, creditLimit.Currency);
    }

    public static ReceivableAccount Create(Guid customerId, Money creditLimit)
    {
        if (customerId == Guid.Empty) throw new ArgumentException("Customer is required.", nameof(customerId));
        if (creditLimit.Amount < 0m) throw new ArgumentOutOfRangeException(nameof(creditLimit));
        return new ReceivableAccount(customerId, creditLimit);
    }

    public void Charge(Money amount)
    {
        EnsurePositiveAndMatching(amount);
        if (Balance.Amount + amount.Amount > CreditLimit.Amount)
            throw new DomainRuleException("Charge exceeds the customer credit limit.");
        Balance += amount;
    }

    public void ApplyPayment(Money amount)
    {
        EnsurePositiveAndMatching(amount);
        if (amount.Amount > Balance.Amount)
            throw new DomainRuleException("Payment exceeds the outstanding balance.");
        Balance -= amount;
    }

    private void EnsurePositiveAndMatching(Money amount)
    {
        if (amount.Amount <= 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        if (amount.Currency != Balance.Currency)
            throw new DomainRuleException("Receivable operations must use the account currency.");
    }
}
