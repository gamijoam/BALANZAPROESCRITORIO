using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class OpenCashWindow : Window
{
    public decimal OpeningBalanceUsd { get; private set; }
    public decimal OpeningBalanceVes { get; private set; }
    public string Currency { get; private set; } = "VES";

    public OpenCashWindow() => InitializeComponent();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(BalanceUsdBox.Text, out var usd) || usd < 0 ||
            !PosNumberFormat.TryParseAmount(BalanceVesBox.Text, out var ves) || ves < 0)
        {
            ErrorLabel.Text = "Escribe saldos válidos, iguales o mayores que cero.";
            return;
        }
        OpeningBalanceUsd = usd;
        OpeningBalanceVes = ves;
        Currency = "VES";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
