using Pos.Domain.Finance;

namespace Pos.Domain.Sales;

public enum SaleStatus
{
    Draft,
    Confirmed
}

/// <summary>Domain aggregate for sale composition and confirmation. Persistence/idempotency is enforced by the application transaction.</summary>
public sealed class Sale
{
    private readonly List<SaleLine> _lines = [];
    private Currency? _currency;

    public Guid Id { get; }
    public SaleStatus Status { get; private set; } = SaleStatus.Draft;
    public IReadOnlyList<SaleLine> Lines => _lines.AsReadOnly();

    public Money Total
    {
        get
        {
            if (_lines.Count == 0 || _currency is null)
                throw new DomainRuleException("A sale without lines has no total.");

            var total = new Money(0m, _currency.Value);
            foreach (var line in _lines) total += line.Total;
            return total;
        }
    }

    private Sale(Guid id) => Id = id;

    public static Sale Create(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Sale id is required.", nameof(id));
        return new Sale(id);
    }

    public void AddLine(SaleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        EnsureDraft();
        if (_currency.HasValue && line.Total.Currency != _currency.Value)
            throw new DomainRuleException("All sale lines must use the same currency.");
        _currency ??= line.Total.Currency;
        _lines.Add(line);
    }

    public PaymentSettlement Confirm(IReadOnlyCollection<SalePayment> payments, int scale)
    {
        EnsureDraft();
        if (_lines.Count == 0) throw new DomainRuleException("A sale requires at least one line.");

        var settlement = PaymentSettlement.Settle(Total, payments, scale);
        Status = SaleStatus.Confirmed;
        return settlement;
    }

    private void EnsureDraft()
    {
        if (Status != SaleStatus.Draft)
            throw new DomainRuleException("Only a draft sale can be changed or confirmed.");
    }
}
