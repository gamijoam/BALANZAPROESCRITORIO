using Pos.Domain.Finance;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests;

public class SalesAndPaymentsTests
{
    [Fact]
    public void Sale_confirms_once_after_exact_payment_and_freezes_its_total()
    {
        var policy = new DiscountPolicy(0m);
        var line = SaleLine.Create("A-1", "Arroz", 2m, new Money(10m, Currency.USD),
            0m, 0m, policy, false, 2);
        var sale = Sale.Create(Guid.NewGuid());
        sale.AddLine(line);

        var settlement = sale.Confirm(
            new[] { SalePayment.Create(new Money(20m, Currency.USD), PaymentMethod.Cash) }, 2);

        Assert.Equal(SaleStatus.Confirmed, sale.Status);
        Assert.Equal(new Money(20m, Currency.USD), sale.Total);
        Assert.Equal(new Money(20m, Currency.USD), settlement.Paid);
        Assert.Throws<DomainRuleException>(() => sale.AddLine(line));
        Assert.Throws<DomainRuleException>(() => sale.Confirm(
            new[] { SalePayment.Create(new Money(20m, Currency.USD), PaymentMethod.Cash) }, 2));
    }

    [Fact]
    public void Sale_with_incomplete_payment_remains_a_draft()
    {
        var line = SaleLine.Create("A-1", "Arroz", 1m, new Money(20m, Currency.USD),
            0m, 0m, new DiscountPolicy(0m), false, 2);
        var sale = Sale.Create(Guid.NewGuid());
        sale.AddLine(line);

        Assert.Throws<DomainRuleException>(() => sale.Confirm(
            new[] { SalePayment.Create(new Money(19m, Currency.USD), PaymentMethod.Cash) }, 2));
        Assert.Equal(SaleStatus.Draft, sale.Status);
    }

    [Fact]
    public void Sale_requires_lines_and_consistent_currency()
    {
        var sale = Sale.Create(Guid.NewGuid());
        Assert.Throws<DomainRuleException>(() => sale.Confirm(
            new[] { SalePayment.Create(new Money(1m, Currency.USD), PaymentMethod.Cash) }, 2));

        sale.AddLine(SaleLine.Create("A-1", "Arroz", 1m, new Money(1m, Currency.USD),
            0m, 0m, new DiscountPolicy(0m), false, 2));
        Assert.Throws<DomainRuleException>(() => sale.AddLine(SaleLine.Create("B-1", "Azúcar", 1m,
            new Money(1m, Currency.VES), 0m, 0m, new DiscountPolicy(0m), false, 2)));
    }

    [Fact]
    public void Sale_line_snapshots_discount_tax_and_total_with_explicit_rounding()
    {
        var policy = new DiscountPolicy(maximumPercent: 10m);

        var line = SaleLine.Create(
            "A-1", "Arroz", 2m, new Money(10m, Currency.USD),
            taxRate: 0.16m, discountPercent: 10m, policy, supervisorAuthorized: false, scale: 2);

        Assert.Equal(new Money(18m, Currency.USD), line.SubtotalAfterDiscount);
        Assert.Equal(new Money(2.88m, Currency.USD), line.TaxAmount);
        Assert.Equal(new Money(20.88m, Currency.USD), line.Total);
    }

    [Fact]
    public void Discount_above_operator_limit_requires_specific_supervisor_authorization()
    {
        var policy = new DiscountPolicy(maximumPercent: 5m);

        Assert.Throws<DomainRuleException>(() => SaleLine.Create(
            "A-1", "Arroz", 1m, new Money(10m, Currency.USD),
            taxRate: 0m, discountPercent: 6m, policy, supervisorAuthorized: false, scale: 2));

        var authorized = SaleLine.Create(
            "A-1", "Arroz", 1m, new Money(10m, Currency.USD),
            taxRate: 0m, discountPercent: 6m, policy, supervisorAuthorized: true, scale: 2);
        Assert.Equal(new Money(9.4m, Currency.USD), authorized.Total);
    }

    [Fact]
    public void Maximum_discount_can_be_set_to_one_hundred_percent()
    {
        var line = SaleLine.Create("A-1", "Arroz", 1m, new Money(10m, Currency.USD),
            0.16m, 100m, new DiscountPolicy(100m), false, 2);

        Assert.Equal(new Money(0m, Currency.USD), line.SubtotalAfterDiscount);
        Assert.Equal(new Money(0m, Currency.USD), line.TaxAmount);
        Assert.Equal(new Money(0m, Currency.USD), line.Total);
    }

