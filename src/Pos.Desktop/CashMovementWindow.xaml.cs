using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CashMovementWindow : Window
{
    public CashMovementInput? Movement { get; private set; }
    public CashMovementWindow(string currency)
    {
        InitializeComponent();
        CurrencyLabel.Text = "Registra la moneda y el motivo del movimiento.";
        CurrencyBox.SelectedIndex = currency == "USD" ? 1 : 0;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(AmountBox.Text, out var amount) || amount <= 0m)
        { ErrorLabel.Text = "El monto debe ser mayor que cero."; return; }
        var reason = ReasonBox.Text.Trim();
        if (reason.Length is < 3 or > 250) { ErrorLabel.Text = "Escribe un motivo de 3 a 250 caracteres."; return; }
        var direction = (DirectionBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "Entrada" ? "In" : "Out";
        var currency = (CurrencyBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "VES";
        Movement = new CashMovementInput(amount, direction, reason, currency); DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
