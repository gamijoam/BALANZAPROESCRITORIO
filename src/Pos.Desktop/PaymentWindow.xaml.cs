using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class PaymentWindow : Window
{
    public static IReadOnlyList<string> PaymentMethods { get; } = ["Efectivo", "Punto de venta", "Transferencia", "Pago móvil", "Fiado"];
    public static IReadOnlyList<string> Currencies { get; } = ["VES", "USD"];
    private readonly decimal _total;
    private readonly string _currency;
    private readonly decimal? _usdToVes;
    private readonly ObservableCollection<PaymentRow> _rows = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public IReadOnlyList<SalePaymentInput> Payments { get; private set; } = [];

    public PaymentWindow(decimal total, string currency, IReadOnlyList<SalePaymentInput>? fixedPayments = null, decimal? usdToVes = null)
    {
        InitializeComponent();
        _total = total;
        _currency = currency;
        _usdToVes = usdToVes;
        TotalLabel.Text = $"{currency} {total:N2}";
        var isRetry = fixedPayments is { Count: > 0 };
        if (isRetry)
        {
            PaymentTitle.Text = "Pagos de la venta pendiente";
            AddPaymentButton.IsEnabled = false;
            foreach (var payment in fixedPayments!) _rows.Add(new PaymentRow(payment.Method, PosNumberFormat.FormatAmount(payment.Amount), payment.Currency));
        }
        else _rows.Add(new PaymentRow("Efectivo", PosNumberFormat.FormatAmount(total), currency));
        PaymentsGrid.ItemsSource = _rows;
        PaymentsGrid.IsReadOnly = isRetry;
        ConfirmButton.Content = isRetry ? "Reintentar venta" : "Confirmar venta";
        _refresh.Tick += (_, _) => RefreshTotals();
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();
        RefreshTotals();
    }

    private void AddPayment_Click(object sender, RoutedEventArgs e)
    {
        var pending = Math.Max(0, _total - CurrentPaidInSaleCurrency());
        _rows.Add(new PaymentRow("Punto de venta", PosNumberFormat.FormatAmount(pending), _currency));
    }

    private void RemovePayment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PaymentRow row && _rows.Count > 1) _rows.Remove(row);
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
        if (payments.Any(x => x.Currency != _currency) && _usdToVes is null)
        {
            ErrorLabel.Text = "Registra una tasa vigente para cobrar en dos monedas.";
            return;
        }
        var converted = payments.Sum(x => x.Currency == _currency ? x.Amount : decimal.Round(x.Currency == "USD" ? x.Amount * _usdToVes!.Value : x.Amount / _usdToVes!.Value, 2, MidpointRounding.AwayFromZero));
        if (converted != _total)
        {
            ErrorLabel.Text = $"Los pagos convertidos deben sumar {_currency} {PosNumberFormat.FormatAmount(_total)}.";
            return;
        }
        Payments = payments;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private decimal CurrentPaid() => _rows.Sum(x => x.TryGetAmount(out var amount) ? amount : 0m);
    private decimal CurrentPaidInSaleCurrency() => _rows.Sum(x => x.TryGetAmount(out var amount) ? x.Currency == _currency ? amount : _usdToVes is null ? 0m : decimal.Round(x.Currency == "USD" ? amount * _usdToVes.Value : amount / _usdToVes.Value, 2, MidpointRounding.AwayFromZero) : 0m);

    private void RefreshTotals()
    {
        var paid = CurrentPaidInSaleCurrency();
        PaidLabel.Text = $"{_currency} {paid:N2}";
        DueLabel.Text = $"{_currency} {_total - paid:N2}";
        ConfirmButton.IsEnabled = paid == _total && _rows.Count > 0;
    }

    public sealed class PaymentRow(string method, string amountText, string currency)
    {
        public string Method { get; set; } = method;
        public string AmountText { get; set; } = amountText;
        public string Currency { get; set; } = currency;
        public bool TryGetAmount(out decimal amount) => PosNumberFormat.TryParseAmount(AmountText, out amount);
    }
}
