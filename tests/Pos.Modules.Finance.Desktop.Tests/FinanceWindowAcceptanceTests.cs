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

        AssertLabel(document, "DraftSupplierLabel", "Proveedor (obligatorio)");
        AssertLabel(document, "DraftDocumentTypeLabel", "Tipo de documento");
        AssertLabel(document, "DraftDocumentNumberLabel", "Número de factura (obligatorio)");
        AssertLabel(document, "DraftControlNumberLabel", "Número de control (opcional)");
        AssertLabel(document, "DraftCurrencyLabel", "Moneda de la factura");
        AssertLabel(document, "DraftExchangeRateLabel", "Tasa VES por USD");
        AssertLabel(document, "DraftIssueDateLabel", "Fecha de emisión");
        AssertLabel(document, "DraftDueDateLabel", "Vencimiento (opcional)");
        AssertLabel(document, "DraftFreightLabel", "Flete facturado");
        AssertLabel(document, "DraftPaymentTermLabel", "Condición de pago");
        AssertLabel(document, "DraftProductLabel", "Artículo (busca y selecciona)");
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

    [Fact]
    public void Purchase_draft_has_a_separate_workspace_with_a_pinned_save_bar_and_line_recovery()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FinanceWindow.xaml"));
        var invoiceWorkspace = FindNamedElement(document, "InvoiceWorkspace");
        var draftEditor = FindNamedElement(document, "DraftEditor");
        var footer = FindNamedElement(document, "DraftFooterPanel");

        Assert.Equal("Collapsed", (string?)draftEditor.Attribute("Visibility"));
        AssertLabel(document, "SaveDraftButton", "Guardar borrador");
        AssertLabel(document, "CancelDraftButton", "Volver a facturas");
        Assert.NotNull(FindNamedElement(document, "RemoveDraftLineButton"));
        Assert.DoesNotContain(footer.Ancestors(), element => element.Name.LocalName == "ScrollViewer");
        Assert.NotNull(invoiceWorkspace);
    }

    [Fact]
    public void Supplier_payment_dialog_explains_the_invoice_context_and_labels_every_field()
    {
        var paymentWindow = typeof(FinanceWindow).Assembly.GetType("Pos.Modules.Finance.Desktop.SupplierPaymentWindow");
        Assert.NotNull(paymentWindow);
        var paymentXaml = Path.Combine(Path.GetDirectoryName(typeof(FinanceWindow).Assembly.Location)!, "SupplierPaymentWindow.xaml");
        var document = XDocument.Load(paymentXaml);

        AssertLabel(document, "PaymentSupplierLabel", "Proveedor");
        AssertLabel(document, "PaymentDocumentLabel", "Factura");
        AssertLabel(document, "PaymentBalanceLabel", "Saldo pendiente");
        AssertLabel(document, "PaymentAmountLabel", "Importe que vas a pagar");
        AssertLabel(document, "PaymentCurrencyLabel", "Moneda del pago");
        AssertLabel(document, "PaymentMethodLabel", "Medio de pago");
        AssertLabel(document, "PaymentReceivingAccountLabel", "Cuenta receptora");
        AssertLabel(document, "PaymentReferenceLabel", "Referencia (opcional)");
        Assert.NotNull(FindNamedElement(document, "PaymentConversionHelp"));
        Assert.NotNull(FindNamedElement(document, "ConfirmPaymentButton"));
    }

    [Fact]
    public void Selected_payment_has_a_detail_area_for_reconciliation_data()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FinanceWindow.xaml"));
        var detail = FindNamedElement(document, "PaymentDetailLabel");

        Assert.Equal("Visible", (string?)detail.Attribute("Visibility"));
    }

    [Fact]
    public void Price_suggestions_require_configured_margins_and_an_explicit_review_action()
    {
        var windowDocument = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FinanceWindow.xaml"));
        AssertLabel(windowDocument, "CostBasisLabel", "Costo base para precios sugeridos");
        var costBasisPicker = FindNamedElement(windowDocument, "CostBasisPicker");
        var costBasisOptions = costBasisPicker.Descendants()
            .Select(element => (string?)element.Attribute("Content"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Contains("Promedio ponderado", costBasisOptions);
        Assert.Contains("Último costo recibido", costBasisOptions);
        AssertLabel(windowDocument, "Price1MarginLabel", "Precio 1 (%)");
        AssertLabel(windowDocument, "Price2MarginLabel", "Precio 2 (%)");
        AssertLabel(windowDocument, "Price3MarginLabel", "Precio 3 (%)");
        AssertLabel(windowDocument, "SavePriceMarginsButton", "Guardar márgenes y costo base");
        AssertLabel(windowDocument, "ViewPriceSuggestionsButton", "Revisar precios sugeridos");

        var suggestionPath = Path.Combine(AppContext.BaseDirectory, "PriceSuggestionWindow.xaml");
        var suggestionDocument = XDocument.Load(suggestionPath);
        Assert.Equal("Propuestas de precio por producto y nivel",
            (string?)FindNamedElement(suggestionDocument, "SuggestionsGrid").Attribute("AutomationProperties.Name"));
        Assert.NotNull(FindNamedElement(suggestionDocument, "CostBasisSummary"));
        var suggestionHeaders = FindNamedElement(suggestionDocument, "SuggestionsGrid").Descendants()
            .Select(element => (string?)element.Attribute("Header"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Contains("Costo utilizado", suggestionHeaders);
        Assert.Contains("Origen del costo", suggestionHeaders);
        AssertLabel(suggestionDocument, "ApplySuggestionButton", "Confirmar precio seleccionado");
        Assert.Contains("El servicio comprobará que el costo de inventario y el precio actual no hayan cambiado", suggestionDocument.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Price_suggestions_show_two_decimals_and_confirm_the_same_rounded_price()
    {
        var suggestion = new PriceSuggestionWindow.PriceSuggestionClientInfo(Guid.NewGuid(), "SKU-1", "Producto", "USD",
            6m, "Price1", 50m, 3m, 12m, Guid.NewGuid(), "F-1", 6m, "LastReceivedCost", "Receipt", "F-1", DateTime.UtcNow);

        Assert.Equal("USD 6,00", suggestion.CostLabel);
        Assert.Equal("USD 3,00", suggestion.CurrentPriceLabel);
        Assert.Equal("USD 12,00", suggestion.SuggestedPriceLabel);

        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "Modules", "Pos.Modules.Finance.Desktop", "PriceSuggestionWindow.xaml.cs"));
        Assert.Contains("value.ToString(\"N2\"", source, StringComparison.Ordinal);
        Assert.Contains("decimal.Round(suggestion.SuggestedPrice, 2", source, StringComparison.Ordinal);
        var initialCost = new PriceSuggestionWindow.PriceSuggestionClientInfo(Guid.NewGuid(), "SKU-1", "Producto", "USD",
            1.2345m, "Price1", 50m, null, 2m, Guid.NewGuid(), "F-1", 1.2345m, "LastReceivedCost", "InitialCost", null, null);
        Assert.Equal("Costo inicial", initialCost.CostSourceLabel);
    }

    [Fact]
    public void Price_suggestions_can_be_opened_from_a_saved_invoice_before_its_first_receipt()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "Modules", "Pos.Modules.Finance.Desktop", "FinanceWindow.xaml.cs"));
        var start = source.IndexOf("ViewPriceSuggestionsButton.IsEnabled =", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("ToggleSupplierButton.IsEnabled", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var rule = source[start..end];

        Assert.Contains("_selectedPurchase is not null", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("ReceiptsGrid.Items", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrying_the_same_supplier_payment_reuses_its_idempotency_key_but_changed_data_gets_a_new_key()
    {
        var assembly = typeof(FinanceWindow).Assembly;
        var policy = assembly.GetType("Pos.Modules.Finance.Desktop.FinancePaymentRetryPolicy");
        var requestType = typeof(FinanceWindow).GetNestedType("SupplierPaymentRequest");
        Assert.NotNull(policy);
        Assert.NotNull(requestType);
        var method = policy!.GetMethod("CreateOrReuse", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var invoiceId = Guid.NewGuid();
        var pending = Activator.CreateInstance(requestType!, [25m, "VES", "Transferencia", "Banco Uno", "ref-123", "first-key"]);
        var retry = method!.Invoke(null, [invoiceId, invoiceId, pending, 25m, "VES", "Transferencia", "Banco Uno", "ref-123"]);
        var changed = method.Invoke(null, [invoiceId, invoiceId, pending, 30m, "VES", "Transferencia", "Banco Uno", "ref-123"]);

        Assert.Equal("first-key", requestType!.GetProperty("IdempotencyKey")!.GetValue(retry));
        Assert.NotEqual("first-key", requestType.GetProperty("IdempotencyKey")!.GetValue(changed));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("F-001", true)]
    public void Purchase_invoice_requires_a_document_number(string? documentNumber, bool expected)
    {
        var validation = typeof(FinanceWindow).Assembly.GetType("Pos.Modules.Finance.Desktop.FinanceDraftValidation");
        Assert.NotNull(validation);
        var method = validation!.GetMethod("HasDocumentNumber", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Equal(expected, method!.Invoke(null, [documentNumber]));
    }

    [Fact]
    public void Saving_a_new_draft_selects_it_so_the_next_invoice_action_is_available()
    {
        var createdId = Guid.NewGuid();
        var created = new FinanceWindow.PurchaseSummaryClientInfo(createdId, Guid.NewGuid(), "Proveedor", "Invoice",
            "F-002", null, "USD", new DateOnly(2026, 10, 14), null, 20m, "Draft", "Credit", 20m);
        var prior = new FinanceWindow.PurchaseSummaryClientInfo(Guid.NewGuid(), Guid.NewGuid(), "Otro proveedor", "Invoice",
            "F-001", null, "USD", new DateOnly(2026, 10, 13), null, 10m, "Draft", "Credit", 10m);

        var selected = FinancePurchaseSelectionPolicy.Select([prior, created], createdId, prior.Id);

        Assert.Equal(createdId, selected?.Id);

        var request = new FinanceWindow.PurchaseDraftRequest(created.SupplierId, "Invoice", created.DocumentNumber,
            null, "USD", 1m, new DateOnly(2026, 10, 14), null, 0m, "Credit", [], null, null);
        var selectedWhenCreateResponseHasNoId = FinancePurchaseSelectionPolicy.Select([prior, created], null, prior.Id, request);
        Assert.Equal(createdId, selectedWhenCreateResponseHasNoId?.Id);
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

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }
}
