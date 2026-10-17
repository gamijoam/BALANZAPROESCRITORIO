using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Modules.ThermalReceipts.Desktop;

internal sealed class ThermalReceiptExtension : IPosReceiptExtension
{
    public Window CreatePreviewWindow(PosDesktopModuleContext context, SaleReceipt receipt)
        => new ThermalReceiptPreviewWindow(context, receipt);

    public async Task<PosDesktopReceiptPrintResult> PrintAsync(PosDesktopModuleContext context, SaleReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await context.ApiClient.GetModuleAsync<ThermalReceiptTemplateSnapshot>(context.ModuleId, "template", cancellationToken);
            var settings = ThermalReceiptPrinterSettings.Load();
            if (string.IsNullOrWhiteSpace(settings.PrinterName))
                return new(false, "No hay impresora configurada en esta terminal. Abre Módulos → Ticket térmico para elegirla.");
            using var server = new LocalPrintServer();
            var queue = server.GetPrintQueue(settings.PrinterName);
            var document = ThermalReceiptDocumentBuilder.Build(receipt, snapshot?.Template ?? ThermalReceiptTemplateDefaults.Default);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            var printTicket = queue.DefaultPrintTicket.Clone();
            printTicket.PageMediaSize = new PageMediaSize(document.PageWidth, document.PageHeight);
            printTicket.CopyCount = settings.Copies;
            var validatedTicket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, printTicket).ValidatedPrintTicket;
            if (validatedTicket.CopyCount != settings.Copies)
                return new(false, "El controlador de Windows no aceptó la cantidad de copias seleccionada.");
            writer.Write(((IDocumentPaginatorSource)document).DocumentPaginator, validatedTicket);
            return new(true);
        }
        catch (Exception exception)
        {
            return new(false, $"No se pudo enviar el ticket a la impresora: {PosUserMessage.ForException(exception)}");
        }
    }
}

internal static class ThermalReceiptTemplateDefaults
{
    public static ThermalReceiptTemplate Default => new(1, 80, ["Punto de venta"], [],
        ["saleInfo", "customer", "items", "total", "rounding", "payments"], ["description", "quantity", "lineTotal"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["receiptTitle"] = "COMPROBANTE DE VENTA", ["sale"] = "Venta", ["date"] = "Fecha",
            ["customer"] = "Cliente", ["item"] = "Artículo", ["sku"] = "Código", ["quantity"] = "Cant.",
            ["unitPrice"] = "Precio", ["lineTotal"] = "Importe", ["total"] = "TOTAL",
            ["rounding"] = "Ajuste de conversión", ["payments"] = "Pagos",
            ["nonFiscal"] = "Documento comercial. No válido como factura fiscal."
        }, "Center", 100);
}

internal sealed class ThermalReceiptPreviewWindow : Window
{
    private readonly PosDesktopModuleContext _context;
    private readonly SaleReceipt _receipt;
    private readonly FlowDocumentScrollViewer _viewer = new() { IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public ThermalReceiptPreviewWindow(PosDesktopModuleContext context, SaleReceipt receipt)
    {
        _context = context; _receipt = receipt;
        Title = "Vista previa del ticket"; Width = 500; Height = 720; MinWidth = 380; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Border { Background = new SolidColorBrush(Color.FromRgb(16, 45, 80)), CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10) };
        heading.Child = new TextBlock { Text = "Vista previa del ticket", Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.Bold };
        Grid.SetRow(heading, 0); layout.Children.Add(heading);
        var retry = new Button { Content = "Imprimir / reintentar", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(5),
            Background = new SolidColorBrush(Color.FromRgb(8, 103, 213)), Foreground = Brushes.White };
        retry.Click += async (_, _) =>
        {
            var result = await new ThermalReceiptExtension().PrintAsync(_context, _receipt);
            MessageBox.Show(this, result.Accepted ? "El ticket se envió a la cola de impresión." : result.Message,
                result.Accepted ? "Ticket enviado" : "No se pudo imprimir", MessageBoxButton.OK,
                result.Accepted ? MessageBoxImage.Information : MessageBoxImage.Warning);
        };
        var close = new Button { Content = "Cerrar", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(5) };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(retry); buttons.Children.Add(close); Grid.SetRow(buttons, 2); layout.Children.Add(buttons);
        var preview = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(213, 223, 235)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10) };
        _viewer.Margin = new Thickness(0); preview.Child = _viewer; Grid.SetRow(preview, 1); layout.Children.Add(preview);
        Content = layout;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var snapshot = await _context.ApiClient.GetModuleAsync<ThermalReceiptTemplateSnapshot>(_context.ModuleId, "template");
            _viewer.Document = ThermalReceiptDocumentBuilder.Build(_receipt, snapshot?.Template ?? ThermalReceiptTemplateDefaults.Default);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, PosUserMessage.ForException(exception), "Vista previa", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
