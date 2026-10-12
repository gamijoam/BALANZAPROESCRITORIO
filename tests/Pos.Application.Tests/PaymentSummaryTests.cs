using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

/// <summary>
/// Criterios de aceptación del cobro:
/// - solo se admite una diferencia de conversión dentro del límite de precisión monetaria;
/// - el ajuste se informa, los faltantes mayores se completan y los excedentes mayores se corrigen;
/// - el monto pendiente se muestra también en la moneda alternativa al cambio vigente;
/// - sin tasa vigente, el equivalente USD no se inventa y queda no disponible.
/// </summary>
public sealed class PaymentSummaryTests
{
    [Fact]
    public void Currency_rounding_within_half_a_cent_is_accepted_and_recorded_as_adjustment()
    {
        var rows = new[] { new PaymentRow("Efectivo", "27,40", "USD", 36.50m) };

        var summary = PaymentSummary.Calculate(1_000m, "VES", rows, 36.50m);

        Assert.Equal(1_000.10m, summary.Paid);
        Assert.Equal(-0.10m, summary.RoundingAdjustment);
        Assert.Equal(0m, summary.Remaining);
        Assert.True(summary.CanConfirm);
        Assert.False(summary.CanAddPayment); // 27,40 USD ya supera el total al convertir.
        Assert.Contains("redondeo", summary.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0,10", summary.Guidance);
    }

    [Fact]
    public void Underpayment_in_sale_currency_explains_missing_amount_and_allows_another_payment()
    {
        var rows = new[] { new PaymentRow("Efectivo", "900,00", "VES", 36.50m) };

        var summary = PaymentSummary.Calculate(1_000m, "VES", rows, 36.50m);

        Assert.Equal(900m, summary.Paid);
        Assert.False(summary.CanConfirm);
        Assert.True(summary.CanAddPayment);
        Assert.Contains("agrega otro pago", summary.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Incomplete_row_does_not_erase_valid_paid_amount_or_remaining_balance()
    {
        var rows = new[]
        {
            new PaymentRow("Punto de venta", "5.000,00", "VES", 1_000m),
            new PaymentRow("Efectivo", "", "USD", 1_000m)
        };

        var summary = PaymentSummary.Calculate(10_000m, "VES", rows, 1_000m);

        Assert.Equal(5_000m, summary.Paid);
        Assert.Equal(5_000m, summary.Remaining);
        Assert.Equal(5m, summary.RemainingOtherCurrencyEquivalent);
        Assert.False(summary.CanConfirm);
        Assert.False(summary.CanAddPayment);
        Assert.Contains("completa o elimina", summary.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exact_payment_can_confirm_but_cannot_add_another_row()
    {
        var rows = new[] { new PaymentRow("Efectivo", "1.000,00", "VES", 36.50m) };

        var summary = PaymentSummary.Calculate(1_000m, "VES", rows, 36.50m);

        Assert.True(summary.CanConfirm);
        Assert.False(summary.CanAddPayment);
        Assert.Equal(string.Empty, summary.Guidance);
    }

    [Fact]
    public void Excess_payment_cannot_confirm_or_add_and_explains_reduction()
    {
        var rows = new[] { new PaymentRow("Efectivo", "1.100,00", "VES", 36.50m) };

        var summary = PaymentSummary.Calculate(1_000m, "VES", rows, 36.50m);

        Assert.False(summary.CanConfirm);
        Assert.False(summary.CanAddPayment);
        Assert.Contains("superan", summary.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reduce o ajusta", summary.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exact_mixed_currency_payment_can_confirm()
    {
        var rows = new[]
        {
            new PaymentRow("Efectivo", "500,00", "VES", 100m),
            new PaymentRow("Transferencia", "5,00", "USD", 100m)
        };

        var summary = PaymentSummary.Calculate(1_000m, "VES", rows, 100m);

        Assert.Equal(1_000m, summary.Paid);
        Assert.True(summary.CanConfirm);
        Assert.False(summary.CanAddPayment);
    }

    [Fact]
    public void Exact_cash_plus_credit_payment_can_confirm_for_customer_sale()
    {
        var rows = new[]
        {
            new PaymentRow("Efectivo", "15.000,00", "VES", 3_000m),
            new PaymentRow("Crédito", "15.000,00", "VES", 3_000m)
        };

        var summary = PaymentSummary.Calculate(30_000m, "VES", rows, 3_000m);

        Assert.Equal(30_000m, summary.Paid);
        Assert.Equal(0m, summary.Remaining);
        Assert.True(summary.CanConfirm);
    }

    [Fact]
    public void VES_sale_shows_running_paid_and_remaining_as_USD_equivalents()
    {
        var rows = new[]
        {
            new PaymentRow("Efectivo", "2.000,00", "VES", 900m),
            new PaymentRow("Pago móvil", "5.000,00", "VES", 900m)
        };

        var summary = PaymentSummary.Calculate(9_000m, "VES", rows, 900m);

        Assert.Equal(7_000m, summary.Paid);
        Assert.Equal(2_000m, summary.Remaining);
        Assert.Equal(2.22m, summary.RemainingOtherCurrencyEquivalent);
    }

    [Fact]
    public void VES_sale_without_rate_keeps_USD_equivalents_unavailable()
    {
        var rows = new[] { new PaymentRow("Efectivo", "2.000,00", "VES") };

        var summary = PaymentSummary.Calculate(9_000m, "VES", rows, null);

        Assert.Null(summary.RemainingOtherCurrencyEquivalent);
    }

    [Fact]
    public void USD_sale_shows_pending_balance_in_VES_at_the_current_rate()
    {
        var rows = new[] { new PaymentRow("Transferencia", "5.000,00", "USD", 1_000m) };

        var summary = PaymentSummary.Calculate(10_000m, "USD", rows, 1_000m);

        Assert.Equal(5_000m, summary.Paid);
        Assert.Equal(5_000m, summary.Remaining);
        Assert.Equal(5_000_000m, summary.RemainingOtherCurrencyEquivalent);
    }
}
