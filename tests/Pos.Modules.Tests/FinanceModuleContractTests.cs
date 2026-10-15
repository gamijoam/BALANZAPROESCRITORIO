using Pos.Modules.Abstractions;
using Pos.Modules.Finance;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Pos.Modules.Tests;

public sealed class FinanceModuleContractTests
{
    [Fact]
    public void Receipt_query_quotes_the_product_currency_identifier_for_postgresql()
    {
        var sql = FinanceSql.LoadInvoiceReceiptCosts("\"finance\".\"purchase_invoice_lines\"");

        Assert.Contains("p.\"Currency\"", sql);
        Assert.DoesNotContain("\\", sql);
    }

    [Fact]
    public void Supplier_payment_query_uses_the_case_sensitive_exchange_rate_column()
    {
        Assert.Contains("SELECT \"Rate\" FROM exchange_rates", FinanceSql.ReadCurrentUsdToVesRate);
    }

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

    [Theory]
    [InlineData(10, 0.25, 13.33)]
    [InlineData(12.5, 0, 12.5)]
    [InlineData(1.005, 0, 1.01)]
    public void Suggested_price_uses_the_configured_gross_margin_and_decimal_rounding(
        decimal cost, decimal targetMargin, decimal expectedPrice)
        => Assert.Equal(expectedPrice, FinancePurchasePolicy.CalculateSuggestedPrice(cost, targetMargin));

    [Fact]
    public void Suggested_price_rejects_missing_cost_and_margin_outside_zero_to_one()
    {
        Assert.Null(FinancePurchasePolicy.CalculateSuggestedPrice(null, 0.25m));
        Assert.Throws<ArgumentOutOfRangeException>(() => FinancePurchasePolicy.CalculateSuggestedPrice(10m, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => FinancePurchasePolicy.CalculateSuggestedPrice(10m, -0.01m));
    }

    [Fact]
    public void Immediate_purchase_payment_must_settle_the_whole_invoice_using_the_current_rate()
    {
        var settlement = FinancePurchasePolicy.CalculateImmediateSettlement(4000m, "VES", 100m, "USD", 40m, 35m);

        Assert.Equal(4000m, settlement.InvoiceCurrencyAmount);
        Assert.Equal(40m, settlement.AppliedExchangeRate);
        Assert.Equal(100m, settlement.PaymentUsdEquivalent);
        Assert.Equal(14.2857m, settlement.FxDifferenceUsd);
        Assert.Throws<InvalidOperationException>(() => FinancePurchasePolicy.CalculateImmediateSettlement(4000m, "VES", 99m, "USD", 40m, 35m));
        Assert.Throws<InvalidOperationException>(() => FinancePurchasePolicy.CalculateImmediateSettlement(4000m, "VES", 100m, "USD", 0m, 35m));
    }

    [Fact]
    public void Manifest_declares_a_signed_installable_desktop_server_module()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "finance", "module.manifest.json");
        var manifest = ModuleManifestReader.ParseAndValidate(File.ReadAllText(path));