    [Fact]
    public void Sale_line_rejects_invalid_quantity_tax_rate_and_discount()
    {
        var policy = new DiscountPolicy(10m);

        Assert.Throws<ArgumentOutOfRangeException>(() => SaleLine.Create(
            "A-1", "Arroz", 0m, new Money(10m, Currency.USD), 0m, 0m, policy, false, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleLine.Create(
            "A-1", "Arroz", 1m, new Money(10m, Currency.USD), -0.01m, 0m, policy, false, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleLine.Create(
            "A-1", "Arroz", 1m, new Money(10m, Currency.USD), 0m, 101m, policy, true, 2));
    }

    [Fact]
    public void Payment_settlement_accepts_exact_mixed_currency_payment_with_rate_snapshot()
    {
        var rate = ExchangeRate.Create(
            Currency.USD, Currency.VES, 40m,
            new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), "manual");
        var payments = new[]
        {
            SalePayment.Create(new Money(1m, Currency.USD), PaymentMethod.Cash, rate),
            SalePayment.Create(new Money(60m, Currency.VES), PaymentMethod.MobilePayment)
        };

        var settlement = PaymentSettlement.Settle(new Money(100m, Currency.VES), payments, scale: 2);

        Assert.Equal(new Money(100m, Currency.VES), settlement.Paid);
        Assert.Equal(new Money(0m, Currency.VES), settlement.Change);
    }

    [Fact]
    public void Payment_settlement_rejects_short_payment_wrong_rate_and_non_cash_change()
    {
        Assert.Throws<DomainRuleException>(() => PaymentSettlement.Settle(
            new Money(100m, Currency.VES),
            new[] { SalePayment.Create(new Money(99m, Currency.VES), PaymentMethod.Cash) }, 2));

        var wrongRate = ExchangeRate.Create(
            Currency.VES, Currency.USD, 0.025m,
            new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), "manual");
        Assert.Throws<DomainRuleException>(() => PaymentSettlement.Settle(
            new Money(100m, Currency.VES),
            new[] { SalePayment.Create(new Money(1m, Currency.USD), PaymentMethod.Cash, wrongRate) }, 2));

        Assert.Throws<DomainRuleException>(() => PaymentSettlement.Settle(
            new Money(100m, Currency.VES),
            new[] { SalePayment.Create(new Money(101m, Currency.VES), PaymentMethod.Transfer, reference: "R-1") }, 2));
    }

    [Fact]
    public void Payment_settlement_rejects_duplicate_external_references()
    {
        var payments = new[]
        {
            SalePayment.Create(new Money(50m, Currency.VES), PaymentMethod.Transfer, reference: "REF-1"),
            SalePayment.Create(new Money(50m, Currency.VES), PaymentMethod.MobilePayment, reference: " ref-1 ")
        };

        Assert.Throws<DomainRuleException>(() => PaymentSettlement.Settle(
            new Money(100m, Currency.VES), payments, 2));
    }

    [Fact]
    public void Distinct_external_references_are_allowed_on_exact_mixed_tender()
    {
        var payments = new[]
        {
            SalePayment.Create(new Money(50m, Currency.VES), PaymentMethod.Transfer, reference: "REF-1"),
            SalePayment.Create(new Money(50m, Currency.VES), PaymentMethod.MobilePayment, reference: "REF-2")
        };

        var settlement = PaymentSettlement.Settle(new Money(100m, Currency.VES), payments, 2);
        Assert.Equal(new Money(100m, Currency.VES), settlement.Paid);
    }

