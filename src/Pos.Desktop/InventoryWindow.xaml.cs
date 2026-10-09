using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class InventoryWindow : Window
{
    private readonly PosViewModel _viewModel;
    public InventoryWindow(PosViewModel viewModel) { InitializeComponent(); _viewModel = viewModel; Loaded += async (_, _) => await LoadAsync(); }

    private async Task LoadAsync()
    {
        try
        {
            var items = await _viewModel.GetInventoryAsync();
            InventoryGrid.ItemsSource = items;
            InventorySummary.Text = $"{items.Count} productos activos.";
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        if (new ProductCreateWindow(_viewModel) { Owner = this }.ShowDialog() == true) await LoadAsync();
    }

    private async void ProductImage_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        var picker = new OpenFileDialog { Title = $"Foto de {item.Name}", Filter = "Imágenes JPG, PNG o WebP|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try { await _viewModel.UploadProductImageAsync(item.Id, picker.FileName); StatusLabel.Text = "Foto guardada en el producto."; await LoadAsync(); }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void ChangePrice_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        if (new ProductPriceWindow(_viewModel, item) { Owner = this }.ShowDialog() == true)
        { StatusLabel.Text = "Precio actualizado."; await LoadAsync(); }
    }

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        if (!PosNumberFormat.TryParseAmount(DeltaBox.Text, out var delta) || delta == 0m || decimal.Round(delta, 3) != delta)
        { StatusLabel.Text = "El ajuste debe ser distinto de cero y tener hasta tres decimales."; return; }
        try
        {
            var quantity = await _viewModel.AdjustStockAsync(item.Id, delta, ReasonBox.Text.Trim());
            StatusLabel.Text = $"Existencia actualizada: {PosNumberFormat.FormatQuantity(quantity)}.";
            ReasonBox.Clear(); await LoadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (InventoryGrid.SelectedItem is not InventoryItemInfo item) { StatusLabel.Text = "Selecciona un producto."; return; }
        try
        {
            var movements = await _viewModel.GetStockMovementsAsync(item.Id);
            var text = movements.Count == 0 ? "Sin movimientos." : string.Join(Environment.NewLine, movements.Select(x => $"{x.OccurredAtUtc:dd/MM/yyyy HH:mm}  {x.QuantityDelta:+0.###;-0.###}  {x.Reason}"));
            MessageBox.Show(this, text, $"Historial · {item.Sku}", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { StatusLabel.Text = PosUserMessage.ForException(ex); }
    }
}
