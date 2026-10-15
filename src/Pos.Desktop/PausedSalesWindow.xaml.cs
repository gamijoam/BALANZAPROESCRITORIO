using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Desktop;

public partial class PausedSalesWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly ObservableCollection<PausedSaleRow> _rows = [];

    public PausedSalesWindow(PosViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        PausedSalesGrid.ItemsSource = _rows;
    }

    private PausedSaleRow? SelectedRow => PausedSalesGrid.SelectedItem as PausedSaleRow;

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        SetBusy(true);
        try
        {
            var selectedId = SelectedRow?.Workspace.Id;
            var workspaces = await _viewModel.GetPausedSalesAsync();
            _rows.Clear();
            foreach (var workspace in workspaces)
                _rows.Add(new PausedSaleRow(workspace));
            EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selectedId.HasValue)
                PausedSalesGrid.SelectedItem = _rows.FirstOrDefault(x => x.Workspace.Id == selectedId.Value);
            StatusText.Text = _rows.Count == 0 ? "" : $"{_rows.Count} venta{(_rows.Count == 1 ? "" : "s")} pausada{(_rows.Count == 1 ? "" : "s")} en esta terminal.";
            UpdateSelectionDetails();
        }
        catch (Exception ex)
        {
            StatusText.Text = PosUserMessage.ForException(ex);
            MessageBox.Show(this, StatusText.Text, "No se pudieron consultar las ventas pausadas", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private void UpdateSelectionDetails()
    {
        var selected = SelectedRow;
        ResumeButton.IsEnabled = selected is not null;
        DiscardButton.IsEnabled = selected is not null;
        if (selected is null)
        {
            DetailsText.Text = "Selecciona una venta para ver sus artículos, nota y cambios detectados.";
            return;
        }

        var workspace = selected.Workspace;
        var lines = string.Join(Environment.NewLine, workspace.Lines.Select(line =>
            $"• {line.Name} ({line.Sku}) · {line.Quantity:0.###} × {line.SavedCurrency} {line.SavedUnitPrice:N2}"));
        var note = string.IsNullOrWhiteSpace(workspace.Note) ? "Sin nota" : workspace.Note;
        DetailsText.Text = $"Cliente: {selected.CustomerName}    Operador: {workspace.OperatorName}{Environment.NewLine}" +
            $"Nota: {note}{Environment.NewLine}" +
            $"Artículos:{Environment.NewLine}{lines}{Environment.NewLine}{Environment.NewLine}" +
            _viewModel.BuildPausedSaleReview(workspace);
    }

    private void PausedSalesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionDetails();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRow;
        if (selected is null) return;
        SetBusy(true);
        try
        {
            var preview = await _viewModel.GetPausedSaleAsync(selected.Workspace.Id);
            var review = _viewModel.BuildPausedSaleReview(preview);
            var answer = MessageBox.Show(this,
                $"Se volverá a cargar esta venta con los precios, impuestos, moneda y existencias que informa ahora el servidor.{Environment.NewLine}{Environment.NewLine}" +
                $"{review}{Environment.NewLine}{Environment.NewLine}¿Deseas retomar la venta?",
                "Revisar antes de retomar", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                await _viewModel.ResumePausedSaleAsync(preview, acceptCurrentValues: true);
                DialogResult = true;
            }
            catch (PosApiException ex) when (ex.Code == "cart_values_changed")
            {
                await RefreshAsync();
                MessageBox.Show(this, "Los valores cambiaron mientras confirmabas. La venta sigue pausada; revisa el detalle actualizado y vuelve a confirmar.",
                    "Cambió el precio o la tasa", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (_viewModel.CartLines.Count > 0)
            {
                MessageBox.Show(this,
                    $"La venta pasó a ser el carrito activo y permanece visible en el POS, pero el último guardado necesita reintentarse.{Environment.NewLine}{Environment.NewLine}{PosUserMessage.ForException(ex)}",
                    "Venta retomada con guardado pendiente", MessageBoxButton.OK, MessageBoxImage.Warning);
                DialogResult = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo retomar la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRow;
        if (selected is null) return;
        var answer = MessageBox.Show(this,
            $"¿Descartar la venta de {selected.CustomerName}, por {selected.TotalDisplay}? Esta acción quedará registrada en auditoría.",
            "Confirmar descarte", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            await _viewModel.DiscardPausedSaleAsync(selected.Workspace.Id, selected.Workspace.Version);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, PosUserMessage.ForException(ex), "No se pudo descartar la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        PausedSalesGrid.IsEnabled = !busy;
        if (busy)
        {
            ResumeButton.IsEnabled = false;
            DiscardButton.IsEnabled = false;
        }
        else UpdateSelectionDetails();
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private sealed class PausedSaleRow
    {
        public PausedSaleRow(CartWorkspaceSnapshot workspace) => Workspace = workspace;
        public CartWorkspaceSnapshot Workspace { get; }
        public string PausedAtDisplay => Workspace.UpdatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        public string OperatorName => Workspace.OperatorName;
        public string CustomerName => Workspace.Customer?.Name ?? "Venta general";
        public string Summary => $"{Workspace.Lines.Count} renglón{(Workspace.Lines.Count == 1 ? "" : "es")} · {Workspace.Lines.Sum(x => x.Quantity):0.###} unidades";
        public string TotalDisplay => $"{Workspace.Currency} {Workspace.TotalAtPause:N2}";
    }
}