    [Fact]
    public void Sale_payment_rejects_zero_tender_and_exact_cash_has_no_change()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SalePayment.Create(new Money(0m, Currency.USD), PaymentMethod.Cash));

        var settlement = PaymentSettlement.Settle(new Money(10m, Currency.USD),
            new[] { SalePayment.Create(new Money(10m, Currency.USD), PaymentMethod.Cash) }, 2);

        Assert.Equal(new Money(0m, Currency.USD), settlement.Change);
    }

    [Fact]
    public void Cash_overpayment_returns_change_in_sale_currency()
    {
        var settlement = PaymentSettlement.Settle(new Money(10m, Currency.USD),
            new[] { SalePayment.Create(new Money(15m, Currency.USD), PaymentMethod.Cash) }, 2);

        Assert.Equal(new Money(15m, Currency.USD), settlement.Paid);
        Assert.Equal(new Money(5m, Currency.USD), settlement.Change);
    }

    [Fact]
    public void A_zero_total_sale_can_be_settled_without_a_tender()
    {
        var settlement = PaymentSettlement.Settle(new Money(0m, Currency.USD), Array.Empty<SalePayment>(), 2);

        Assert.Equal(new Money(0m, Currency.USD), settlement.Paid);
        Assert.Equal(new Money(0m, Currency.USD), settlement.Change);
    }

    [Fact]
    public void Settlement_rejects_invalid_due_scale_empty_payment_for_positive_due_and_unknown_method()
    {
        Assert.Throws<ArgumentNullException>(() => PaymentSettlement.Settle(new Money(1m, Currency.USD), null!, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PaymentSettlement.Settle(new Money(-1m, Currency.USD), Array.Empty<SalePayment>(), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PaymentSettlement.Settle(new Money(1m, Currency.USD), Array.Empty<SalePayment>(), -1));
        Assert.Throws<DomainRuleException>(() => PaymentSettlement.Settle(new Money(1m, Currency.USD), Array.Empty<SalePayment>(), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SalePayment.Create(new Money(1m, Currency.USD), (PaymentMethod)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => PaymentSettlement.Settle(new Money(1m, Currency.USD),
            new[] { SalePayment.Create(new Money(1m, Currency.USD), PaymentMethod.Cash) }, 29));
    }

    [Fact]
    public void Settlement_accepts_zero_and_maximum_supported_scale()
    {
        Assert.Equal(new Money(1m, Currency.USD), PaymentSettlement.Settle(new Money(1m, Currency.USD),
            new[] { SalePayment.Create(new Money(1m, Currency.USD), PaymentMethod.Cash) }, 0).Paid);
        Assert.Equal(new Money(1m, Currency.USD), PaymentSettlement.Settle(new Money(1m, Currency.USD),
            new[] { SalePayment.Create(new Money(1m, Currency.USD), PaymentMethod.Cash) }, 28).Paid);
    }

    [Fact]
    public void Whitespace_payment_reference_is_normalized_to_missing()
    {
        var payment = SalePayment.Create(new Money(1m, Currency.USD), PaymentMethod.Cash, reference: "   ");

        Assert.Null(payment.Reference);
    }

    [Fact]
    public void Sale_line_allows_a_one_hundred_percent_tax_rate_and_rejects_negative_unit_price()
    {
        var line = SaleLine.Create("A-1", "Arroz", 1m, new Money(1m, Currency.USD),
            1m, 0m, new DiscountPolicy(0m), false, 2);

        Assert.Equal(new Money(1m, Currency.USD), line.TaxAmount);
        Assert.Throws<DomainRuleException>(() => SaleLine.Create("A-1", "Arroz", 1m,
            new Money(-1m, Currency.USD), 0m, 0m, new DiscountPolicy(0m), false, 2));
    }

    [Fact]
    public void Sale_line_allows_zero_price_and_scale_boundaries()
    {
        var zeroPrice = SaleLine.Create("FREE", "Muestra", 1m, new Money(0m, Currency.USD),
            0m, 0m, new DiscountPolicy(0m), false, 0);
        var maximumScale = SaleLine.Create("A-1", "Arroz", 1m, new Money(1m, Currency.USD),
            0m, 0m, new DiscountPolicy(0m), false, 28);

        Assert.Equal(new Money(0m, Currency.USD), zeroPrice.Total);
        Assert.Equal(new Money(1m, Currency.USD), maximumScale.Total);
    }

    [Fact]
    public void Sale_rejects_empty_id_and_null_lines()
    {
        Assert.Throws<ArgumentException>(() => Sale.Create(Guid.Empty));
        var sale = Sale.Create(Guid.NewGuid());
        Assert.Throws<ArgumentNullException>(() => sale.AddLine(null!));
        Assert.Throws<DomainRuleException>(() => _ = sale.Total);
    }

    [Fact]
    public void Sale_line_validates_snapshot_fields_policy_and_scale()
    {
        var price = new Money(1m, Currency.USD);
        var policy = new DiscountPolicy(10m);
        Assert.Throws<ArgumentException>(() => SaleLine.Create(" ", "Arroz", 1m, price, 0m, 0m, policy, false, 2));
        Assert.Throws<ArgumentException>(() => SaleLine.Create("A-1", " ", 1m, price, 0m, 0m, policy, false, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleLine.Create("A-1", "Arroz", 1m, price, 1.01m, 0m, policy, false, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleLine.Create("A-1", "Arroz", 1m, price, 0m, 0m, policy, false, 29));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleLine.Create("A-1", "Arroz", 1m, price, 0m, 0m, policy, false, -1));
        Assert.Throws<ArgumentNullException>(() => SaleLine.Create("A-1", "Arroz", 1m, price, 0m, 0m, null!, false, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleLine.Create("A-1", "Arroz", 1m, price, 0m, -1m, policy, false, 2));
    }

    [Fact]
    public void Discount_policy_rejects_limits_outside_zero_to_one_hundred()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscountPolicy(-0.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscountPolicy(100.01m));
    }
}
