using System.Windows;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class InventoryMovementsWindow : Window
{
    private const int PageSize = 100;
    private readonly PosViewModel _viewModel;
    private readonly InventoryItemInfo _product;
    private readonly PosDesktopModuleContext? _moduleContext;
    private readonly IPosInventoryHistoryExtension? _historyExtension;
    private int _offset;
    private bool _hasMore;
    private bool _loading;
    private int _transferOffset;
    private bool _transferHasMore;
    private bool _transferLoading;

    internal InventoryMovementsWindow(PosViewModel viewModel, InventoryItemInfo product,
        PosDesktopModuleContext? moduleContext = null, IPosInventoryHistoryExtension? historyExtension = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _product = product;
        _moduleContext = moduleContext;
        _historyExtension = historyExtension;
        ProductLabel.Text = $"{product.Name} · SKU {product.Sku}";
        TransfersTab.Visibility = historyExtension is null || moduleContext is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) => await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (_loading) return;
        _loading = true;
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        PageStatus.Text = "Cargando movimientos…";
        try
        {
            var page = await _viewModel.GetStockMovementsAsync(_product.Id, _offset, PageSize);
            MovementsGrid.ItemsSource = page.Items;
            _hasMore = page.HasMore;
            var first = page.Items.Count == 0 ? 0 : _offset + 1;
            var last = _offset + page.Items.Count;
            PageStatus.Text = page.Items.Count == 0
                ? "No hay movimientos para este producto."
                : $"Movimientos {first:N0}–{last:N0}" + (_hasMore ? " · hay más" : " · final del historial");
        }
        catch (Exception exception)
        {
            PageStatus.Text = PosUserMessage.ForException(exception);
        }
        finally
        {
            _loading = false;
            PreviousPageButton.IsEnabled = _offset > 0;
            NextPageButton.IsEnabled = _hasMore;
        }
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _offset == 0) return;
        _offset = Math.Max(0, _offset - PageSize);
        await LoadPageAsync();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !_hasMore) return;
        _offset += PageSize;
        await LoadPageAsync();
        if (MovementsGrid.Items.Count == 0 && _offset > 0)
        {
            _offset = Math.Max(0, _offset - PageSize);
            await LoadPageAsync();
        }
    }

    private async Task LoadTransferPageAsync()
    {
        if (_transferLoading || _historyExtension is null || _moduleContext is null) return;
        _transferLoading = true;
        PreviousTransferPageButton.IsEnabled = false;
        NextTransferPageButton.IsEnabled = false;
        TransferPageStatus.Text = "Cargando traslados…";
        try
        {
            var page = await _historyExtension.GetMovementsAsync(_moduleContext, _product.Id, _transferOffset, PageSize);
            TransfersGrid.ItemsSource = page.Items;
            _transferHasMore = page.HasMore;
            var first = page.Items.Count == 0 ? 0 : _transferOffset + 1;
            var last = _transferOffset + page.Items.Count;
            TransferPageStatus.Text = page.Items.Count == 0
                ? "Este producto no tiene salidas ni recepciones por traslado."
                : $"Traslados {first:N0}–{last:N0}" + (_transferHasMore ? " · hay más" : " · final del historial");
        }
        catch (Exception exception) { TransferPageStatus.Text = PosUserMessage.ForException(exception); }
        finally
        {
            _transferLoading = false;
            PreviousTransferPageButton.IsEnabled = _transferOffset > 0;
            NextTransferPageButton.IsEnabled = _transferHasMore;
        }
    }

    private async void PreviousTransferPage_Click(object sender, RoutedEventArgs e)
    {
        if (_transferLoading || _transferOffset == 0) return;
        _transferOffset = Math.Max(0, _transferOffset - PageSize);
        await LoadTransferPageAsync();
    }

    private async void NextTransferPage_Click(object sender, RoutedEventArgs e)
    {
        if (_transferLoading || !_transferHasMore) return;
        _transferOffset += PageSize;
        await LoadTransferPageAsync();
        if (TransfersGrid.Items.Count == 0 && _transferOffset > 0)
        {
            _transferOffset = Math.Max(0, _transferOffset - PageSize);
            await LoadTransferPageAsync();
        }
    }

    private async void HistoryTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.Source == HistoryTabs && HistoryTabs.SelectedItem == TransfersTab) await LoadTransferPageAsync();
    }
}
