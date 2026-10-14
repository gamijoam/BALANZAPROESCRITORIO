using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductTaxTreatmentWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly InventoryItemInfo _product;

    public ProductTaxTreatmentWindow(PosViewModel viewModel, InventoryItemInfo product)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _product = product;
        ProductLabel.Text = $"{product.Sku} · {product.Name} · actual: {product.TaxTreatmentLabel}";
        if (product.TaxTreatment is "Taxable16" or "Taxable8" or "Taxable0" or "Exempt")
            TaxTreatmentBox.SelectedItem = TaxTreatmentBox.Items.Cast<System.Windows.Controls.ComboBoxItem>().First(x => Equals(x.Tag, product.TaxTreatment));
        else TaxTreatmentBox.SelectedIndex = 0;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorLabel.Text = string.Empty;
        if (TaxTreatmentBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item || item.Tag is not string code || string.IsNullOrWhiteSpace(code))
        {
            ErrorLabel.Text = "Selecciona una regla antes de guardar.";
            TaxTreatmentBox.Focus();
            return;
        }
        try
        {
            await _viewModel.SetProductTaxTreatmentAsync(_product.Id, code);
            DialogResult = true;
        }
        catch (Exception ex) { ErrorLabel.Text = PosUserMessage.ForException(ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
