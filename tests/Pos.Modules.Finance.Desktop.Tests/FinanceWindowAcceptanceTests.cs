using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Pos.Desktop.Presentation;
using Pos.Modules.Finance.Desktop;

namespace Pos.Modules.Finance.Desktop.Tests;

public sealed class FinanceWindowAcceptanceTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Supplier_and_purchase_forms_have_visible_field_labels_and_a_supplier_level_debt_summary()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FinanceWindow.xaml"));

        AssertLabel(document, "SupplierCodeLabel", "Código del proveedor");
        AssertLabel(document, "SupplierNameLabel", "Nombre o razón social");
        AssertLabel(document, "SupplierTaxIdLabel", "RIF o documento");
        AssertLabel(document, "SupplierPhoneLabel", "Teléfono");
        AssertLabel(document, "SupplierEmailLabel", "Correo");
        AssertLabel(document, "SupplierAddressLabel", "Dirección");

        AssertLabel(document, "DraftSupplierLabel", "Proveedor");
        AssertLabel(document, "DraftDocumentTypeLabel", "Tipo de documento");
        AssertLabel(document, "DraftDocumentNumberLabel", "Número de factura");
        AssertLabel(document, "DraftControlNumberLabel", "Número de control");
        AssertLabel(document, "DraftCurrencyLabel", "Moneda");
        AssertLabel(document, "DraftExchangeRateLabel", "Tasa de cambio");
        AssertLabel(document, "DraftIssueDateLabel", "Fecha de emisión");
        AssertLabel(document, "DraftDueDateLabel", "Fecha de vencimiento");
        AssertLabel(document, "DraftFreightLabel", "Flete facturado");
        AssertLabel(document, "DraftPaymentTermLabel", "Condición de pago");
        AssertLabel(document, "DraftProductLabel", "Artículo");
        AssertLabel(document, "DraftQuantityLabel", "Cantidad");
        AssertLabel(document, "DraftUnitPriceLabel", "Precio unitario");
        AssertLabel(document, "DraftDiscountLabel", "Descuento");
        AssertLabel(document, "DraftTaxLabel", "Impuesto");

        var root = document.Root!;
        Assert.Equal("Maximized", (string?)root.Attribute("WindowState"));
        Assert.Equal("CanResize", (string?)root.Attribute("ResizeMode"));

        var obligationGrid = FindNamedElement(document, "OpenPayablesGrid");
        var headers = obligationGrid.Descendants()
            .Select(element => (string?)element.Attribute("Header"))
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Proveedor", headers);
        Assert.Contains("Documento", headers);
        Assert.Contains("Importe", headers);
        Assert.Contains("Pagado", headers);
        Assert.Contains("Saldo pendiente", headers);
        Assert.Contains("Estado", headers);
    }

    [Theory]
    [InlineData(100, "100")]
    [InlineData(0.5, "0,5")]
    [InlineData(1.25, "1,25")]
    [InlineData(1.234, "1,234")]
    public void Quantity_display_uses_only_significant_decimals(decimal quantity, string expected)
    {
        var method = typeof(PosNumberFormat).GetMethod("FormatQuantity", BindingFlags.Public | BindingFlags.Static,
            [typeof(decimal)]);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [quantity]);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Draft", "Borrador")]
    [InlineData("Posted", "Contabilizada")]
    [InlineData("Partial", "Pago parcial")]
    [InlineData("Paid", "Pagada")]
    [InlineData("Reversed", "Reversada")]
    public void Purchase_status_is_localized_only_for_display(string status, string expected)
    {
        var purchase = new FinanceWindow.PurchaseSummaryClientInfo(Guid.NewGuid(), Guid.NewGuid(), "Proveedor",
            "Invoice", "F-001", null, "USD", new DateOnly(2026, 10, 14), null, 100m, status, "Credit", 100m);
        var property = purchase.GetType().GetProperty("StatusLabel");

        Assert.NotNull(property);
        Assert.Equal(expected, property!.GetValue(purchase));
        Assert.Equal(status, purchase.Status);
    }

    [Fact]
    public void Retrying_a_pending_receipt_reuses_the_original_idempotency_key_and_quantities()
    {
        var receiptType = typeof(FinanceWindow).GetNestedType("PurchaseReceiptClientInfo");
        Assert.NotNull(receiptType);
        var retryPolicy = typeof(FinanceWindow).Assembly.GetType("Pos.Modules.Finance.Desktop.FinanceReceiptRetryPolicy");
        Assert.NotNull(retryPolicy);
        var method = retryPolicy!.GetMethod("CreateRetryRequest", BindingFlags.Public | BindingFlags.Static,
            [typeof(FinanceWindow.PurchaseReceiptClientInfo)]);
        Assert.NotNull(method);

        var invoiceLineId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();
        var header = new FinanceWindow.PurchaseReceiptHeaderClientInfo(Guid.NewGuid(), Guid.NewGuid(), warehouseId,
            "same-key-for-retry", DateTime.UtcNow, null, null);
        var lines = new[] { new FinanceWindow.PurchaseReceiptLineClientInfo(invoiceLineId, Guid.NewGuid(), 4.5m, 2m, "USD", 1m) };
        var pending = new FinanceWindow.PurchaseReceiptClientInfo(header, lines);
        var request = method!.Invoke(null, [pending]);

        Assert.NotNull(request);
        Assert.Equal(header.IdempotencyKey, request!.GetType().GetProperty("IdempotencyKey")!.GetValue(request));
        Assert.Equal(warehouseId, request.GetType().GetProperty("WarehouseId")!.GetValue(request));
        var requestLines = Assert.IsAssignableFrom<IReadOnlyList<FinanceWindow.PurchaseReceiptLineRequest>>(
            request.GetType().GetProperty("Lines")!.GetValue(request));
        Assert.Equal(new FinanceWindow.PurchaseReceiptLineRequest(invoiceLineId, 4.5m), Assert.Single(requestLines));
    }

    private static void AssertLabel(XDocument document, string name, string expected)
    {
        var element = FindNamedElement(document, name);
        var text = (string?)element.Attribute("Text") ?? (string?)element.Attribute("Content");
        Assert.Equal(expected, text);
        Assert.NotEqual("Collapsed", (string?)element.Attribute("Visibility"));
    }

    private static XElement FindNamedElement(XDocument document, string name)
        => Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute(XamlNamespace + "Name") == name);
}
