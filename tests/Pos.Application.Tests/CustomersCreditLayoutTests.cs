using System.Xml.Linq;

namespace Pos.Application.Tests;

public sealed class CustomersCreditLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Customer_form_labels_each_field_and_uses_credit_language()
    {
        var document = LoadView("CustomersWindow.xaml");
        var text = string.Join(" ", document.Descendants().Attributes("Text").Select(x => x.Value));
        Assert.Contains("CLIENTES Y CRÉDITOS", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nombre", text);
        Assert.Contains("Documento", text);
        Assert.Contains("Teléfono", text);
        Assert.Contains("Límite de crédito (USD)", text);
        Assert.DoesNotContain("fiado", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Customer_list_shows_balance_and_available_credit()
    {
        var document = LoadView("CustomersWindow.xaml");
        var headers = document.Descendants().Attributes("Header").Select(x => x.Value).ToArray();
        Assert.Contains("Saldo (USD)", headers);
        Assert.Contains("Equivalente (VES)", headers);
        Assert.Contains("Disponible (USD)", headers);
    }

    [Fact]
    public void Customer_window_exposes_editing_for_selected_customer()
    {
        var document = LoadView("CustomersWindow.xaml");
        var text = string.Join(" ", document.Descendants().Attributes("Content").Select(x => x.Value));
        Assert.Contains("Editar cliente", text);
        Assert.DoesNotContain("Guardar cambios", text);
        var code = LoadSource("CustomersWindow.xaml.cs");
        Assert.Contains("new CustomerEditWindow(_viewModel, customer)", code);
        Assert.Contains("ShowDialog()", code);
        Assert.DoesNotContain("Actualiza los datos y guarda", code);
    }

    [Fact]
    public void Customer_edit_dialog_prefills_and_labels_editable_customer_data()
    {
        var document = LoadView("CustomerEditWindow.xaml");
        var text = string.Join(" ", document.Descendants().Attributes("Text").Select(x => x.Value));
        Assert.Contains("Nombre completo", text);
        Assert.Contains("Documento", text);
        Assert.Contains("Teléfono", text);
        Assert.Contains("Límite de crédito (USD)", text);
        var xamlNames = document.Descendants().Attributes().Where(x => x.Name.LocalName == "Name").Select(x => x.Value).ToArray();
        Assert.Contains("LimitBox", xamlNames);
        Assert.DoesNotContain("*", document.Descendants().Attributes("Height").Select(x => x.Value));
        var buttons = document.Descendants().Attributes("Content").Select(x => x.Value);
        Assert.Contains("Guardar cambios", buttons);
        Assert.Contains("Cancelar", buttons);
    }

    [Fact]
    public void Customer_can_be_removed_from_active_list_without_deleting_history()
    {
        var document = LoadView("CustomersWindow.xaml");
        var buttonNames = document.Descendants().Attributes("AutomationProperties.Name").Select(x => x.Value);
        Assert.Contains("Eliminar cliente seleccionado", buttonNames);
        var code = LoadSource("CustomersWindow.xaml.cs");
        Assert.Contains("DeactivateCustomerAsync", code);
        Assert.Contains("MessageBoxButton.YesNo", code);
        Assert.Contains("historial", code, StringComparison.OrdinalIgnoreCase);
        var api = LoadSource("../Pos.Desktop.Presentation/Contracts.cs");
        Assert.Contains("DeactivateCustomerAsync", api);
        var endpoint = LoadSource("../Pos.Server/Hito4Endpoints.cs");
        Assert.Contains("/api/v1/customers/{customerId:guid}/active", endpoint);
        Assert.Contains("customer_has_balance", endpoint);
        Assert.Contains("historyPreserved = true", endpoint);
    }

    [Fact]
    public void Closing_customer_account_refreshes_customer_balances()
    {
        var code = LoadSource("CustomersWindow.xaml.cs");
        var handler = code[code.IndexOf("private async void Receivables_Click", StringComparison.Ordinal)..];
        Assert.Contains("ShowDialog()", handler);
        Assert.Contains("await LoadAsync()", handler);
    }

    [Fact]
    public void Receivable_payment_reference_is_optional_and_labeled_as_reference()
    {
        var document = LoadView("ReceivablesWindow.xaml");
        var text = string.Join(" ", document.Descendants().Attributes("Text").Select(x => x.Value));
        Assert.Contains("Referencia (opcional)", text);
        Assert.DoesNotContain("Motivo del pago", text);
        Assert.DoesNotContain("Motivo", document.Descendants().Attributes("Header").Select(x => x.Value));
        var code = LoadSource("ReceivablesWindow.xaml.cs");
        Assert.DoesNotContain("reason.Length < 2", code);
    }

    [Fact]
    public void Customer_and_credit_windows_fit_a_standard_windows_work_area()
    {
        var customers = LoadView("CustomersWindow.xaml").Root!;
        var account = LoadView("ReceivablesWindow.xaml").Root!;
        Assert.True(double.Parse(customers.Attribute("Height")!.Value, System.Globalization.CultureInfo.InvariantCulture) <= 700);
        Assert.True(double.Parse(account.Attribute("Height")!.Value, System.Globalization.CultureInfo.InvariantCulture) <= 700);
        var accountHeaders = account.Descendants().Attributes("Header").Select(x => x.Value).ToArray();
        Assert.All(accountHeaders, header => Assert.False(string.IsNullOrWhiteSpace(header)));
        Assert.Contains("Referencia", accountHeaders);
    }

    [Fact]
    public void Credit_account_uses_human_readable_movement_and_balance_columns()
    {
        var document = LoadView("ReceivablesWindow.xaml");
        var headers = document.Descendants().Attributes("Header").Select(x => x.Value).ToArray();
        Assert.Contains("Movimiento", headers);
        Assert.Contains("Referencia", headers);
        Assert.Contains("Saldo después", headers);
    }

    [Fact]
    public void Credit_sale_movement_shows_purchased_products_and_usd_values_from_sale_time()
    {
        var document = LoadView("ReceivablesWindow.xaml");
        var headers = document.Descendants().Attributes("Header").Select(x => x.Value).ToArray();
        Assert.Contains("Artículos · valor histórico USD", headers);

        var windowCode = LoadSource("ReceivablesWindow.xaml.cs");
        Assert.Contains("SaleItemsLabel", windowCode);
        Assert.Contains("ReceivableSaleItemPresentation.Format", windowCode);

        var contracts = LoadSource("../Pos.Desktop.Presentation/Contracts.cs");
        Assert.Contains("ReceivableSaleItemInfo", contracts);
        Assert.Contains("SaleItems", contracts);

        var endpoint = LoadSource("../Pos.Server/Hito4Endpoints.cs");
        Assert.Contains("db.SaleLines", endpoint);
        Assert.Contains("AppliedUsdToVesRate", endpoint);
        Assert.Contains("saleItems", endpoint);
    }

    [Fact]
    public void Receivable_sale_item_summary_preserves_sale_time_usd_amount()
    {
        var item = new Pos.Desktop.Presentation.ReceivableSaleItemInfo("123", "Harina PAN", 2m, 8.50m);
        var summary = Pos.Desktop.Presentation.ReceivableSaleItemPresentation.Format([item]);
        Assert.Equal("Harina PAN × 2 · USD 8,50", summary);
        Assert.Equal("—", Pos.Desktop.Presentation.ReceivableSaleItemPresentation.Format([]));
        Assert.Equal("SKU-2 × 1 · USD no disponible (sin tasa histórica)",
            Pos.Desktop.Presentation.ReceivableSaleItemPresentation.Format([new Pos.Desktop.Presentation.ReceivableSaleItemInfo("SKU-2", "", 1m, null)]));
    }

    [Fact]
    public void Customer_credit_available_is_limit_minus_open_balance()
    {
        var customer = new Pos.Desktop.Presentation.CustomerInfo(Guid.NewGuid(), "Cliente", null, null, "VES", 100m)
        {
            Balance = 35m
        };
        Assert.Equal(65m, customer.AvailableCredit);
    }

    [Fact]
    public void Credit_accounts_are_always_usd_and_show_a_ves_reference()
    {
        var customers = LoadView("CustomersWindow.xaml");
        var receivables = LoadView("ReceivablesWindow.xaml");
        var customerText = string.Join(" ", customers.Descendants().Attributes("Text").Select(x => x.Value));
        var accountText = string.Join(" ", receivables.Descendants().Attributes("Text").Select(x => x.Value));
        Assert.Contains("USD", customerText);
        var customerNames = customers.Descendants().Attributes().Where(x => x.Name.LocalName == "Name").Select(x => x.Value);
        Assert.DoesNotContain(customerNames, x => x == "CurrencyBox");
        Assert.Contains("equivalente en VES", accountText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("BalanceVesEquivalent", typeof(Pos.Desktop.Presentation.CustomerInfo).GetProperty("BalanceVesEquivalent")?.Name);
        var accountNames = receivables.Descendants().Attributes().Where(x => x.Name.LocalName == "Name").Select(x => x.Value);
        Assert.Contains(accountNames, x => x == "PaymentCurrencyBox");
        Assert.Contains("Se permiten pagos parciales hasta cubrir el saldo.", accountText);
    }

    private static XDocument LoadView(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "Pos.Desktop", fileName));
    }

    private static string LoadSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.GetFullPath(Path.Combine(directory!.FullName, "src", "Pos.Desktop", fileName)));
    }
}
