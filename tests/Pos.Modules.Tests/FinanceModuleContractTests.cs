using Pos.Modules.Abstractions;
using Pos.Modules.Finance;
using System.Xml.Linq;

namespace Pos.Modules.Tests;

public sealed class FinanceModuleContractTests
{
    [Theory]
    [InlineData(12.34, "USD", "VES", 40.5, 499.77)]
    [InlineData(405, "VES", "USD", 40.5, 10)]
    [InlineData(12.34, "USD", "USD", 40.5, 12.34)]
    public void Purchase_cost_is_converted_to_the_product_currency_using_the_invoice_rate(
        decimal invoiceUnitPrice, string invoiceCurrency, string productCurrency, decimal rate, decimal expected)
        => Assert.Equal(expected, FinancePurchasePolicy.ConvertUnitCost(invoiceUnitPrice, invoiceCurrency, productCurrency, rate));

    [Fact]
    public void Weighted_average_cost_uses_current_stock_and_received_stock()
        => Assert.Equal(12m, FinancePurchasePolicy.WeightedAverageUnitCost(3m, 10m, 2m, 15m));

    [Fact]
    public void Inventory_cost_uses_only_net_item_price_and_excludes_freight_and_taxes()
        => Assert.Equal(9m, FinancePurchasePolicy.CalculateItemUnitCost(2m, 10m, 2m, "USD", "USD", 50m));

    [Theory]
    [InlineData(100, "VES", "USD", 50, 2)]
    [InlineData(3, "USD", "VES", 50, 150)]
    [InlineData(3, "USD", "USD", 50, 3)]
    public void Supplier_payment_converts_to_the_invoice_currency_and_preserves_its_principal(
        decimal paymentAmount, string paymentCurrency, string invoiceCurrency, decimal rate, decimal expectedPrincipalReduction)
        => Assert.Equal(expectedPrincipalReduction,
            FinancePurchasePolicy.ConvertPaymentToInvoiceCurrency(paymentAmount, paymentCurrency, invoiceCurrency, rate));

    [Fact]
    public void Purchase_total_is_calculated_from_subtotal_discount_freight_and_tax()
        => Assert.Equal(40m, FinancePurchasePolicy.CalculateInvoiceTotal(40m, 5m, 3m, 2m));

    [Fact]
    public void Manifest_declares_a_signed_installable_desktop_server_module()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "finance", "module.manifest.json");
        var manifest = ModuleManifestReader.ParseAndValidate(File.ReadAllText(path));

        Assert.Equal("finance", manifest.Id);
        Assert.Equal("1.0.2", manifest.Version.ToString(3));
        Assert.NotNull(manifest.EntryAssembly);
        Assert.NotNull(manifest.DesktopEntryAssembly);
        Assert.Contains("Supplier.View", manifest.Permissions);
        Assert.Contains("Supplier.Manage", manifest.Permissions);
        Assert.Contains("Purchase.View", manifest.Permissions);
        Assert.Contains("Purchase.Manage", manifest.Permissions);
        Assert.Contains("SupplierPayment.Manage", manifest.Permissions);
        Assert.Contains("Finance.Reports", manifest.Permissions);
        Assert.NotEmpty(manifest.Migrations);
    }

    [Fact]
    public void Migration_creates_isolated_supplier_purchase_payable_and_payment_records()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "finance", "finance-0001.json");
        var migration = ModuleMigrationReader.Parse(File.ReadAllText(path));
        var tables = migration.Operations.OfType<CreateModuleTable>()
            .Select(operation => operation.Table).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("suppliers", tables);
        Assert.Contains("purchase_invoices", tables);
        Assert.Contains("purchase_invoice_lines", tables);
        Assert.Contains("purchase_receipts", tables);
        Assert.Contains("purchase_receipt_lines", tables);
        Assert.Contains("supplier_payments", tables);
        Assert.Contains("supplier_events", tables);
        Assert.Contains(migration.Operations.OfType<CreateModuleIndex>(), index => index.Unique && index.Table == "purchase_invoices");
        Assert.All(migration.Operations, operation => Assert.True(operation is CreateModuleTable or CreateModuleIndex));
    }

    [Fact]
    public void Every_finance_endpoint_has_a_manifest_permission()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "finance", "module.manifest.json");
        var manifest = ModuleManifestReader.ParseAndValidate(File.ReadAllText(manifestPath));
        var permissions = new FinanceModule().GetEndpoints()
            .Select(endpoint => endpoint.RequiredPermission)
            .Where(permission => permission is not null)
            .Distinct(StringComparer.Ordinal);

        Assert.All(permissions, permission => Assert.Contains(permission, manifest.Permissions));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.EndsWith("/post", StringComparison.Ordinal));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.EndsWith("/receipts", StringComparison.Ordinal));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.EndsWith("/payments", StringComparison.Ordinal));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.Contains("receipts/{receiptId:guid}/reverse", StringComparison.Ordinal));
    }

    [Fact]
    public void Payment_idempotency_is_bound_to_the_invoice()
    {
        var firstInvoice = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondInvoice = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var request = new FinanceModule.SupplierPaymentRequest(10m, "USD", "Transfer", "Banco", "ref-1", "request-12345678");

        Assert.NotEqual(FinanceIdempotencyPolicy.HashPayment(firstInvoice, request),
            FinanceIdempotencyPolicy.HashPayment(secondInvoice, request));
    }

    [Fact]
    public void Finance_desktop_is_a_module_menu_and_uses_the_host_http_client()
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, "finance", "FinanceWindow.xaml");
        var document = XDocument.Load(xamlPath);
        var text = File.ReadAllText(xamlPath);
        var moduleSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "finance", "FinanceDesktopModule.cs"));
        var windowSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "finance", "FinanceWindow.xaml.cs"));
        Assert.Contains("x:Name=\"SupplierGrid\"", text);
        Assert.Contains("x:Name=\"PurchaseGrid\"", text);
        Assert.Contains("Proveedores", text);
        Assert.Contains("Facturas y cuentas por pagar", text);
        Assert.Contains("RegisterMenu(\"finance.main\", \"Compras y proveedores\"", moduleSource);
        Assert.Contains("GetModuleAsync", windowSource);
        Assert.Contains("PostModuleAsync", windowSource);
        Assert.NotNull(document.Root);
    }
}
