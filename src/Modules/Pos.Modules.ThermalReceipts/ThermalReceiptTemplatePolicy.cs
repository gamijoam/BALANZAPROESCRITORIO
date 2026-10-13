using Pos.Shared.Contracts;

namespace Pos.Modules.ThermalReceipts;

public static class ThermalReceiptTemplatePolicy
{
    public static readonly IReadOnlySet<string> SupportedSections = new HashSet<string>(StringComparer.Ordinal)
        { "saleInfo", "customer", "items", "total", "rounding", "payments" };
    public static readonly IReadOnlySet<string> SupportedItemColumns = new HashSet<string>(StringComparer.Ordinal)
        { "sku", "description", "quantity", "unitPrice", "lineTotal" };
    public static readonly IReadOnlySet<string> SupportedLabels = new HashSet<string>(StringComparer.Ordinal)
        { "receiptTitle", "sale", "date", "customer", "item", "sku", "quantity", "unitPrice", "lineTotal", "total", "rounding", "payments", "nonFiscal" };

    public static ThermalReceiptTemplate Default => new(
        1, 80,
        ["Punto de venta"],
        [],
        ["saleInfo", "customer", "items", "total", "rounding", "payments"],
        ["description", "quantity", "lineTotal"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["receiptTitle"] = "COMPROBANTE DE VENTA",
            ["sale"] = "Venta",
            ["date"] = "Fecha",
            ["customer"] = "Cliente",
            ["item"] = "Artículo",
            ["sku"] = "Código",
            ["quantity"] = "Cant.",
            ["unitPrice"] = "Precio",
            ["lineTotal"] = "Importe",
            ["total"] = "TOTAL",
            ["rounding"] = "Ajuste de conversión",
            ["payments"] = "Pagos",
            ["nonFiscal"] = "Documento comercial. No válido como factura fiscal."
        },
        "Center", 100);

    public static string? Validate(ThermalReceiptTemplate? template)
    {
        if (template is null) return "Indica una plantilla de ticket.";
        if (template.SchemaVersion != 1) return "La versión de plantilla no es compatible.";
        if (template.PaperWidthMm is not (58 or 80)) return "El ancho de papel debe ser 58 u 80 mm.";
        if (template.FontSizePercent is < 80 or > 150) return "El tamaño de letra debe estar entre 80 % y 150 %.";
        if (template.Alignment is not ("Left" or "Center" or "Right")) return "La alineación no es válida.";
        if (template.HeaderLines is null || template.FooterLines is null || template.Sections is null ||
            template.ItemColumns is null || template.Labels is null) return "La plantilla contiene listas vacías o incompletas.";
        if (!ValidTextLines(template.HeaderLines) || !ValidTextLines(template.FooterLines))
            return "Cada encabezado o pie admite hasta 5 líneas de 60 caracteres.";
        if (template.Sections.Count > SupportedSections.Count ||
            template.Sections.Distinct(StringComparer.Ordinal).Count() != template.Sections.Count ||
            template.Sections.Any(section => !SupportedSections.Contains(section)))
            return "La plantilla contiene secciones repetidas o no compatibles.";
        if (!template.Sections.Contains("items", StringComparer.Ordinal) || !template.Sections.Contains("total", StringComparer.Ordinal))
            return "El ticket debe mostrar los artículos y el total.";
        if (template.ItemColumns.Count is < 1 or > 5 ||
            template.ItemColumns.Distinct(StringComparer.Ordinal).Count() != template.ItemColumns.Count ||
            template.ItemColumns.Any(column => !SupportedItemColumns.Contains(column)) ||
            !template.ItemColumns.Contains("description", StringComparer.Ordinal) ||
            !template.ItemColumns.Contains("quantity", StringComparer.Ordinal) ||
            !template.ItemColumns.Contains("lineTotal", StringComparer.Ordinal))
            return "Las columnas deben incluir descripción, cantidad e importe y no repetir campos.";
        if (template.Labels.Count > SupportedLabels.Count || template.Labels.Keys.Any(key => !SupportedLabels.Contains(key)) ||
            template.Labels.Any(pair => pair.Value is null ||
                pair.Value.Length > (pair.Key == "nonFiscal" ? 120 : 32) || pair.Value.Any(char.IsControl)))
            return "Una etiqueta está fuera del catálogo permitido o supera 32 caracteres.";
        return null;
    }

    public static ThermalReceiptTemplate Normalize(ThermalReceiptTemplate template)
    {
        var error = Validate(template);
        if (error is not null) throw new ArgumentException(error, nameof(template));
        return template with
        {
            HeaderLines = template.HeaderLines.Select(line => line.Trim()).Where(line => line.Length > 0).ToArray(),
            FooterLines = template.FooterLines.Select(line => line.Trim()).Where(line => line.Length > 0).ToArray(),
            Labels = template.Labels.ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.Ordinal)
        };
    }

    private static bool ValidTextLines(IReadOnlyList<string> lines) => lines.Count <= 5 &&
        lines.All(line => line is not null && line.Length <= 60 && !line.Any(char.IsControl));
}
