using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class SaleReturnWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly SaleDetailRow _sale;
    private readonly IReadOnlyList<ReturnLineForm> _lines;
    private SaleReturnInput? _pendingRequest;
    private string? _pendingIdempotencyKey;

    public SaleReturnWindow(PosViewModel viewModel, SaleDetailRow sale)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _sale = sale;
        _lines = sale.Lines.Where(x => x.ReturnableQuantity > 0m)
            .Select(x => new ReturnLineForm(x) { QuantityToReturn = PosNumberFormat.FormatQuantity(x.ReturnableQuantity), Restock = true }).ToArray();
        SaleSummary.Text = $"Venta {_sale.Id.ToString("N")[..8]} · {_sale.CustomerName} · Total {_sale.Currency} {PosNumberFormat.FormatAmount(_sale.Total)} · ya devuelto {_sale.Currency} {PosNumberFormat.FormatAmount(_sale.ReturnedAmount)}";
        ReturnLinesGrid.ItemsSource = _lines;
        ConfirmButton.IsEnabled = _lines.Count > 0 && sale.Status != "Returned";
        if (!ConfirmButton.IsEnabled) ErrorLabel.Text = "Esta venta ya fue devuelta completamente o no tiene cantidades pendientes.";
        UpdatePreview();
    }

    private void ReturnLinesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(UpdatePreview, DispatcherPriority.Background);

    private void UpdatePreview()
    {
        var amount = _lines.Where(x => TryGetQuantity(x, out _))
            .Sum(x => decimal.Round(x.UnitPrice * ParsedQuantity(x), 2, MidpointRounding.AwayFromZero));
        PreviewLabel.Text = $"Reintegro estimado: {_sale.Currency} {PosNumberFormat.FormatAmount(amount)} · se reparte según los pagos originales";
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ReturnLinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ReturnLinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ErrorLabel.Text = string.Empty;
        if (_pendingRequest is null)
        {
            var selected = new List<SaleReturnLineInput>();
            foreach (var line in _lines)
            {
                if (!TryGetQuantity(line, out var quantity))
                {
                    if (!string.IsNullOrWhiteSpace(line.QuantityToReturn) && line.QuantityToReturn != "0")
                    { ErrorLabel.Text = $"Revisa la cantidad de {line.Description}."; return; }
                    continue;
                }
                if (quantity > line.ReturnableQuantity)
                { ErrorLabel.Text = $"La cantidad de {line.Description} supera lo pendiente de devolver."; return; }
                selected.Add(new SaleReturnLineInput(line.Id, quantity, line.Restock));
            }
            var reason = ReasonBox.Text.Trim();
            if (selected.Count == 0) { ErrorLabel.Text = "Indica al menos un artículo y una cantidad a devolver."; return; }
            if (reason.Length < 3) { ErrorLabel.Text = "Escribe un motivo de al menos 3 caracteres."; ReasonBox.Focus(); return; }

            var preview = selected.Sum(x =>
            {
                var original = _lines.Single(y => y.Id == x.SaleLineId);
                return decimal.Round(original.UnitPrice * x.Quantity, 2, MidpointRounding.AwayFromZero);
            });
            if (MessageBox.Show(this,
                    $"¿Confirmas devolver {selected.Sum(x => x.Quantity):0.###} unidades por {_sale.Currency} {PosNumberFormat.FormatAmount(preview)}? El reintegro se distribuirá según los pagos originales.",
                    "Confirmar devolución", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _pendingRequest = new SaleReturnInput(reason, selected);
            _pendingIdempotencyKey = $"return-{Guid.NewGuid():N}";
            ReturnLinesGrid.IsEnabled = false;
            ReasonBox.IsEnabled = false;
        }

        ConfirmButton.IsEnabled = false;
        try
        {
            var result = await _viewModel.ReturnSaleAsync(_sale.Id, _pendingRequest, _pendingIdempotencyKey!);
            MessageBox.Show(this, $"Devolución registrada: {result.Currency} {PosNumberFormat.FormatAmount(result.Amount)}.", "Devolución", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            if (exception is PosApiException api && (int)api.StatusCode is >= 400 and < 500)
            {
                _pendingRequest = null;
                _pendingIdempotencyKey = null;
                ReturnLinesGrid.IsEnabled = true;
                ReasonBox.IsEnabled = true;
                ErrorLabel.Text = PosUserMessage.ForException(exception);
            }
            else
            {
                ErrorLabel.Text = "No se confirmó la respuesta. Reintenta para consultar la misma solicitud sin duplicar la devolución.";
            }
            ConfirmButton.IsEnabled = true;
        }
    }

    private static bool TryGetQuantity(ReturnLineForm line, out decimal quantity)
    {
        var parsed = PosNumberFormat.TryParseAmount(line.QuantityToReturn, out quantity);
        return parsed && quantity > 0m && decimal.Round(quantity, 3) == quantity;
    }

    private static decimal ParsedQuantity(ReturnLineForm line)
        => TryGetQuantity(line, out var quantity) ? quantity : 0m;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class ReturnLineForm(SaleDetailLine line)
    {
        public Guid Id { get; } = line.Id;
        public string Sku { get; } = line.Sku;
        public string Description { get; } = line.Description;
        public decimal Quantity { get; } = line.Quantity;
        public decimal ReturnedQuantity { get; } = line.ReturnedQuantity;
        public decimal ReturnableQuantity { get; } = line.ReturnableQuantity;
        public decimal UnitPrice { get; } = line.UnitPrice;
        public string QuantityToReturn { get; set; } = string.Empty;
        public bool Restock { get; set; }
    }
}
