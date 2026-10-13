using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Modules.ThermalReceipts.Desktop;

internal static class ThermalReceiptDocumentBuilder
{
    public static FlowDocument Build(SaleReceipt receipt, ThermalReceiptTemplate template)
    {
        var width = template.PaperWidthMm * 96d / 25.4d;
        var doc = new FlowDocument { PageWidth = width, PagePadding = new Thickness(8), ColumnWidth = width - 16,
            FontFamily = new FontFamily("Consolas"), FontSize = 10 * template.FontSizePercent / 100d };
        foreach (var line in template.HeaderLines) doc.Blocks.Add(Paragraph(line, template.Alignment, true));
        var labels = template.Labels;
        if (template.Sections.Contains("saleInfo"))
        {
            doc.Blocks.Add(Paragraph(Label(labels, "receiptTitle", "COMPROBANTE DE VENTA"), template.Alignment, true));
            doc.Blocks.Add(Paragraph($"{Label(labels, "sale", "Venta")}: {receipt.SaleId.ToString("N")[..8].ToUpperInvariant()}"));
            doc.Blocks.Add(Paragraph($"{Label(labels, "date", "Fecha")}: {receipt.IssuedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}"));
        }
        if (template.Sections.Contains("customer") && !string.IsNullOrWhiteSpace(receipt.CustomerName))
            doc.Blocks.Add(Paragraph($"{Label(labels, "customer", "Cliente")}: {receipt.CustomerName}"));
        if (template.Sections.Contains("items"))
        {
            doc.Blocks.Add(Paragraph(new string('-', template.PaperWidthMm == 58 ? 30 : 44)));
            foreach (var line in receipt.Lines)
            {
                foreach (var column in template.ItemColumns)
                {
                    var content = column switch
                    {
                        "sku" => $"{Label(labels, "sku", "Código")}: {line.Sku}",
                        "description" => line.Description,
                        "quantity" => $"{Label(labels, "quantity", "Cant.")}: {PosNumberFormat.FormatQuantity(line.Quantity)} × {PosNumberFormat.FormatAmount(line.UnitPrice)} {receipt.Currency}",
                        "unitPrice" => $"{Label(labels, "unitPrice", "Precio")}: {PosNumberFormat.FormatAmount(line.UnitPrice)} {receipt.Currency}",
                        "lineTotal" => $"{Label(labels, "lineTotal", "Importe")}: {PosNumberFormat.FormatAmount(line.Total)} {receipt.Currency}",
                        _ => string.Empty
                    };
                    doc.Blocks.Add(Paragraph(content, column == "lineTotal" ? "Right" : "Left", column == "description"));
                }
            }
        }
        doc.Blocks.Add(Paragraph(new string('-', template.PaperWidthMm == 58 ? 30 : 44)));
        if (template.Sections.Contains("rounding") && receipt.RoundingAdjustment != 0)
            doc.Blocks.Add(Paragraph($"{Label(labels, "rounding", "Ajuste de conversión")}: {PosNumberFormat.FormatAmount(receipt.RoundingAdjustment)} {receipt.Currency}"));
        if (template.Sections.Contains("total"))
            doc.Blocks.Add(Paragraph($"{Label(labels, "total", "TOTAL")}: {PosNumberFormat.FormatAmount(receipt.Total)} {receipt.Currency}", "Right", true));
        if (template.Sections.Contains("payments"))
        {
            doc.Blocks.Add(Paragraph(Label(labels, "payments", "Pagos"), "Left", true));
            foreach (var payment in receipt.Payments) doc.Blocks.Add(Paragraph($"{payment.Method}: {PosNumberFormat.FormatAmount(payment.Amount)} {payment.Currency}"));
        }
        doc.Blocks.Add(Paragraph(Label(labels, "nonFiscal", "Documento comercial. No válido como factura fiscal."), template.Alignment));
        foreach (var line in template.FooterLines) doc.Blocks.Add(Paragraph(line, template.Alignment));
        var charactersPerLine = Math.Max(14, (width - 16) / (doc.FontSize * 0.62));
        var visualLines = doc.Blocks.OfType<Paragraph>().Sum(paragraph =>
        {
            var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
            return Math.Max(1, (int)Math.Ceiling(text.Length / charactersPerLine));
        });
        doc.PageHeight = Math.Ceiling(24 + visualLines * doc.FontSize * 1.45);
        return doc;
    }

    private static Paragraph Paragraph(string text, string alignment = "Left", bool bold = false) => new(new Run(text))
    {
        Margin = new Thickness(0, 0, 0, 2),
        TextAlignment = alignment switch { "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => TextAlignment.Left },
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
    };
    private static string Label(IReadOnlyDictionary<string, string> labels, string key, string fallback)
        => labels.TryGetValue(key, out var label) && !string.IsNullOrWhiteSpace(label) ? label : fallback;
}
