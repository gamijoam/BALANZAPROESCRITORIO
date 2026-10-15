using System.Windows;

namespace Pos.Desktop;

public partial class PauseSaleWindow : Window
{
    public PauseSaleWindow(decimal quantity, decimal total, string currency)
    {
        InitializeComponent();
        SaleSummaryText.Text = $"{quantity:0.###} unidades · total actual {currency} {total:N2}";
    }

    public string? Note => string.IsNullOrWhiteSpace(NoteTextBox.Text) ? null : NoteTextBox.Text.Trim();

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
