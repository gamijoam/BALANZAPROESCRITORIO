using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class CloseCashWindow : Window
{
    private readonly decimal? _expectedUsd;
    private readonly decimal? _expectedVes;

    public decimal CountedUsd { get; private set; }
    public decimal CountedVes { get; private set; }

    public CloseCashWindow(decimal? expectedUsd, decimal? expectedVes)
    {
        InitializeComponent();
        _expectedUsd = expectedUsd;
        _expectedVes = expectedVes;
        ExpectedUsdLabel.Text = FormatExpected("USD", expectedUsd);
        ExpectedVesLabel.Text = FormatExpected("VES", expectedVes);
        CountedUsdBox.TextChanged += CountedAmount_TextChanged;
        CountedVesBox.TextChanged += CountedAmount_TextChanged;
        RefreshDifferences();
    }

    private void CountedAmount_TextChanged(object sender, TextChangedEventArgs e) => RefreshDifferences();

    private void RefreshDifferences()
    {
        UpdateDifference(CountedUsdBox.Text, _expectedUsd, DifferenceUsdLabel, "USD");
        UpdateDifference(CountedVesBox.Text, _expectedVes, DifferenceVesLabel, "VES");
    }

    private static string FormatExpected(string currency, decimal? expected) => expected is { } value
        ? $"{currency} {PosNumberFormat.FormatAmount(value)}"
        : "No disponible";

    private static void UpdateDifference(string text, decimal? expected, TextBlock target, string currency)
    {
        if (expected is null)
        {
            target.Text = "Diferencia no disponible";
            target.Foreground = new SolidColorBrush(Color.FromRgb(128, 86, 0));
            return;
        }
        if (!PosNumberFormat.TryParseAmount(text, out var counted) || counted < 0m)
        {
            target.Text = "Revisa el monto contado";
            target.Foreground = Brushes.Firebrick;
            return;
        }

        var difference = counted - expected.Value;
        target.Text = difference switch
        {
            > 0m => $"Sobrante: {currency} {PosNumberFormat.FormatAmount(difference)}",
            < 0m => $"Faltante: {currency} {PosNumberFormat.FormatAmount(decimal.Abs(difference))}",
            _ => "Sin diferencia"
        };
        target.Foreground = difference == 0m
            ? new SolidColorBrush(Color.FromRgb(21, 128, 61))
            : difference > 0m
                ? new SolidColorBrush(Color.FromRgb(128, 86, 0))
                : Brushes.Firebrick;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!PosNumberFormat.TryParseAmount(CountedUsdBox.Text, out var usd) || usd < 0m ||
            !PosNumberFormat.TryParseAmount(CountedVesBox.Text, out var ves) || ves < 0m)
        {
            ErrorLabel.Text = "Escribe montos válidos, iguales o mayores que cero, para ambas monedas.";
            return;
        }
        CountedUsd = usd;
        CountedVes = ves;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
