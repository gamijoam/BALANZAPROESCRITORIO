using System.Windows;

namespace Pos.Desktop;

public partial class ProductUnitCreateWindow : Window
{
    public string UnitName => NameBox.Text.Trim();
    public string Abbreviation => AbbreviationBox.Text.Trim();
    public bool AllowsFractional => FractionalBox.IsChecked == true;

    public ProductUnitCreateWindow() => InitializeComponent();

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (UnitName.Length is < 2 or > 48 || Abbreviation.Length is < 1 or > 12)
        {
            ErrorLabel.Text = "El nombre debe tener entre 2 y 48 caracteres y la abreviatura entre 1 y 12.";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
