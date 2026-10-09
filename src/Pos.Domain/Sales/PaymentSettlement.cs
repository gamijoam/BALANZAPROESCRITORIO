using Pos.Domain.Finance;

namespace Pos.Domain.Sales;

public enum PaymentMethod
{
    Cash,
    MobilePayment,
    Transfer,
    Debit,
    Card
}

public sealed record SalePayment(Money Tendered, PaymentMethod Method, ExchangeRate? ExchangeRate, string? Reference)
{
    public static SalePayment Create(Money tendered, PaymentMethod method, ExchangeRate? exchangeRate = null, string? reference = null)
    {
        if (!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method));
        if (tendered.Amount <= 0m) throw new ArgumentOutOfRangeException(nameof(tendered));
        if (reference is not null && string.IsNullOrWhiteSpace(reference)) reference = null;
        return new SalePayment(tendered, method, exchangeRate, reference?.Trim());
    }
}

public sealed record PaymentSettlement(Money Paid, Money Change)
{
    public static PaymentSettlement Settle(Money due, IReadOnlyCollection<SalePayment> payments, int scale)
    {
        ArgumentNullException.ThrowIfNull(payments);
        if (due.Amount < 0m) throw new ArgumentOutOfRangeException(nameof(due));
        if (scale is < 0 or > 28) throw new ArgumentOutOfRangeException(nameof(scale));
        if (due.Amount == 0m && payments.Count == 0)
            return new PaymentSettlement(new Money(0m, due.Currency), new Money(0m, due.Currency));
        if (payments.Count == 0) throw new DomainRuleException("At least one payment is required.");

        var duplicateReference = payments
            .Where(payment => payment.Reference is not null)
            .GroupBy(payment => payment.Reference!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);
        if (duplicateReference) throw new DomainRuleException("Payment references must be unique within a sale.");

        var paid = new Money(0m, due.Currency);
        foreach (var payment in payments)
        {
            Money settled;
            if (payment.Tendered.Currency == due.Currency)
            {
                if (payment.ExchangeRate is not null)
                    throw new DomainRuleException("A conversion rate is not valid for a payment in the sale currency.");
                settled = payment.Tendered;
            }
            else
            {
                var rate = payment.ExchangeRate;
                if (rate is null || rate.FromCurrency != payment.Tendered.Currency || rate.ToCurrency != due.Currency)
                    throw new DomainRuleException("A payment in another currency requires a matching conversion rate.");
                settled = rate.Convert(payment.Tendered, scale);
            }
            paid += settled;
        }

        if (paid.Amount < due.Amount) throw new DomainRuleException("Payments do not cover the amount due.");

        var change = new Money(paid.Amount - due.Amount, due.Currency);
        var singlePayment = payments.Count == 1 ? payments.Single() : null;
        if (change.Amount > 0m &&
            (singlePayment is null || singlePayment.Method != PaymentMethod.Cash || singlePayment.Tendered.Currency != due.Currency))
            throw new DomainRuleException("Change is allowed only for a single cash payment in the sale currency.");

        return new PaymentSettlement(paid, change);
    }
}
