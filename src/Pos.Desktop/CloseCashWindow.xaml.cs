using System.Globalization;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CloseCashWindow : Window
{
    public decimal CountedUsd { get; private set; }
    public decimal CountedVes { get; private set; }
    public CloseCashWindow(string currency)
    {
        InitializeComponent();
    }
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(CountedUsdBox.Text, out var usd) || usd < 0m || !PosNumberFormat.TryParseAmount(CountedVesBox.Text, out var ves) || ves < 0m)
        { ErrorLabel.Text = "Escribe montos válidos, iguales o mayores que cero, para ambas monedas."; return; }
        CountedUsd = usd; CountedVes = ves; DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
