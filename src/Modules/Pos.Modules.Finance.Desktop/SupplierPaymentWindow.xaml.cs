using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Pos.Modules.Finance.Desktop;

public partial class SupplierPaymentWindow : Window
{
    public SupplierPaymentWindow(Window owner, string supplierName, string documentLabel, string invoiceCurrency,
        decimal outstandingBalance, FinanceWindow.SupplierPaymentRequest? retryRequest = null, bool isImmediateSettlement = false)
    {
        InitializeComponent();
        Owner = owner;
        if (isImmediateSettlement)
        {
            Title = "Contabilizar factura de contado";
            PaymentHeading.Text = "PAGO DE CONTADO";
            PaymentIntro.Text = "La factura y el pago se registrarán juntos en una sola operación.";
            PaymentBalanceLabel.Text = "Total de la factura";
            PaymentAmountBox.Text = retryRequest?.Amount.ToString("N2", CultureInfo.CurrentCulture) ?? outstandingBalance.ToString("N2", CultureInfo.CurrentCulture);
            PaymentConversionHelp.Text = "El pago debe cubrir el total completo. Si eliges otra moneda, el servidor verificará la equivalencia con la tasa vigente. No se acepta pago parcial en este paso.";
            ConfirmPaymentButton.Content = "Contabilizar y pagar";
        }
        PaymentSupplierValue.Text = supplierName;
        PaymentDocumentValue.Text = documentLabel;
        PaymentBalanceValue.Text = $"{invoiceCurrency} {outstandingBalance:N2}";
        PaymentCurrencyPicker.SelectedIndex = (retryRequest?.Currency ?? invoiceCurrency) == "VES" ? 1 : 0;
        if (!isImmediateSettlement)
            PaymentAmountBox.Text = retryRequest?.Amount.ToString("N2", CultureInfo.CurrentCulture) ?? string.Empty;
        PaymentMethodBox.Text = retryRequest?.Method ?? "Transferencia";
        PaymentReceivingAccountBox.Text = retryRequest?.ReceivingAccount ?? string.Empty;
        PaymentReferenceBox.Text = retryRequest?.Reference ?? string.Empty;
        if (retryRequest is not null)
        {
            PaymentRetryHint.Text = isImmediateSettlement
                ? "Este pago quedó pendiente de confirmar. Conserva los mismos datos para reintentar con la misma clave y evitar duplicar el registro."
                : "Este abono quedó pendiente de confirmar. Si los datos siguen iguales, el reintento usará la misma clave para evitar duplicarlo.";
            PaymentRetryHint.Visibility = Visibility.Visible;
            ConfirmPaymentButton.Content = isImmediateSettlement ? "Reintentar contabilización" : "Reintentar el mismo abono";
        }
    }

    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string Method { get; private set; } = string.Empty;
    public string ReceivingAccount { get; private set; } = string.Empty;
    public string? Reference { get; private set; }

    private void ConfirmPayment_Click(object sender, RoutedEventArgs e)
    {
        PaymentErrorLabel.Visibility = Visibility.Collapsed;
        if (!TryDecimal(PaymentAmountBox.Text, out var amount) || amount <= 0m)
        {
            ShowError("Escribe un importe válido mayor que cero.", PaymentAmountBox);
            return;
        }
        if (PaymentCurrencyPicker.SelectedItem is not ComboBoxItem selectedCurrency)
        {
            ShowError("Selecciona USD o VES para este pago.", PaymentCurrencyPicker);
            return;
        }
        if (string.IsNullOrWhiteSpace(PaymentMethodBox.Text) || PaymentMethodBox.Text.Trim().Length > 32)
        {
            ShowError("Indica un medio de pago de hasta 32 caracteres.", PaymentMethodBox);
            return;
        }
        if (string.IsNullOrWhiteSpace(PaymentReceivingAccountBox.Text) || PaymentReceivingAccountBox.Text.Trim().Length > 100)
        {
            ShowError("Indica la cuenta receptora de hasta 100 caracteres.", PaymentReceivingAccountBox);
            return;
        }

        Amount = amount;
        Currency = selectedCurrency.Content?.ToString() ?? "USD";
        Method = PaymentMethodBox.Text.Trim();
        ReceivingAccount = PaymentReceivingAccountBox.Text.Trim();
        Reference = string.IsNullOrWhiteSpace(PaymentReferenceBox.Text) ? null : PaymentReferenceBox.Text.Trim();
        DialogResult = true;
    }

    private void ShowError(string message, Control control)
    {
        PaymentErrorLabel.Text = message;
        PaymentErrorLabel.Visibility = Visibility.Visible;
        control.Focus();
    }

    private static bool TryDecimal(string? text, out decimal amount)
    {
        const NumberStyles styles = NumberStyles.Number;
        return decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out amount) ||
            decimal.TryParse(text, styles, CultureInfo.GetCultureInfo("es-VE"), out amount) ||
            decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out amount);
    }
}
