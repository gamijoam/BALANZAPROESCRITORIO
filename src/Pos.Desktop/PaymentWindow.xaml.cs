using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class PaymentWindow : Window
{
    public static IReadOnlyList<string> PaymentMethods { get; } = ["Efectivo", "Punto de venta", "Transferencia", "Pago móvil", "Crédito"];
    public static IReadOnlyList<string> Currencies { get; } = ["VES", "USD"];
    private readonly decimal _total;
    private readonly string _currency;
    private readonly decimal? _usdToVes;
    private readonly bool _isRetry;
    private readonly ObservableCollection<PaymentRow> _rows = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public IReadOnlyList<SalePaymentInput> Payments { get; private set; } = [];
    public DateOnly? ReceivableDueDate { get; private set; }

    public PaymentWindow(decimal total, string currency, IReadOnlyList<SalePaymentInput>? fixedPayments = null, decimal? usdToVes = null,
        DateOnly? fixedReceivableDueDate = null)
    {
        InitializeComponent();
        _total = total;
        _currency = currency;
        _usdToVes = usdToVes;
        TotalLabel.Text = $"{currency} {total:N2}";
        TotalOtherCurrencyTitle.Text = $"EQUIVALENTE {OtherCurrency(currency)}";
        TotalOtherCurrencyLabel.Text = FormatOtherCurrencyEquivalent(total, currency, usdToVes);
        _isRetry = fixedPayments is { Count: > 0 };
        _rows.CollectionChanged += (_, _) =>
        {
            PaymentsPlaceholder.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshDueDateVisibility();
        };
        if (_isRetry)
        {
            PaymentTitle.Text = "Pagos de la venta pendiente";
            PaymentMethodsPanel.IsEnabled = false;
            foreach (var payment in fixedPayments!) _rows.Add(new PaymentRow(payment.Method, PosNumberFormat.FormatAmount(payment.Amount), payment.Currency, _usdToVes, isFixed: true));
        }
        PaymentsGrid.ItemsSource = _rows;
        PaymentsGrid.IsReadOnly = _isRetry;
        ReceivableDueDatePicker.SelectedDate = (fixedReceivableDueDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(30))).ToDateTime(TimeOnly.MinValue);
        ReceivableDueDatePicker.IsEnabled = !_isRetry;
        ConfirmButton.Content = _isRetry ? "Reintentar venta" : "Confirmar venta";
        _refresh.Tick += (_, _) => RefreshTotals();
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();
        RefreshTotals();
        RefreshDueDateVisibility();
    }

    private void PaymentMethod_Click(object sender, RoutedEventArgs e)
    {
        if (_isRetry || (sender as FrameworkElement)?.Tag?.ToString() is not { } method ||
            !PaymentMethods.Contains(method, StringComparer.Ordinal)) return;
        AddPayment(method);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isRetry) PaymentsGrid.Focus();
        else CashMethodButton.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;

        // Enter in an edited payment cell commits the amount first. If the
        // payment now covers the sale, it also confirms it; otherwise the
        // dialog stays open and keeps the pending balance visible.
        if (pressedKey == Key.Enter && modifiers == ModifierKeys.None &&
            PaymentsGrid.IsKeyboardFocusWithin && Keyboard.FocusedElement is not Button &&
            (Keyboard.FocusedElement is not ComboBox comboBox || !comboBox.IsDropDownOpen))
        {
            e.Handled = true;
            PaymentsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            PaymentsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            RefreshTotals();
            if (ConfirmButton.IsEnabled)
                Confirm_Click(ConfirmButton, new RoutedEventArgs(Button.ClickEvent, ConfirmButton));
            return;
        }

        if (_isRetry || (modifiers != ModifierKeys.Control && modifiers != ModifierKeys.Alt)) return;
        var method = pressedKey switch
        {
            Key.D1 or Key.NumPad1 => PaymentMethods[0],
            Key.D2 or Key.NumPad2 => PaymentMethods[1],
            Key.D3 or Key.NumPad3 => PaymentMethods[2],
            Key.D4 or Key.NumPad4 => PaymentMethods[3],
            Key.D5 or Key.NumPad5 => PaymentMethods[4],
            _ => null
        };
        if (method is null) return;
        e.Handled = true;
        AddPayment(method);
    }

    private void AddPayment(string method)
    {
        if (_isRetry || !PaymentMethods.Contains(method, StringComparer.Ordinal)) return;
        var pending = Math.Max(0, _total - CurrentPaidInSaleCurrency());
        var row = new PaymentRow(method, PosNumberFormat.FormatAmount(pending), _currency, _usdToVes);
        _rows.Add(row);
        PaymentsGrid.SelectedItem = row;
        PaymentsGrid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_rows.Contains(row) || PaymentsGrid.Columns.Count < 3) return;
            PaymentsGrid.CurrentCell = new DataGridCellInfo(row, PaymentsGrid.Columns[2]);
            PaymentsGrid.BeginEdit();
        }));
        RefreshTotals();
    }

    private void RemovePayment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PaymentRow row) _rows.Remove(row);
        RefreshTotals();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        PaymentsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        PaymentsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var payments = new List<SalePaymentInput>();
        foreach (var row in _rows)
        {
            if (!row.TryGetAmount(out var amount) || amount <= 0)
            {
                ErrorLabel.Text = "Cada pago debe tener un monto mayor que cero.";
                return;
            }
            payments.Add(new SalePaymentInput(row.Method, amount, row.Currency));
        }
        var summary = PaymentSummary.Calculate(_total, _currency, _rows, _usdToVes);
        if (!summary.CanConfirm)
        {
            ErrorLabel.Text = summary.Guidance;
            return;
        }
        var hasCredit = payments.Any(payment => IsCreditMethod(payment.Method));
        if (hasCredit && ReceivableDueDatePicker.SelectedDate is null)
        {
            ErrorLabel.Text = "Indica la fecha de vencimiento del crédito.";
            return;
        }
        ReceivableDueDate = hasCredit ? DateOnly.FromDateTime(ReceivableDueDatePicker.SelectedDate!.Value) : null;
        Payments = payments;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private decimal CurrentPaidInSaleCurrency() => PaymentSummary.Calculate(_total, _currency, _rows, _usdToVes).Paid;

    private void RefreshDueDateVisibility()
    {
        ReceivableDueDatePanel.Visibility = _rows.Any(row => IsCreditMethod(row.Method)) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsCreditMethod(string method) => string.Equals(method, "Crédito", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method, "credit", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "fiado", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method, "credito", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "crédito", StringComparison.OrdinalIgnoreCase);

    private void RefreshTotals()
    {
        var summary = PaymentSummary.Calculate(_total, _currency, _rows, _usdToVes);
        DueLabel.Text = $"{_currency} {PosNumberFormat.FormatAmount(summary.Remaining)}";
        OtherCurrencyDueTitle.Text = $"EQUIVALENTE {OtherCurrency(_currency)}";
        OtherCurrencyDueLabel.Text = FormatOtherCurrencyEquivalent(summary.RemainingOtherCurrencyEquivalent, _currency);
        ErrorLabel.Text = _rows.Count == 0 ? "Selecciona un método de pago para continuar." : summary.Guidance;
        ErrorLabel.Foreground = summary.RoundingAdjustment != 0m
            ? new SolidColorBrush(Color.FromRgb(8, 103, 213))
            : Brushes.Firebrick;
        PaymentsPlaceholder.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_isRetry) PaymentMethodsPanel.IsEnabled = summary.CanAddPayment;
        ConfirmButton.IsEnabled = summary.CanConfirm;
    }

    private static string OtherCurrency(string currency) => currency == "USD" ? "VES" : "USD";

    private static string FormatOtherCurrencyEquivalent(decimal amount, string currency, decimal? usdToVes) =>
        FormatOtherCurrencyEquivalent(currency switch
        {
            "USD" when usdToVes is > 0m => decimal.Round(amount * usdToVes.Value, 2, MidpointRounding.AwayFromZero),
            "VES" when usdToVes is > 0m => decimal.Round(amount / usdToVes.Value, 2, MidpointRounding.AwayFromZero),
            _ => null
        }, currency);

    private static string FormatOtherCurrencyEquivalent(decimal? amount, string currency) =>
        amount is { } value ? $"{OtherCurrency(currency)} {PosNumberFormat.FormatAmount(value)}" : $"{OtherCurrency(currency)} —";
}