        Assert.Equal("finance", manifest.Id);
        Assert.Equal("1.0.8", manifest.Version.ToString(3));
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

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "finance", "finance-0002.json");
        var settingsMigration = ModuleMigrationReader.Parse(File.ReadAllText(settingsPath));
        Assert.Contains(settingsMigration.Operations.OfType<CreateModuleTable>(), table => table.Table == "price_suggestion_margins");
    }

    [Fact]
    public void Finance_price_suggestion_migration_keeps_the_checksum_from_the_published_1_0_3_package()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "finance", "finance-0002.json");
        var checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        Assert.Equal("119628a9f57dec16af3a56e69a2fa330271787b86174031b95e5ea6067955b72", checksum);
    }

    [Fact]
    public void Every_finance_endpoint_has_a_manifest_permission()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "finance", "module.manifest.json");
        var manifest = ModuleManifestReader.ParseAndValidate(File.ReadAllText(manifestPath));
        var permissions = new FinanceModule().GetEndpoints()
            .SelectMany(endpoint => endpoint.RequiredPermissions)
            .Distinct(StringComparer.Ordinal);

        Assert.All(permissions, permission => Assert.Contains(permission, manifest.Permissions));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.EndsWith("/post", StringComparison.Ordinal));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.EndsWith("/post-and-pay", StringComparison.Ordinal));
        var priceSuggestionQuery = Assert.Single(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "GET" && endpoint.Route.EndsWith("/price-suggestions", StringComparison.Ordinal));
        Assert.Contains("Purchase.View", priceSuggestionQuery.RequiredPermissions);
        Assert.Contains("PriceSuggestion.Manage", priceSuggestionQuery.RequiredPermissions);
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.EndsWith("/receipts", StringComparison.Ordinal));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.EndsWith("/payments", StringComparison.Ordinal));
        Assert.Contains(new FinanceModule().GetEndpoints(), endpoint => endpoint.Method == "POST" && endpoint.Route.Contains("receipts/{receiptId:guid}/reverse", StringComparison.Ordinal));
    }

    [Fact]
    public void Immediate_purchase_posting_requires_purchase_and_payment_permissions()
    {
        var endpoint = Assert.Single(new FinanceModule().GetEndpoints(), item => item.Method == "POST" && item.Route.EndsWith("/post-and-pay", StringComparison.Ordinal));

        Assert.Equal(new[] { "Purchase.Manage", "SupplierPayment.Manage" }, endpoint.RequiredPermissions);
    }

    [Fact]
    public void Finance_module_declares_price_suggestion_configuration_and_its_additive_migration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "finance", "module.manifest.json");
        var manifest = ModuleManifestReader.ParseAndValidate(File.ReadAllText(path));

        Assert.Contains("PriceSuggestion.Manage", manifest.Permissions);
        Assert.Contains("finance:0002", manifest.Migrations);
        Assert.Contains("finance:0003", manifest.Migrations);
        Assert.Equal("1.0.8", manifest.Version.ToString(3));

        var settingsMigrationPath = Path.Combine(AppContext.BaseDirectory, "finance", "finance-0003.json");
        var settingsMigration = ModuleMigrationReader.Parse(File.ReadAllText(settingsMigrationPath));
        var settingsTable = Assert.Single(settingsMigration.Operations.OfType<CreateModuleTable>());
        Assert.Equal("price_suggestion_settings", settingsTable.Table);
        Assert.Contains(settingsTable.Columns, column => column.Name == "cost_basis" && !column.Nullable);
    }

    [Fact]
    public void Cost_basis_defaults_to_weighted_average_and_an_older_request_preserves_the_saved_choice()
    {
        var policy = typeof(FinancePurchasePolicy).Assembly.GetType("Pos.Modules.Finance.FinancePriceSuggestionCostPolicy");
        Assert.NotNull(policy);
        var resolveBasis = policy!.GetMethod("ResolveBasis", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(resolveBasis);

        Assert.Equal("WeightedAverage", resolveBasis!.Invoke(null, [null, null]));
        Assert.Equal("WeightedAverage", resolveBasis.Invoke(null, ["WeightedAverage", "LastReceivedCost"]));
        Assert.Equal("LastReceivedCost", resolveBasis.Invoke(null, [null, "LastReceivedCost"]));
    }

    [Fact]
    public void Selected_cost_basis_never_falls_back_when_its_own_cost_is_unavailable()
    {
        var policy = typeof(FinancePurchasePolicy).Assembly.GetType("Pos.Modules.Finance.FinancePriceSuggestionCostPolicy");
        Assert.NotNull(policy);
        var chooseCost = policy!.GetMethod("Choose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(chooseCost);

        var unavailableLastCost = chooseCost!.Invoke(null, ["LastReceivedCost", 1.25m, null, true]);
        Assert.NotNull(unavailableLastCost);
        Assert.Null(unavailableLastCost!.GetType().GetProperty("UnitCost")!.GetValue(unavailableLastCost));
        Assert.NotNull(unavailableLastCost.GetType().GetProperty("UnavailableReason")!.GetValue(unavailableLastCost));

        var weightedAverageWithoutCost = chooseCost.Invoke(null, ["WeightedAverage", null, null, true]);
        Assert.NotNull(weightedAverageWithoutCost);
        Assert.Null(weightedAverageWithoutCost!.GetType().GetProperty("UnitCost")!.GetValue(weightedAverageWithoutCost));
    }

    [Fact]
    public void Explicit_initial_cost_is_used_and_identified_until_a_purchase_receipt_exists()
    {
        var policy = typeof(FinancePurchasePolicy).Assembly.GetType("Pos.Modules.Finance.FinancePriceSuggestionCostPolicy");
        Assert.NotNull(policy);
        var chooseCost = policy!.GetMethod("Choose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(chooseCost);

        var initialCost = chooseCost!.Invoke(null, ["LastReceivedCost", 1.2345m, null, false]);
        Assert.Equal(1.2345m, initialCost!.GetType().GetProperty("UnitCost")!.GetValue(initialCost));
        Assert.Equal("InitialCost", initialCost.GetType().GetProperty("SourceKind")!.GetValue(initialCost)?.ToString());

        var weightedInitialCost = chooseCost.Invoke(null, ["WeightedAverage", 1.2345m, null, false]);
        Assert.Equal("InitialCost", weightedInitialCost!.GetType().GetProperty("SourceKind")!.GetValue(weightedInitialCost)?.ToString());
        var weightedCostAfterReceipt = chooseCost.Invoke(null, ["WeightedAverage", 1.2345m, null, true]);
        Assert.Equal("WeightedAverage", weightedCostAfterReceipt!.GetType().GetProperty("SourceKind")!.GetValue(weightedCostAfterReceipt)?.ToString());

        var reversedHistoryOnly = chooseCost.Invoke(null, ["LastReceivedCost", 1.2345m, null, true]);
        Assert.Null(reversedHistoryOnly!.GetType().GetProperty("UnitCost")!.GetValue(reversedHistoryOnly));
        Assert.Contains("recepción completada", (string)reversedHistoryOnly.GetType().GetProperty("UnavailableReason")!.GetValue(reversedHistoryOnly)!, StringComparison.OrdinalIgnoreCase);

        var noInitialCost = chooseCost.Invoke(null, ["LastReceivedCost", null, null, false]);
        Assert.Contains("costo inicial", (string)noInitialCost!.GetType().GetProperty("UnavailableReason")!.GetValue(noInitialCost)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggestion_endpoint_includes_draft_invoice_lines_and_distinguishes_initial_cost_from_receipts()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot().FullName, "src", "Modules", "Pos.Modules.Finance", "FinanceModule.cs"));

        Assert.Contains("purchase_invoice_lines", source, StringComparison.Ordinal);
        Assert.Contains("ReadReceiptHistoryProductIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("selectedCost.SourceKind", source, StringComparison.Ordinal);
        var suggestion = typeof(FinanceModule).GetNestedType("PriceSuggestionInfo");
        Assert.NotNull(suggestion);
        Assert.Equal(typeof(string), suggestion!.GetProperty("CostSourceKind")?.PropertyType);
    }

    [Fact]
    public void Margin_contract_includes_an_optional_cost_basis_for_older_clients()
    {
        var request = typeof(FinanceModule).GetNestedType("PriceSuggestionMarginRequest");
        var settings = typeof(FinanceModule).GetNestedType("PriceSuggestionMarginSettings");

        Assert.NotNull(request);
        Assert.NotNull(settings);
        Assert.Equal(typeof(string), request!.GetProperty("CostBasis")?.PropertyType);
        Assert.Equal(typeof(string), settings!.GetProperty("CostBasis")?.PropertyType);

        var oldRequest = System.Text.Json.JsonSerializer.Deserialize<FinanceModule.PriceSuggestionMarginRequest>(
            "{\"price1MarginPercent\":25,\"price2MarginPercent\":35,\"price3MarginPercent\":45}");
        Assert.Null(oldRequest?.CostBasis);
    }

    [Fact]
    public void Suggestion_contract_keeps_the_inventory_cost_snapshot_separate_from_the_selected_basis()
    {
        var suggestion = typeof(FinanceModule).GetNestedType("PriceSuggestionInfo");
        Assert.NotNull(suggestion);
        Assert.Equal(typeof(decimal), suggestion!.GetProperty("UnitCost")?.PropertyType);
        Assert.Equal(typeof(decimal?), suggestion.GetProperty("ProductCostSnapshot")?.PropertyType);
        Assert.Equal(typeof(string), suggestion.GetProperty("CostBasis")?.PropertyType);
        Assert.Equal(typeof(string), suggestion.GetProperty("CostSourceDocumentNumber")?.PropertyType);
        Assert.Equal(typeof(DateTime?), suggestion.GetProperty("CostReceivedAtUtc")?.PropertyType);
        Assert.Equal(typeof(string), suggestion.GetProperty("CostSourceKind")?.PropertyType);
    }

    [Fact]
    public void Latest_received_cost_is_used_with_its_receipt_document_and_timestamp()
    {
        var policy = typeof(FinancePurchasePolicy).Assembly.GetType("Pos.Modules.Finance.FinancePriceSuggestionCostPolicy");
        Assert.NotNull(policy);
        var sourceType = typeof(FinancePurchasePolicy).Assembly.GetType("Pos.Modules.Finance.ReceivedCostSource");
        Assert.NotNull(sourceType);
        var source = Activator.CreateInstance(sourceType!, [2m, Guid.NewGuid(), "F-2026-008", new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc)]);
        var chooseCost = policy!.GetMethod("Choose", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        var selected = chooseCost!.Invoke(null, ["LastReceivedCost", 1.25m, source, true]);

        Assert.Equal(2m, selected!.GetType().GetProperty("UnitCost")!.GetValue(selected));
        var receiptSource = selected.GetType().GetProperty("ReceiptSource")!.GetValue(selected);
        Assert.Equal("F-2026-008", receiptSource!.GetType().GetProperty("DocumentNumber")!.GetValue(receiptSource));
        Assert.Equal("LastReceivedCost", selected.GetType().GetProperty("Basis")!.GetValue(selected));
    }

    [Fact]
    public void Latest_receipt_cost_query_excludes_incomplete_or_reversed_receipts_and_returns_latest_receipt_lines()
    {
        var policy = typeof(FinancePurchasePolicy).Assembly.GetType("Pos.Modules.Finance.FinancePriceSuggestionCostPolicy");
        Assert.NotNull(policy);
        var sql = (string?)policy!.GetField("LatestReceiptCostsSql", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetRawConstantValue();
        Assert.NotNull(sql);
        Assert.Contains("line.quantity, line.unit_cost", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line.cost_currency", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("completed_at_utc IS NOT NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reversed_at_utc IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PARTITION BY l.product_id", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Last_cost_averages_lines_in_the_latest_receipt_after_converting_to_the_product_currency()
    {
        var productId = Guid.NewGuid();
        var olderReceiptId = Guid.NewGuid();
        var latestReceiptId = Guid.NewGuid();
        var latestAt = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var lines = new[]
        {
            new ReceivedCostLine(productId, olderReceiptId, "F-OLD", latestAt.AddDays(-2), 5m, 100m, "VES", 40m),
            new ReceivedCostLine(productId, latestReceiptId, "F-LATEST", latestAt, 2m, 1m, "USD", 40m),
            new ReceivedCostLine(productId, latestReceiptId, "F-LATEST", latestAt, 3m, 2m, "USD", 40m)
        };

        var result = FinancePriceSuggestionCostPolicy.AggregateLatestReceiptLines(lines,
            new Dictionary<Guid, string> { [productId] = "VES" });

        var source = Assert.Single(result).Value;
        Assert.Equal(64m, source.UnitCost);
        Assert.Equal("F-LATEST", source.DocumentNumber);
        Assert.Equal(latestReceiptId, source.ReceiptId);
        Assert.Equal(latestAt, source.ReceivedAtUtc);
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

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("No se encontró la raíz del proyecto POS.");
    }
}
