using System.Windows;
using System.Windows.Input;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ProductSelectionWindow : Window
{
    public ProductSelectionWindow(IReadOnlyList<ProductInfo> products, string searchText)
    {
        InitializeComponent();
        DataContext = products;
        TitleLabel.Text = products.Count == 1
            ? $"1 producto encontrado para «{searchText}»"
            : $"{products.Count} productos encontrados para «{searchText}»";
    }

    public ProductInfo? SelectedProduct => ProductsList.SelectedItem as ProductInfo;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ProductsList.Items.Count == 0) return;
        ProductsList.SelectedIndex = 0;
        ProductsList.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AcceptSelection();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void Select_Click(object sender, RoutedEventArgs e) => AcceptSelection();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void AcceptSelection()
    {
        if (SelectedProduct is null) return;
        DialogResult = true;
    }
}
