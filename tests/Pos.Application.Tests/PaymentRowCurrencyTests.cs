using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

/// <summary>
/// Criterios de aceptación del cambio de moneda en pagos:
/// - convertir el importe automáticamente con la tasa USD/VES vigente;
/// - redondear a dos decimales con MidpointRounding.AwayFromZero;
/// - restaurar el importe al volver a la moneda original dentro de la precisión disponible;
/// - conservar la moneda e importe si no hay tasa válida;
/// - no permitir cambios de moneda en filas de reintento fijas.
/// </summary>
public sealed class PaymentRowCurrencyTests
{
    [Fact]
    public void Changing_from_ves_to_usd_converts_amount_using_current_rate()
    {
        var row = new PaymentRow("Efectivo", "1.000,00", "VES", 100m);

        row.Currency = "USD";

        Assert.Equal("10,00", row.AmountText);
        Assert.Equal("USD", row.Currency);
    }

    [Fact]
    public void Changing_back_to_original_currency_restores_amount()
    {
        var row = new PaymentRow("Efectivo", "1.000,00", "VES", 100m);

        row.Currency = "USD";
        row.Currency = "VES";

        Assert.Equal("1.000,00", row.AmountText);
        Assert.Equal("VES", row.Currency);
    }

    [Fact]
    public void VES_to_USD_conversion_can_leave_a_rounding_residual_against_sale_total()
    {
        var saleTotalVes = 1_000.00m;
        const decimal rate = 36.50m;
        var row = new PaymentRow("Efectivo", "1.000,00", "VES", rate);

        row.Currency = "USD";

        Assert.True(row.TryGetAmount(out var amountUsd));
        Assert.Equal(27.40m, amountUsd);
        var equivalentPaidVes = decimal.Round(amountUsd * rate, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(1_000.10m, equivalentPaidVes);
        Assert.NotEqual(saleTotalVes, equivalentPaidVes);
    }

    [Fact]
    public void Currency_change_rounds_to_two_decimals_away_from_zero()
    {
        var row = new PaymentRow("Efectivo", "0,01", "VES", 2m);

        row.Currency = "USD";

        Assert.Equal("0,01", row.AmountText);
    }

    [Fact]
    public void Changing_one_mixed_payment_keeps_other_rows_and_their_currencies()
    {
        var vesPayment = new PaymentRow("Efectivo", "500,00", "VES", 100m);
        var usdPayment = new PaymentRow("Transferencia", "5,00", "USD", 100m);

        vesPayment.Currency = "USD";

        Assert.Equal("5,00", vesPayment.AmountText);
        Assert.Equal("USD", vesPayment.Currency);
        Assert.Equal("5,00", usdPayment.AmountText);
        Assert.Equal("USD", usdPayment.Currency);
        Assert.True(usdPayment.TryGetAmount(out var usdAmount));
        Assert.Equal(1_000m, 500m + usdAmount * 100m);
    }

    [Fact]
    public void Invalid_rate_or_amount_keeps_original_row_values()
    {
        var noRate = new PaymentRow("Efectivo", "100,00", "VES", null);
        noRate.Currency = "USD";
        var invalidRate = new PaymentRow("Efectivo", "100,00", "VES", 0m);
        invalidRate.Currency = "USD";
        var invalidAmount = new PaymentRow("Efectivo", "texto", "VES", 100m);
        invalidAmount.Currency = "USD";

        Assert.Equal(("VES", "100,00"), (noRate.Currency, noRate.AmountText));
        Assert.Equal(("VES", "100,00"), (invalidRate.Currency, invalidRate.AmountText));
        Assert.Equal(("VES", "texto"), (invalidAmount.Currency, invalidAmount.AmountText));
    }

    [Fact]
    public void Fixed_retry_payment_does_not_change_currency_or_amount()
    {
        var row = new PaymentRow("Efectivo", "10,00", "USD", 100m, isFixed: true);

        row.Currency = "VES";

        Assert.Equal("USD", row.Currency);
        Assert.Equal("10,00", row.AmountText);
    }

    [Fact]
    public void Payment_row_exposes_its_USD_equivalent_for_live_display()
    {
        var row = new PaymentRow("Efectivo", "2.000,00", "VES", 900m);

        Assert.Equal("2,22", row.EquivalentUsdText);

        row.AmountText = "5.000,00";
        Assert.Equal("5,56", row.EquivalentUsdText);
        row.Currency = "USD";
        Assert.Equal("5,56", row.AmountText);
        Assert.Equal("5,56", row.EquivalentUsdText);
    }

    [Fact]
    public void Payment_row_shows_dash_when_USD_equivalent_cannot_be_calculated()
    {
        var row = new PaymentRow("Efectivo", "2.000,00", "VES");

        Assert.Equal("—", row.EquivalentUsdText);
    }
}
