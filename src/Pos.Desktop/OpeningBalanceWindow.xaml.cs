using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class OpeningBalanceWindow : Window
{
    public decimal AmountUsd { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    public OpeningBalanceWindow()
    {
        InitializeComponent();
        ReasonBox.Text = "Saldo inicial";
        Loaded += (_, _) => AmountBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(AmountBox.Text, out var amount) || amount <= 0m ||
            decimal.Round(amount, 4, MidpointRounding.AwayFromZero) != amount)
        {
            StatusLabel.Text = "Escribe un monto positivo con máximo cuatro decimales.";
            AmountBox.Focus();
            return;
        }

        var reason = ReasonBox.Text.Trim();
        if (reason.Length is < 3 or > 250)
        {
            StatusLabel.Text = "Escribe un motivo de al menos 3 caracteres.";
            ReasonBox.Focus();
            return;
        }

        AmountUsd = amount;
        Reason = reason;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
