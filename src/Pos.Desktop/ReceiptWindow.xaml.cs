using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class ReceiptWindow : Window
{
    private readonly SaleReceipt _receipt;
    private readonly FlowDocument _document;

    public ReceiptWindow(SaleReceipt receipt)
    {
        InitializeComponent();
        _receipt = receipt;
        _document = BuildDocument(receipt);
        ReceiptPaper.Child = new FlowDocumentScrollViewer { Document = _document, IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static FlowDocument BuildDocument(SaleReceipt receipt)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, PagePadding = new Thickness(18) };
        doc.Blocks.Add(new Paragraph(new Run("COMPROBANTE DE PRUEBA — NO FISCAL")) { FontSize = 16, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
        doc.Blocks.Add(new Paragraph(new Run("Punto de venta")) { TextAlignment = TextAlignment.Center, Foreground = Brushes.DimGray });
        doc.Blocks.Add(new Paragraph(new Run($"Venta: {receipt.SaleId:D}\nFecha: {receipt.IssuedAtUtc:dd/MM/yyyy HH:mm:ss}")));
        var table = new Table();
        table.Columns.Add(new TableColumn { Width = new GridLength(3, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.5, GridUnitType.Star) });
        var group = new TableRowGroup(); table.RowGroups.Add(group);
        var header = new TableRow { FontWeight = FontWeights.Bold };
        header.Cells.Add(new TableCell(new Paragraph(new Run("Artículo"))));
        header.Cells.Add(new TableCell(new Paragraph(new Run("Cant."))));
        header.Cells.Add(new TableCell(new Paragraph(new Run("Total")))); group.Rows.Add(header);
        foreach (var line in receipt.Lines)
        {
            var row = new TableRow();
            var sourcePrice = line.OriginalCurrency == receipt.Currency
                ? string.Empty
                : $"\n{line.OriginalCurrency} {line.OriginalUnitPrice:N2} → {receipt.Currency} {line.UnitPrice:N2}";
            row.Cells.Add(new TableCell(new Paragraph(new Run($"{line.Description}\n{line.Sku}{sourcePrice}"))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(PosNumberFormat.FormatQuantity(line.Quantity)))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(PosNumberFormat.FormatAmount(line.Total))))); group.Rows.Add(row);
        }
        doc.Blocks.Add(table);
        doc.Blocks.Add(new Paragraph(new Run($"TOTAL: {receipt.Currency} {receipt.Total:N2}")) { FontSize = 17, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 14, 0, 0) });
        doc.Blocks.Add(new Paragraph(new Run("Pagos")) { FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 2) });
        foreach (var payment in receipt.Payments)
            doc.Blocks.Add(new Paragraph(new Run($"{payment.Method}: {payment.Currency} {payment.Amount:N2}")) { Margin = new Thickness(0, 1, 0, 1) });
        doc.Blocks.Add(new Paragraph(new Run("Documento de demostración. No válido como factura fiscal.")) { FontSize = 10, Foreground = Brushes.DimGray, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 20, 0, 0) });
        return doc;
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        try
        {
            var paginator = ((IDocumentPaginatorSource)_document).DocumentPaginator;
            paginator.PageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
            dialog.PrintDocument(paginator, $"Comprobante { _receipt.SaleId.ToString("N")[..8] } — NO FISCAL");
        }
        catch (Exception) { MessageBox.Show(this, "La venta quedó guardada, pero no se pudo imprimir el comprobante.", "Impresión", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
