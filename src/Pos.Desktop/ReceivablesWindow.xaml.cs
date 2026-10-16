using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ReceivablesWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly CustomerInfo _customer;
    private ReceivableInfo? _account;

    public ReceivablesWindow(PosViewModel viewModel, CustomerInfo customer)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1480, workArea.Width * 0.96);
        MinWidth = Math.Min(1040, Width);
        _viewModel = viewModel;
        _customer = customer;
        CustomerTitle.Text = customer.Name;
        AmountFieldLabel.Text = "Monto recibido";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _account = await _viewModel.GetReceivablesAsync(_customer.Id);
            BalanceLabel.Text = FormatMoney("USD", _account.Balance);
            BalanceVesLabel.Text = _account.BalanceVesEquivalent is decimal vesBalance && _account.CurrentUsdToVesRate is decimal rate
                ? $"Equivalente en VES: {FormatMoney("VES", vesBalance)} · Tasa {PosNumberFormat.FormatAmount(rate)}"
                : "Equivalente en VES: tasa no disponible";
            CreditLimitLabel.Text = FormatMoney("USD", _account.CreditLimit);
            AvailableCreditLabel.Text = FormatMoney("USD", Math.Max(0m, _account.CreditLimit - _account.Balance));
            EntriesGrid.ItemsSource = CreateRows(_account);
            RecordPaymentButton.IsEnabled = _account.Balance > 0m;
            OpeningBalanceButton.IsEnabled = _account.Entries.Count == 0;
            RefreshPaymentPreview();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private static IReadOnlyList<ReceivableMovementRow> CreateRows(ReceivableInfo account)
    {
        var balance = 0m;
        var rows = new List<ReceivableMovementRow>();
        foreach (var entry in account.Entries.OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id))
        {
            var isCharge = string.Equals(entry.EntryType, "Charge", StringComparison.OrdinalIgnoreCase);
            balance += isCharge ? entry.Amount : -entry.Amount;
            var reference = entry.IsOpeningBalance ? entry.Reason : entry.SaleId is Guid saleId
                ? $"Venta {saleId.ToString("N")[..8].ToUpperInvariant()}"
                : isCharge ? "Cargo de crédito" : string.IsNullOrWhiteSpace(entry.Reason) ? "—" : entry.Reason;
            rows.Add(new ReceivableMovementRow(
                entry.OccurredAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                entry.IsOpeningBalance ? "Saldo inicial" : isCharge ? "Venta a crédito" : "Abono",
                reference,
                isCharge && entry.DueDate is DateOnly dueDate ? dueDate.ToString("dd/MM/yyyy") : "—",
                isCharge ? ReceivableSaleItemPresentation.Format(entry.SaleItems) : "—",
                FormatMovementAmount(entry),
                FormatMoney("USD", balance)));
        }
        return rows.AsEnumerable().Reverse().ToArray();
    }

    private static string FormatMoney(string currency, decimal amount) => $"{currency} {PosNumberFormat.FormatAmount(amount)}";

    private static string FormatMovementAmount(ReceivableEntryInfo entry)
    {
        var originalAmount = entry.OriginalAmount ?? entry.Amount;
        var originalCurrency = entry.OriginalCurrency ?? "USD";
        return originalCurrency == "USD" ? FormatMoney("USD", entry.Amount)
            : $"{FormatMoney(originalCurrency, originalAmount)} → {FormatMoney("USD", entry.Amount)}";
    }

    private void AmountBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshPaymentPreview();
    private void PaymentCurrencyBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshPaymentPreview();

    private void RefreshPaymentPreview()
    {
        if (PaymentPreviewLabel is null) return;
        var currency = (PaymentCurrencyBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "USD";
        if (!PosNumberFormat.TryParseAmount(AmountBox?.Text ?? string.Empty, out var amount) || amount <= 0m)
        { PaymentPreviewLabel.Text = "Indica el monto que recibiste."; return; }
        if (currency == "USD")
        { PaymentPreviewLabel.Text = $"Se descontarán {FormatMoney("USD", amount)} de la deuda."; return; }
        if (_account?.CurrentUsdToVesRate is not decimal rate || rate <= 0m)
        { PaymentPreviewLabel.Text = "No hay una tasa vigente para convertir este abono."; return; }
        var amountUsd = decimal.Round(amount / rate, 2, MidpointRounding.AwayFromZero);
        PaymentPreviewLabel.Text = $"Equivale a {FormatMoney("USD", amountUsd)} al cambio actual.";
    }

    private async void Payment_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) { StatusLabel.Text = "No se pudo cargar la cuenta del cliente."; return; }
        if (!PosNumberFormat.TryParseAmount(AmountBox.Text, out var amount) || amount <= 0m || decimal.Round(amount, 2) != amount)
        { StatusLabel.Text = "El abono debe ser positivo y tener máximo dos decimales."; AmountBox.Focus(); return; }
        var currency = (PaymentCurrencyBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "USD";
        if (currency == "VES" && _account.CurrentUsdToVesRate is null)
        { StatusLabel.Text = "No hay una tasa vigente para convertir el abono en bolívares."; return; }
        var amountUsd = currency == "USD" ? amount : decimal.Round(amount / _account.CurrentUsdToVesRate!.Value, 2, MidpointRounding.AwayFromZero);
        if (amountUsd <= 0m)
        { StatusLabel.Text = "El abono es menor al mínimo equivalente de USD 0,01."; AmountBox.Focus(); return; }
        if (amountUsd > _account.Balance)
        { StatusLabel.Text = $"El abono equivale a {FormatMoney("USD", amountUsd)} y supera el saldo pendiente de {FormatMoney("USD", _account.Balance)}."; AmountBox.Focus(); return; }
        var reference = ReferenceBox.Text.Trim();

        RecordPaymentButton.IsEnabled = false;
        try
        {
            await _viewModel.RecordReceivablePaymentAsync(_customer.Id, amount, currency, reference);
            AmountBox.Clear(); ReferenceBox.Clear(); StatusLabel.Text = "Abono registrado en la cuenta del cliente.";
            await LoadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); RecordPaymentButton.IsEnabled = _account.Balance > 0m; }
    }

    private async void OpeningBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null || _account.Entries.Count != 0)
        { StatusLabel.Text = "El saldo inicial solo se registra en una cuenta sin movimientos."; return; }
        var dialog = new OpeningBalanceWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        OpeningBalanceButton.IsEnabled = false;
        try
        {
            var idempotencyKey = $"opening-balance:{_customer.Id:N}:{Guid.NewGuid():N}";
            await _viewModel.RecordReceivableOpeningBalanceAsync(_customer.Id, dialog.AmountUsd, dialog.Reason, idempotencyKey);
            StatusLabel.Text = "Saldo inicial registrado en la cuenta del cliente.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = PosUserMessage.ForException(ex);
            OpeningBalanceButton.IsEnabled = _account?.Entries.Count == 0;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record ReceivableMovementRow(string DateLabel, string MovementLabel, string ReferenceLabel,
    string DueDateLabel, string SaleItemsLabel, string AmountLabel, string ResultingBalanceLabel);
