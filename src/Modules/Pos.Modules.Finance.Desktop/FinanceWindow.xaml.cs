using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;

namespace Pos.Modules.Finance.Desktop;

public partial class FinanceWindow : Window
{
    private const string ModuleId = "finance";
    private readonly PosDesktopModuleContext _context;
    private readonly ObservableCollection<PurchaseDraftLine> _draftLines = [];
    private readonly Dictionary<Guid, SupplierPaymentRequest> _pendingPaymentRequests = [];
    private PurchaseSummaryClientInfo? _selectedPurchase;
    private PurchaseDetailClientInfo? _purchaseDetail;
    private Guid? _editingDraftId;
    private bool _loadingSelection;
    private bool _isInitializing = true;

    public FinanceWindow(PosDesktopModuleContext context, bool openSummary = false)
    {
        InitializeComponent();
        _context = context;
        DraftLinesGrid.ItemsSource = _draftLines;

        var canViewSuppliers = context.GrantedPermissions.Contains("Supplier.View");
        var canManageSuppliers = context.GrantedPermissions.Contains("Supplier.Manage");
        var canViewPurchases = context.GrantedPermissions.Contains("Purchase.View");
        var canManagePurchases = context.GrantedPermissions.Contains("Purchase.Manage");
        var canManagePayments = context.GrantedPermissions.Contains("SupplierPayment.Manage");
        var canManagePriceSuggestions = context.GrantedPermissions.Contains("PriceSuggestion.Manage");
        var canViewFinance = context.GrantedPermissions.Contains("Finance.Reports");

        SuppliersTab.Visibility = canViewSuppliers ? Visibility.Visible : Visibility.Collapsed;
        PurchasesTab.Visibility = canViewPurchases ? Visibility.Visible : Visibility.Collapsed;
        SummaryTab.Visibility = canViewFinance ? Visibility.Visible : Visibility.Collapsed;
        MarginSettingsTab.Visibility = canManagePriceSuggestions ? Visibility.Visible : Visibility.Collapsed;
        CreateSupplierButton.Visibility = canManageSuppliers ? Visibility.Visible : Visibility.Collapsed;
        ToggleSupplierButton.Visibility = canManageSuppliers ? Visibility.Visible : Visibility.Collapsed;
        DraftEditor.Visibility = canManagePurchases ? Visibility.Visible : Visibility.Collapsed;
        NewDraftButton.Visibility = canManagePurchases ? Visibility.Visible : Visibility.Collapsed;
        EditDraftButton.Visibility = canManagePurchases ? Visibility.Visible : Visibility.Collapsed;
        PostSelectedPurchaseButton.Visibility = canManagePurchases ? Visibility.Visible : Visibility.Collapsed;
        ReceiveSelectedPurchaseButton.Visibility = canManagePurchases ? Visibility.Visible : Visibility.Collapsed;
        ReverseReceiptButton.Visibility = canManagePurchases ? Visibility.Visible : Visibility.Collapsed;
        ReversePurchaseButton.Visibility = canManagePurchases ? Visibility.Visible : Visibility.Collapsed;
        ViewPriceSuggestionsButton.Visibility = canManagePriceSuggestions ? Visibility.Visible : Visibility.Collapsed;
        PaySelectedPurchaseButton.Visibility = canManagePayments ? Visibility.Visible : Visibility.Collapsed;
        ReversePaymentButton.Visibility = canManagePayments ? Visibility.Visible : Visibility.Collapsed;
        DraftEditor.IsEnabled = false;
        DraftEditor.Visibility = Visibility.Collapsed;
        InvoiceWorkspace.Visibility = Visibility.Visible;
        DraftLinesEmptyLabel.Visibility = Visibility.Visible;
        DraftIssueDatePicker.SelectedDate = DateTime.Today;
        UpdateActionButtons();
        if (openSummary && canViewFinance) FinanceTabs.SelectedItem = SummaryTab;
        else if (!canViewSuppliers && canViewPurchases) FinanceTabs.SelectedItem = PurchasesTab;
        else if (!canViewSuppliers && !canViewPurchases && canViewFinance) FinanceTabs.SelectedItem = SummaryTab;
        _isInitializing = false;
        UpdateDraftTotal();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_context.GrantedPermissions.Contains("Supplier.View")) await LoadSuppliersAsync();
        if (_context.GrantedPermissions.Contains("Purchase.View")) await LoadPurchasesAsync();
        if (_context.GrantedPermissions.Contains("Finance.Reports")) await LoadSummaryAsync();
        if (_context.GrantedPermissions.Contains("PriceSuggestion.Manage")) await LoadPriceSuggestionMarginsAsync();
    }

    private async void CreateSupplier_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SupplierCodeBox.Text) || string.IsNullOrWhiteSpace(SupplierNameBox.Text))
        { SetStatus("Indica el código y la razón social del proveedor.", true); return; }
        try
        {
            CreateSupplierButton.IsEnabled = false;
            await _context.ApiClient.PostModuleAsync<SupplierRequest, SupplierClientInfo>(ModuleId, "suppliers",
                new(SupplierCodeBox.Text.Trim(), SupplierNameBox.Text.Trim(), NullIfEmpty(SupplierTaxIdBox.Text),
                    NullIfEmpty(SupplierPhoneBox.Text), NullIfEmpty(SupplierEmailBox.Text), NullIfEmpty(SupplierAddressBox.Text)));
            SupplierCodeBox.Clear(); SupplierNameBox.Clear(); SupplierTaxIdBox.Clear(); SupplierPhoneBox.Clear(); SupplierEmailBox.Clear(); SupplierAddressBox.Clear();
            await LoadSuppliersAsync();
            SetStatus("Proveedor agregado.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
        finally { CreateSupplierButton.IsEnabled = true; }
    }

    private async void RefreshSuppliers_Click(object sender, RoutedEventArgs e) => await LoadSuppliersAsync();

    private void SupplierGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionButtons();
    private void RelatedSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionButtons();
    private void DraftCurrencyPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing) UpdateDraftTotal();
    }

    private void DraftFreightBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing) UpdateDraftTotal();
    }

    private async void ToggleSupplier_Click(object sender, RoutedEventArgs e)
    {
        if (SupplierGrid.SelectedItem is not SupplierClientInfo supplier) return;
        try
        {
            await _context.ApiClient.PatchModuleAsync<SetSupplierActiveRequest>(ModuleId,
                $"suppliers/{supplier.Id:D}/active", new(!supplier.IsActive));
            await LoadSuppliersAsync();
            SetStatus(supplier.IsActive ? "Proveedor desactivado. Se conservaron sus facturas." : "Proveedor activado.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async void RefreshPurchases_Click(object sender, RoutedEventArgs e) => await LoadPurchasesAsync();
    private async void RefreshSummary_Click(object sender, RoutedEventArgs e) => await LoadSummaryAsync();

    private async void SavePriceMargins_Click(object sender, RoutedEventArgs e)
    {
        if (!TryMarginPercent(Price1MarginBox.Text, out var price1) || !TryMarginPercent(Price2MarginBox.Text, out var price2) ||
            !TryMarginPercent(Price3MarginBox.Text, out var price3))
        { SetStatus("Escribe porcentajes entre 0 y menos de 100, con hasta dos decimales. Deja vacío el nivel que no tendrá sugerencia.", true); return; }
        try
        {
            SavePriceMarginsButton.IsEnabled = false;
            await _context.ApiClient.PutModuleAsync(ModuleId, "price-suggestions/margins",
                new PriceSuggestionMarginRequest(price1, price2, price3));
            await LoadPriceSuggestionMarginsAsync();
            SetStatus("Márgenes guardados. Las sugerencias aparecerán para artículos recibidos que tengan costo promedio.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
        finally { SavePriceMarginsButton.IsEnabled = true; }
    }

    private async Task LoadPriceSuggestionMarginsAsync()
    {
        try
        {
            var margins = await _context.ApiClient.GetModuleAsync<PriceSuggestionMarginSettings>(ModuleId, "price-suggestions/margins");
            if (margins is null) return;
            Price1MarginBox.Text = FormatMarginPercent(margins.Price1MarginPercent);
            Price2MarginBox.Text = FormatMarginPercent(margins.Price2MarginPercent);
            Price3MarginBox.Text = FormatMarginPercent(margins.Price3MarginPercent);
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async void PriceSuggestions_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPurchase is null || _context.ReportApiClient is null) return;
        var dialog = new PriceSuggestionWindow(_context, _selectedPurchase.Id, _selectedPurchase.DocumentLabel) { Owner = this };
        dialog.ShowDialog();
    }

    private void NewDraft_Click(object sender, RoutedEventArgs e)
    {
        _editingDraftId = null;
        DraftHeading.Text = "Nueva factura";
        DraftSupplierPicker.SelectedItem = null;
        DraftDocumentTypePicker.SelectedIndex = 0;
        DraftDocumentNumberBox.Clear(); DraftControlNumberBox.Clear();
        DraftCurrencyPicker.SelectedIndex = 0; DraftExchangeRateBox.Text = "1";
        DraftIssueDatePicker.SelectedDate = DateTime.Today; DraftDueDatePicker.SelectedDate = null;
        DraftFreightBox.Text = "0"; DraftPaymentTermPicker.SelectedIndex = 0;
        DraftProductPicker.ItemsSource = null; DraftProductPicker.Text = string.Empty;
        DraftQuantityBox.Text = "1"; DraftUnitPriceBox.Clear(); DraftDiscountBox.Text = "0";
        DraftTaxBox.Text = "0"; DraftTaxRecoverableCheck.IsChecked = true;
        _draftLines.Clear(); DraftLinesGrid.SelectedItem = null;
        SetDraftLineStatus("Todavía no hay artículos. Agrégalos arriba para poder guardar la factura.");
        UpdateDraftTotal();
        DraftEditor.IsEnabled = true;
        ShowDraftEditor();
        DraftDocumentNumberBox.Focus();
        SetStatus("Completa los tres pasos del formulario y guarda el borrador. Aún no afecta inventario ni saldo.");
    }

    private async void EditDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPurchase is not { Status: "Draft" } purchase || _purchaseDetail is null) return;
        _editingDraftId = purchase.Id;
        DraftHeading.Text = $"Editar borrador · {purchase.DocumentNumber}";
        var header = _purchaseDetail.Header;
        DraftSupplierPicker.SelectedValuePath = nameof(SupplierClientInfo.Id);
        DraftSupplierPicker.SelectedValue = header.SupplierId;
        DraftDocumentTypePicker.SelectedIndex = header.DocumentType == "DebitNote" ? 1 : 0;
        DraftDocumentNumberBox.Text = header.DocumentNumber;
        DraftControlNumberBox.Text = header.ControlNumber ?? string.Empty;
        DraftCurrencyPicker.SelectedIndex = header.Currency == "VES" ? 1 : 0;
        DraftExchangeRateBox.Text = header.ExchangeRate.ToString(CultureInfo.CurrentCulture);
        DraftIssueDatePicker.SelectedDate = header.IssueDate.ToDateTime(TimeOnly.MinValue);
        DraftDueDatePicker.SelectedDate = header.DueDate?.ToDateTime(TimeOnly.MinValue);
        DraftFreightBox.Text = header.Freight.ToString(CultureInfo.CurrentCulture);
        DraftPaymentTermPicker.SelectedIndex = header.PaymentTerm == "Immediate" ? 1 : 0;
        _draftLines.Clear();
        foreach (var line in _purchaseDetail.Lines)
            _draftLines.Add(new(line.ProductId, line.Sku, line.Name, line.Quantity, line.UnitPrice, line.Discount, line.Tax, line.TaxRecoverable));
        DraftLinesGrid.SelectedItem = null;
        SetDraftLineStatus($"{_draftLines.Count} artículo(s) en el borrador. Selecciona una línea para quitarla si necesitas corregirla.");
        UpdateDraftTotal(); DraftEditor.IsEnabled = true; ShowDraftEditor();
        SetStatus("Edita el borrador y guárdalo. Una factura contabilizada no se puede editar.");
        await Task.CompletedTask;
    }

    private async void SearchProducts_Click(object sender, RoutedEventArgs e)
    {
        var query = DraftProductPicker.Text.Trim();
        if (query.Length < 2) { SetDraftLineStatus("Escribe al menos dos letras o un código y pulsa Buscar.", true); SetStatus(DraftLineStatus.Text, true); return; }
        try
        {
            SearchProductsButton.IsEnabled = false;
            var products = await _context.SearchProductsAsync(query, CancellationToken.None);
            DraftProductPicker.ItemsSource = products;
            DraftProductPicker.DisplayMemberPath = nameof(ProductInfo.Name);
            if (products.Count == 0) { SetDraftLineStatus("No se encontraron artículos activos para esa búsqueda.", true); SetStatus(DraftLineStatus.Text, true); }
            else { DraftProductPicker.IsDropDownOpen = true; SetDraftLineStatus($"Se encontraron {products.Count} artículos. Selecciona el que aparece en la factura."); SetStatus(DraftLineStatus.Text); }
        }
        catch (Exception exception) { SetDraftLineStatus(UserMessage(exception), true); SetStatus(UserMessage(exception), true); }
        finally { SearchProductsButton.IsEnabled = true; }
    }

    private void AddDraftLine_Click(object sender, RoutedEventArgs e)
    {
        if (DraftProductPicker.SelectedItem is not ProductInfo product)
        { SetDraftLineStatus("Primero busca y selecciona un artículo de la factura.", true); SetStatus(DraftLineStatus.Text, true); return; }
        if (!TryDecimal(DraftQuantityBox.Text, out var quantity) || quantity <= 0m ||
            !TryDecimal(DraftUnitPriceBox.Text, out var unitPrice) || unitPrice < 0m ||
            !TryDecimal(DraftDiscountBox.Text, out var discount) || discount < 0m ||
            !TryDecimal(DraftTaxBox.Text, out var tax) || tax < 0m)
        { SetDraftLineStatus("Revisa cantidad, precio, descuento e impuesto; deben ser números válidos.", true); SetStatus(DraftLineStatus.Text, true); return; }
        if (_draftLines.Any(line => line.ProductId == product.Id))
        { SetDraftLineStatus("Ese artículo ya está en la factura. Selecciona y quita su línea para volver a agregarlo con otros datos.", true); SetStatus(DraftLineStatus.Text, true); return; }
        if (discount > quantity * unitPrice)
        { SetDraftLineStatus("El descuento no puede superar el importe de los artículos de esta línea.", true); SetStatus(DraftLineStatus.Text, true); return; }
        _draftLines.Add(new(product.Id, product.Sku, product.Name, quantity, unitPrice, discount, tax,
            DraftTaxRecoverableCheck.IsChecked == true));
        DraftProductPicker.SelectedItem = null; DraftProductPicker.Text = string.Empty;
        DraftQuantityBox.Text = "1"; DraftUnitPriceBox.Clear(); DraftDiscountBox.Text = "0"; DraftTaxBox.Text = "0";
        DraftTaxRecoverableCheck.IsChecked = true;
        DraftLinesGrid.SelectedItem = null;
        UpdateDraftTotal();
        SetDraftLineStatus($"Artículo agregado. La factura ahora tiene {_draftLines.Count} línea(s). Revisa el total y guarda el borrador.");
        SetStatus(DraftLineStatus.Text);
    }

    private void DraftLinesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveDraftLineButton.IsEnabled = DraftLinesGrid.SelectedItem is PurchaseDraftLine;

    private void RemoveDraftLine_Click(object sender, RoutedEventArgs e)
    {
        if (DraftLinesGrid.SelectedItem is not PurchaseDraftLine selected) return;
        _draftLines.Remove(selected);
        DraftLinesGrid.SelectedItem = null;
        UpdateDraftTotal();
        SetDraftLineStatus(_draftLines.Count == 0
            ? "Se quitó la línea. Agrega al menos un artículo antes de guardar el borrador."
            : $"Línea quitada. La factura tiene {_draftLines.Count} línea(s); puedes volver a agregar el artículo corregido.");
    }

    private void CancelDraft_Click(object sender, RoutedEventArgs e)
    {
        var wasEditing = _editingDraftId.HasValue;
        _editingDraftId = null;
        _draftLines.Clear();
        UpdateDraftTotal();
        DraftEditor.IsEnabled = false;
        DraftEditor.Visibility = Visibility.Collapsed;
        InvoiceWorkspace.Visibility = Visibility.Visible;
        SetStatus(wasEditing
            ? "Se descartaron los cambios sin guardar. El borrador original permanece intacto."
            : "Se descartó la factura sin guardar. No se modificó el inventario ni la cuenta por pagar.");
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildDraftRequest(out var request, out var message)) { SetStatus(message, true); return; }
        try
        {
            SaveDraftButton.IsEnabled = false;
            var savedPurchaseId = _editingDraftId;
            if (_editingDraftId is Guid id)
                await _context.ApiClient.PutModuleAsync(ModuleId, $"purchases/{id:D}", request);
            else
            {
                var created = await _context.ApiClient.PostModuleAsync<PurchaseDraftRequest, PurchaseOperationResult>(ModuleId, "purchases", request);
                savedPurchaseId = created?.Id;
            }
            DraftEditor.IsEnabled = false; DraftEditor.Visibility = Visibility.Collapsed;
            InvoiceWorkspace.Visibility = Visibility.Visible; _editingDraftId = null;
            await LoadPurchasesAsync(savedPurchaseId, request);
            SetStatus("Borrador guardado. Todavía no se han modificado inventario ni cuentas por pagar.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
        finally { SaveDraftButton.IsEnabled = true; }
    }

    private async void PostPurchase_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPurchase is not { Status: "Draft" } purchase) return;
        if (purchase.PaymentTerm == "Immediate")
        {
            if (!_context.GrantedPermissions.Contains("SupplierPayment.Manage"))
            { SetStatus("Para contabilizar una compra de contado necesitas permiso de compras y de pagos a proveedores.", true); return; }
            _pendingPaymentRequests.TryGetValue(purchase.Id, out var pending);
            var dialog = new SupplierPaymentWindow(this, purchase.SupplierName, purchase.DocumentLabel,
                purchase.Currency, purchase.Total, pending, isImmediateSettlement: true);
            if (dialog.ShowDialog() != true) return;
            var payment = FinancePaymentRetryPolicy.CreateOrReuse(purchase.Id, purchase.Id, pending,
                dialog.Amount, dialog.Currency, dialog.Method, dialog.ReceivingAccount, dialog.Reference);
            _pendingPaymentRequests[purchase.Id] = payment;
            try
            {
                await _context.ApiClient.PostModuleAsync<ImmediatePurchasePostingRequest, PurchaseOperationResult>(ModuleId,
                    $"purchases/{purchase.Id:D}/post-and-pay", new(payment));
                _pendingPaymentRequests.Remove(purchase.Id);
                await LoadPurchasesAsync();
                SetStatus("Factura contabilizada y pagada de contado en una sola operación. El inventario espera la recepción.");
            }
            catch (Exception exception)
            {
                await LoadPurchasesAsync();
                SetStatus($"{UserMessage(exception)} Si no sabes si se registró, vuelve a intentar con los mismos datos; el POS conservará la misma clave.", true);
            }
            return;
        }
        if (MessageBox.Show(this, "Al contabilizar se registra la cuenta por pagar. El inventario cambia cuando registres la recepción.",
            "Contabilizar factura", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await _context.ApiClient.PostModuleAsync(ModuleId, $"purchases/{purchase.Id:D}/post", new EmptyRequest());
            await LoadPurchasesAsync(); SetStatus("Factura contabilizada. La deuda quedó registrada; el inventario espera la recepción.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async void ReceivePurchase_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPurchase is not { Status: "Posted" or "Partial" or "Paid" } purchase || _purchaseDetail is null) return;
        try
        {
            var existingReceipts = await _context.ApiClient.GetModuleAsync<List<PurchaseReceiptClientInfo>>(
                ModuleId, $"purchases/{purchase.Id:D}/receipts") ?? [];
            var pendingReceipt = existingReceipts.FirstOrDefault(receipt =>
                !receipt.Header.CompletedAtUtc.HasValue && !receipt.Header.ReversedAtUtc.HasValue);
            PurchaseReceiptRequest request;
            if (pendingReceipt is not null)
            {
                if (MessageBox.Show(this,
                    "Hay una recepción que empezó pero no se confirmó. ¿Quieres reintentar esa misma operación? Se usará la misma clave para evitar duplicar existencias.",
                    "Reintentar recepción pendiente", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                request = FinanceReceiptRetryPolicy.CreateRetryRequest(pendingReceipt);
            }
            else
            {
                var remainingLines = _purchaseDetail.Lines
                    .Select(line => new ReceiptLineDraft(line.Id, line.Sku, line.Name, line.Quantity,
                        Math.Max(0m, line.Quantity - line.ReceivedQuantity)))
                    .Where(line => line.Remaining > 0m).ToList();
                if (remainingLines.Count == 0) { SetStatus("Esta factura ya está recibida por completo."); return; }
                var receiveWindow = new ReceiptQuantityWindow(remainingLines) { Owner = this };
                if (receiveWindow.ShowDialog() != true) return;
                var receiptLines = receiveWindow.Lines.Where(line => line.QuantityToReceive > 0m)
                    .Select(line => new PurchaseReceiptLineRequest(line.InvoiceLineId, line.QuantityToReceive)).ToArray();
                if (receiptLines.Length == 0) { SetStatus("Indica al menos una cantidad para recibir.", true); return; }
                request = new(Guid.NewGuid().ToString("N"), null, receiptLines);
            }
            await _context.ApiClient.PostModuleAsync<PurchaseReceiptRequest, PurchaseOperationResult>(ModuleId,
                $"purchases/{purchase.Id:D}/receipts", request);
            await LoadPurchasesAsync(); SetStatus("Recepción registrada. Se actualizaron existencias y costo promedio una sola vez.");
        }
        catch (Exception exception) { SetStatus($"{UserMessage(exception)} Si el servidor alcanzó a iniciar la recepción, vuelve a usar «Registrar recepción» para reintentarla sin duplicar existencias.", true); }
    }

    private async void PayPurchase_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPurchase is not { Status: "Posted" or "Partial" or "Paid" } purchase || purchase.Balance <= 0m) return;
        _pendingPaymentRequests.TryGetValue(purchase.Id, out var pending);
        var dialog = new SupplierPaymentWindow(this, purchase.SupplierName, purchase.DocumentLabel,
            purchase.Currency, purchase.Balance, pending);
        if (dialog.ShowDialog() != true) return;

        var request = FinancePaymentRetryPolicy.CreateOrReuse(purchase.Id, purchase.Id, pending,
            dialog.Amount, dialog.Currency, dialog.Method, dialog.ReceivingAccount, dialog.Reference);
        _pendingPaymentRequests[purchase.Id] = request;
        try
        {
            await _context.ApiClient.PostModuleAsync<SupplierPaymentRequest, PurchaseOperationResult>(ModuleId,
                $"purchases/{purchase.Id:D}/payments", request);
            _pendingPaymentRequests.Remove(purchase.Id);
            await LoadPurchasesAsync(); SetStatus("Abono registrado. La deuda conserva su moneda; la conversión quedó guardada en el historial.");
        }
        catch (Exception exception)
        {
            var message = UserMessage(exception);
            await LoadPurchasesAsync();
            SetStatus($"{message} Si no sabes si se registró, vuelve a abrir el mismo abono con los mismos datos para consultar el resultado sin duplicarlo.", true);
        }
    }

    private async void ReverseReceipt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPurchase is null || ReceiptsGrid.SelectedItem is not PurchaseReceiptClientInfo receipt || receipt.Header.ReversedAtUtc.HasValue) return;
        var reason = TextEntryWindow.Show(this, "Reversar recepción", [new("Motivo del reverso", "motivo", string.Empty)]);
        if (reason is null || string.IsNullOrWhiteSpace(reason["motivo"])) return;
        try
        {
            await _context.ApiClient.PostModuleAsync<ReverseRequest>(ModuleId,
                $"purchases/{_selectedPurchase.Id:D}/receipts/{receipt.Header.Id:D}/reverse", new(reason["motivo"].Trim()));
            await LoadPurchaseSelectionAsync(); SetStatus("Recepción reversada. Solo se permite si no hubo movimientos posteriores de esos artículos.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async void ReversePayment_Click(object sender, RoutedEventArgs e)
    {
        if (PaymentsGrid.SelectedItem is not SupplierPaymentClientInfo payment || payment.ReversedAtUtc.HasValue) return;
        var reason = TextEntryWindow.Show(this, "Reversar abono", [new("Motivo del reverso", "motivo", string.Empty)]);
        if (reason is null || string.IsNullOrWhiteSpace(reason["motivo"])) return;
        try
        {
            await _context.ApiClient.PostModuleAsync<ReverseRequest>(ModuleId, $"payments/{payment.Id:D}/reverse", new(reason["motivo"].Trim()));
            await LoadPurchasesAsync(); SetStatus("Abono reversado; se conservó el movimiento original y se agregó su reverso.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async void ReversePurchase_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPurchase is null || _selectedPurchase.Status == "Reversed") return;
        var reason = TextEntryWindow.Show(this, "Reversar factura", [new("Motivo del reverso", "motivo", string.Empty)]);
        if (reason is null || string.IsNullOrWhiteSpace(reason["motivo"])) return;
        try
        {
            await _context.ApiClient.PostModuleAsync<ReverseRequest>(ModuleId,
                $"purchases/{_selectedPurchase.Id:D}/reverse", new(reason["motivo"].Trim()));
            await LoadPurchasesAsync(); SetStatus("Factura reversada mediante un asiento compensatorio.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async void PurchaseGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection) return;
        _selectedPurchase = PurchaseGrid.SelectedItem as PurchaseSummaryClientInfo;
        await LoadPurchaseSelectionAsync();
    }

    private void PaymentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
        if (PaymentsGrid.SelectedItem is not SupplierPaymentClientInfo payment)
        {
            PaymentDetailLabel.Text = "Selecciona un abono para ver cuenta, referencia, tasa aplicada y diferencia cambiaria.";
            return;
        }

        var reference = string.IsNullOrWhiteSpace(payment.Reference) ? "Sin referencia" : $"Referencia: {payment.Reference}";
        var fxDifference = payment.FxDifference.HasValue ? $"USD {payment.FxDifference.Value:N4}" : "No disponible";
        var state = payment.ReversedAtUtc.HasValue ? $"Reversado el {payment.ReversedAtUtc.Value.ToLocalTime():g}" : "Vigente";
        PaymentDetailLabel.Text = $"Cuenta receptora: {payment.ReceivingAccount} · {reference} · Tasa aplicada: {payment.AppliedExchangeRate:N4} · Diferencia cambiaria: {fxDifference} · {state}";
    }

    private async Task LoadSuppliersAsync()
    {
        try
        {
            var suppliers = await _context.ApiClient.GetModuleAsync<List<SupplierClientInfo>>(ModuleId, "suppliers") ?? [];
            SupplierGrid.ItemsSource = suppliers;
            DraftSupplierPicker.ItemsSource = suppliers.Where(item => item.IsActive).ToArray();
            DraftSupplierPicker.DisplayMemberPath = nameof(SupplierClientInfo.LegalName);
            DraftSupplierPicker.SelectedValuePath = nameof(SupplierClientInfo.Id);
            SetStatus($"Proveedores: {suppliers.Count}.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async Task LoadPurchasesAsync(Guid? preferredSelectionId = null, PurchaseDraftRequest? preferredDraft = null)
    {
        try
        {
            _loadingSelection = true;
            var selectedId = _selectedPurchase?.Id;
            var purchases = await _context.ApiClient.GetModuleAsync<List<PurchaseSummaryClientInfo>>(ModuleId, "purchases") ?? [];
            PurchaseGrid.ItemsSource = purchases;
            _selectedPurchase = FinancePurchaseSelectionPolicy.Select(purchases, preferredSelectionId, selectedId, preferredDraft);
            PurchaseGrid.SelectedItem = _selectedPurchase;
            _loadingSelection = false;
            await LoadPurchaseSelectionAsync();
            SetStatus($"Facturas registradas: {purchases.Count}.");
        }
        catch (Exception exception) { _loadingSelection = false; SetStatus(UserMessage(exception), true); }
    }

    private async Task LoadPurchaseSelectionAsync()
    {
        UpdateActionButtons();
        var purchase = _selectedPurchase;
        if (purchase is null)
        {
            _purchaseDetail = null; PurchaseDetailsGrid.ItemsSource = null; PaymentsGrid.ItemsSource = null; ReceiptsGrid.ItemsSource = null;
            PurchaseSummaryLabel.Text = "Selecciona una factura para ver su detalle y operaciones.";
            PaymentHistoryLabel.Text = string.Empty;
            PaymentDetailLabel.Text = "Selecciona una factura y luego un abono para consultar su trazabilidad.";
            return;
        }
        try
        {
            _purchaseDetail = await _context.ApiClient.GetModuleAsync<PurchaseDetailClientInfo>(ModuleId, $"purchases/{purchase.Id:D}");
            PurchaseDetailsGrid.ItemsSource = _purchaseDetail?.Lines ?? [];
            var due = purchase.DueDate?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) ?? "sin fecha de vencimiento";
            PurchaseSummaryLabel.Text = $"Proveedor: {purchase.SupplierName} · {FinanceDisplayText.DocumentType(purchase.DocumentType)} {purchase.DocumentNumber} · Emitida {purchase.IssueDate:dd/MM/yyyy} · vence {due} · Total {purchase.Currency} {purchase.Total:N2} · Pagado {purchase.Currency} {purchase.PaidAmount:N2} · Saldo {purchase.Currency} {purchase.Balance:N2} · Estado: {purchase.StatusLabel}";
            var payments = await _context.ApiClient.GetModuleAsync<List<SupplierPaymentClientInfo>>(ModuleId, $"purchases/{purchase.Id:D}/payments") ?? [];
            PaymentsGrid.ItemsSource = payments;
            PaymentsGrid.SelectedItem = null;
            var receipts = await _context.ApiClient.GetModuleAsync<List<PurchaseReceiptClientInfo>>(ModuleId, $"purchases/{purchase.Id:D}/receipts") ?? [];
            ReceiptsGrid.ItemsSource = receipts;
            PaymentHistoryLabel.Text = $"Abonos registrados: {payments.Count}. Selecciona uno para ver sus datos completos. · Recepciones: {receipts.Count}.";
            PaymentDetailLabel.Text = "Selecciona un abono para ver cuenta, referencia, tasa aplicada y diferencia cambiaria.";
            UpdateActionButtons();
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private async Task LoadSummaryAsync()
    {
        try
        {
            var summary = await _context.ApiClient.GetModuleAsync<FinanceSummaryClientInfo>(ModuleId, "summary");
            SummaryGrid.ItemsSource = summary?.Balances ?? [];
            var obligations = summary?.Obligations ?? [];
            OpenPayablesGrid.ItemsSource = obligations;
            OpenPayablesEmptyLabel.Visibility = obligations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus("Resumen preliminar de cuentas por pagar actualizado.");
        }
        catch (Exception exception) { SetStatus(UserMessage(exception), true); }
    }

    private void UpdateActionButtons()
    {
        var managePurchases = _context.GrantedPermissions.Contains("Purchase.Manage");
        var managePayments = _context.GrantedPermissions.Contains("SupplierPayment.Manage");
        EditDraftButton.IsEnabled = managePurchases && _selectedPurchase?.Status == "Draft" && _purchaseDetail is not null;
        PostSelectedPurchaseButton.IsEnabled = managePurchases && _selectedPurchase?.Status == "Draft" &&
            (_selectedPurchase.PaymentTerm != "Immediate" || managePayments);
        var canReceive = _selectedPurchase is { Status: "Posted" or "Partial" or "Paid" } &&
            _purchaseDetail?.Lines.Any(line => line.Quantity > line.ReceivedQuantity) == true;
        ReceiveSelectedPurchaseButton.IsEnabled = managePurchases && canReceive;
        PaySelectedPurchaseButton.IsEnabled = managePayments && _selectedPurchase is { Balance: > 0m, Status: "Posted" or "Partial" or "Paid" };
        ReversePurchaseButton.IsEnabled = managePurchases && _selectedPurchase is not null && _selectedPurchase.Status != "Reversed";
        ReverseReceiptButton.IsEnabled = managePurchases && ReceiptsGrid.SelectedItem is PurchaseReceiptClientInfo { Header: { CompletedAtUtc: not null, ReversedAtUtc: null } };
        ReversePaymentButton.IsEnabled = managePayments && PaymentsGrid.SelectedItem is SupplierPaymentClientInfo { ReversedAtUtc: null };
        ViewPriceSuggestionsButton.IsEnabled = _context.GrantedPermissions.Contains("PriceSuggestion.Manage") &&
            _selectedPurchase is not null && ReceiptsGrid.Items.Cast<object>().OfType<PurchaseReceiptClientInfo>()
                .Any(receipt => receipt.Header.CompletedAtUtc.HasValue && !receipt.Header.ReversedAtUtc.HasValue);
        ToggleSupplierButton.IsEnabled = SupplierGrid.SelectedItem is SupplierClientInfo;
    }

    private bool TryBuildDraftRequest(out PurchaseDraftRequest request, out string message)
    {
        request = default!; message = "";
        if (DraftSupplierPicker.SelectedItem is not SupplierClientInfo supplier)
        { message = "Selecciona un proveedor activo."; return false; }
        if (!FinanceDraftValidation.HasDocumentNumber(DraftDocumentNumberBox.Text))
        { message = "Indica el número que aparece en la factura del proveedor."; return false; }
        if (DraftLinesGrid.Items.Count == 0)
        { message = "Agrega al menos un artículo a la factura."; return false; }
        if (!TryDecimal(DraftExchangeRateBox.Text, out var rate) || rate <= 0m || !TryDecimal(DraftFreightBox.Text, out var freight) || freight < 0m)
        { message = "La tasa y el flete deben ser números válidos; la tasa debe ser mayor que cero."; return false; }
        var issueDate = DraftIssueDatePicker.SelectedDate;
        var dueDate = DraftDueDatePicker.SelectedDate;
        if (issueDate is null || dueDate.HasValue && dueDate.Value.Date < issueDate.Value.Date)
        { message = "Indica una fecha de emisión y revisa la fecha de vencimiento."; return false; }
        var documentType = SelectedTag(DraftDocumentTypePicker) ?? "Invoice";
        var paymentTerm = SelectedTag(DraftPaymentTermPicker) ?? "Credit";
        var currency = (DraftCurrencyPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "USD";
        request = new(supplier.Id, documentType, DraftDocumentNumberBox.Text.Trim(), NullIfEmpty(DraftControlNumberBox.Text),
            currency, rate, DateOnly.FromDateTime(issueDate.Value.Date), dueDate.HasValue ? DateOnly.FromDateTime(dueDate.Value.Date) : null,
            freight, paymentTerm, _draftLines.Select(line => new PurchaseLineRequest(line.ProductId, line.Quantity, line.UnitPrice,
                line.Discount, line.Tax, line.TaxRecoverable)).ToArray(), _editingDraftId is null ? Guid.NewGuid().ToString("N") : null, null);
        return true;
    }

    private void UpdateDraftTotal()
    {
        DraftLinesEmptyLabel.Visibility = _draftLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveDraftLineButton.IsEnabled = DraftLinesGrid.SelectedItem is PurchaseDraftLine;
        var subtotal = _draftLines.Sum(line => line.Quantity * line.UnitPrice);
        var discount = _draftLines.Sum(line => line.Discount);
        var tax = _draftLines.Sum(line => line.Tax);
        var freight = TryDecimal(DraftFreightBox.Text, out var parsedFreight) ? parsedFreight : 0m;
        DraftTotalLabel.Text = $"Total de la factura: {DraftCurrency()} {decimal.Round(subtotal - discount + tax + freight, 2, MidpointRounding.AwayFromZero):N2}";
    }

    private void ShowDraftEditor()
    {
        InvoiceWorkspace.Visibility = Visibility.Collapsed;
        DraftEditor.Visibility = Visibility.Visible;
    }

    private void SetDraftLineStatus(string message, bool isError = false)
    {
        DraftLineStatus.Text = message;
        DraftLineStatus.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.SlateGray;
    }

    private void SetStatus(string message, bool isError = false)
    {
        FinanceStatus.Text = message;
        FinanceStatus.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.SlateGray;
    }

    private static bool TryDecimal(string? text, out decimal amount)
    {
        var styles = NumberStyles.Number;
        return decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out amount) ||
            decimal.TryParse(text, styles, CultureInfo.GetCultureInfo("es-VE"), out amount) ||
            decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out amount);
    }

    private static bool TryMarginPercent(string? text, out decimal? percent)
        => FinanceMarginInputPolicy.TryParsePercent(text, out percent);

    private static string FormatMarginPercent(decimal? percent) => percent?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;

    private string DraftCurrency() => (DraftCurrencyPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "USD";
    private static string? SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string UserMessage(Exception exception)
    {
        if (exception is PosApiException apiException)
            return apiException.Code switch
            {
                "payment_exceeds_supplier_balance" => "El abono, convertido a la moneda de la factura, supera el saldo pendiente. Reduce el importe o revisa la moneda.",
                "immediate_payment_must_equal_invoice_total" => "El pago de contado debe equivaler al total completo de la factura usando la tasa vigente. Revisa importe y moneda.",
                "immediate_purchase_requires_payment" => "Esta factura está marcada de contado. Para evitar dejar una deuda, contabilízala junto con el pago.",
                "immediate_purchase_required" => "Esta factura está marcada a crédito y no puede contabilizarse mediante el flujo de contado.",
                "stale_price_suggestion" => "El costo o el precio del artículo cambió desde que se calculó la propuesta. Actualiza las sugerencias y revísala de nuevo.",
                "supplier_payment_idempotency_conflict" => "El servidor detectó que se reutilizó un intento de pago con datos distintos. Revisa el abono antes de volver a registrarlo.",
                "posted_purchase_required" => "Primero contabiliza la factura. Después podrás registrar un abono o una recepción.",
                "exchange_rate_unavailable" => "No hay una tasa vigente para convertir este pago. Actualiza la tasa e inténtalo otra vez.",
                "purchase_warehouse_not_available" => "No hay un almacén disponible para recibir la compra. Revisa la configuración de Almacenes.",
                "purchase_receipt_exceeds_unreceived_quantity" => "La cantidad indicada supera lo que todavía falta por recibir. Actualiza la factura e inténtalo de nuevo.",
                "purchase_product_unavailable" => "Uno de los artículos de la factura ya no está disponible. Revisa sus datos antes de continuar.",
                "purchase_cost_currency_mismatch" => "La moneda del costo no coincide con la moneda configurada para el artículo.",
                "purchase_product_stock_missing" => "No existe un registro de inventario para uno de los artículos. Revisa el catálogo antes de recibir.",
                _ => PosUserMessage.ForException(exception)
            };
        return PosUserMessage.ForException(exception);
    }

    public sealed record SupplierRequest(string Code, string LegalName, string? TaxId, string? Phone, string? Email, string? Address);
    public sealed record SetSupplierActiveRequest(bool Active);
    public sealed record PurchaseLineRequest(Guid ProductId, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Tax, bool TaxRecoverable);
    public sealed record PurchaseDraftRequest(Guid SupplierId, string DocumentType, string DocumentNumber, string? ControlNumber,
        string Currency, decimal ExchangeRate, DateOnly IssueDate, DateOnly? DueDate, decimal Freight, string PaymentTerm,
        IReadOnlyList<PurchaseLineRequest> Lines, string? IdempotencyKey, string? Notes);
    public sealed record PurchaseReceiptLineRequest(Guid InvoiceLineId, decimal Quantity);
    public sealed record PurchaseReceiptRequest(string IdempotencyKey, Guid? WarehouseId, IReadOnlyList<PurchaseReceiptLineRequest> Lines);
    public sealed record SupplierPaymentRequest(decimal Amount, string Currency, string Method, string ReceivingAccount, string? Reference, string IdempotencyKey);
    public sealed record ImmediatePurchasePostingRequest(SupplierPaymentRequest Payment);
    public sealed record PriceSuggestionMarginRequest(decimal? Price1MarginPercent, decimal? Price2MarginPercent, decimal? Price3MarginPercent);
    public sealed record PriceSuggestionMarginSettings(decimal? Price1MarginPercent, decimal? Price2MarginPercent, decimal? Price3MarginPercent);
    public sealed record ReverseRequest(string Reason);
    public sealed record EmptyRequest;
    public sealed record PurchaseOperationResult(Guid Id, string? Status, bool Replayed = false);
    public sealed record SupplierClientInfo(Guid Id, string Code, string LegalName, string? TaxId, string? Phone, string? Email,
        string? Address, bool IsActive, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
    public sealed record PurchaseSummaryClientInfo(Guid Id, Guid SupplierId, string SupplierName, string DocumentType, string DocumentNumber,
        string? ControlNumber, string Currency, DateOnly IssueDate, DateOnly? DueDate, decimal Total, string Status, string PaymentTerm, decimal Balance)
    {
        public string StatusLabel => FinanceDisplayText.PurchaseStatus(Status);
        public string DocumentLabel => FinanceDisplayText.DocumentType(DocumentType) + " " + DocumentNumber +
            (string.IsNullOrWhiteSpace(ControlNumber) ? string.Empty : $" · Control {ControlNumber}");
        public string DueDateLabel => DueDate?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) ?? "Sin fecha";
        public decimal PaidAmount => Math.Max(0m, Total - Balance);
    }
    public sealed record PurchaseHeaderClientInfo(Guid Id, Guid SupplierId, string DocumentType, string DocumentNumber, string? ControlNumber,
        string Currency, decimal ExchangeRate, DateOnly IssueDate, DateOnly? DueDate, decimal Total, decimal Freight, string Status, string PaymentTerm);
    public sealed record PurchaseLineClientInfo(Guid Id, Guid ProductId, string Sku, string Name, decimal Quantity, decimal ReceivedQuantity,
        decimal UnitPrice, decimal Discount, decimal Tax, bool TaxRecoverable, decimal Total);
    public sealed record PurchaseDetailClientInfo(PurchaseHeaderClientInfo Header, IReadOnlyList<PurchaseLineClientInfo> Lines);
    public sealed record PurchaseReceiptHeaderClientInfo(Guid Id, Guid InvoiceId, Guid? WarehouseId, string IdempotencyKey,
        DateTime CreatedAtUtc, DateTime? CompletedAtUtc, DateTime? ReversedAtUtc)
    {
        public string StatusLabel => ReversedAtUtc.HasValue ? "Reversada" : CompletedAtUtc.HasValue ? "Recibida" : "Pendiente";
    }
    public sealed record PurchaseReceiptClientInfo(PurchaseReceiptHeaderClientInfo Header, IReadOnlyList<PurchaseReceiptLineClientInfo> Lines);
    public sealed record PurchaseReceiptLineClientInfo(Guid InvoiceLineId, Guid ProductId, decimal Quantity, decimal UnitCost, string CostCurrency, decimal CostExchangeRate);
    public sealed record SupplierPaymentClientInfo(Guid Id, Guid InvoiceId, string Currency, decimal Amount, decimal InvoiceCurrencyAmount,
        decimal AppliedExchangeRate, decimal? FxDifference, string Method, string ReceivingAccount, string? Reference, DateTime PaidAtUtc, DateTime? ReversedAtUtc);
    public sealed record FinanceSummaryClientInfo(string Label, IReadOnlyList<FinanceSummaryBalanceClientInfo> Balances,
        IReadOnlyList<FinanceOpenPayableClientInfo> Obligations);
    public sealed record FinanceSummaryBalanceClientInfo(string Currency, decimal Amount, long Invoices);
    public sealed record FinanceOpenPayableClientInfo(Guid InvoiceId, string SupplierName, string DocumentType,
        string DocumentNumber, string? ControlNumber, string Currency, DateOnly IssueDate, DateOnly? DueDate,
        decimal InvoiceAmount, decimal PaidAmount, decimal Balance, string Status, string PaymentTerm)
    {
        public string StatusLabel => FinanceDisplayText.PurchaseStatus(Status);
        public string DocumentLabel => FinanceDisplayText.DocumentType(DocumentType) + " " + DocumentNumber +
            (string.IsNullOrWhiteSpace(ControlNumber) ? string.Empty : $" · Control {ControlNumber}");
        public string DueDateLabel => DueDate?.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) ?? "Sin fecha";
    }

    public sealed class PurchaseDraftLine(Guid productId, string sku, string name, decimal quantity, decimal unitPrice,
        decimal discount, decimal tax, bool taxRecoverable)
    {
        public Guid ProductId { get; } = productId;
        public string Sku { get; } = sku;
        public string Name { get; } = name;
        public decimal Quantity { get; } = quantity;
        public decimal UnitPrice { get; } = unitPrice;
        public decimal Discount { get; } = discount;
        public decimal Tax { get; } = tax;
        public bool TaxRecoverable { get; } = taxRecoverable;
        public decimal Total => decimal.Round(Quantity * UnitPrice - Discount + Tax, 4, MidpointRounding.AwayFromZero);
    }
}

internal sealed class ReceiptLineDraft(Guid invoiceLineId, string sku, string name, decimal quantity, decimal remaining)
{
    public Guid InvoiceLineId { get; } = invoiceLineId;
    public string Sku { get; } = sku;
    public string Name { get; } = name;
    public decimal Quantity { get; } = quantity;
    public decimal Remaining { get; } = remaining;
    public decimal QuantityToReceive { get; set; } = remaining;
}

internal sealed class ReceiptQuantityWindow : Window
{
    private readonly IReadOnlyList<ReceiptLineDraft> _lines;

    public ReceiptQuantityWindow(IReadOnlyList<ReceiptLineDraft> lines)
    {
        _lines = lines;
        Title = "Registrar recepción parcial"; Width = 1160; Height = 760; MinWidth = 720; MinHeight = 440;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width - 32);
        Height = Math.Min(Height, workArea.Height - 48);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = System.Windows.Media.Brushes.White;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("es-VE");
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var description = new TextBlock { Text = "Indica cuánto recibiste ahora de cada artículo. Puedes registrar una recepción parcial.",
            Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(2, 0, 2, 10), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(description, 0); layout.Children.Add(description);
        var grid = new DataGrid { ItemsSource = _lines, AutoGenerateColumns = false, CanUserAddRows = false,
            IsReadOnly = false, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 34, Margin = new Thickness(0, 0, 0, 10) };
        grid.Columns.Add(new DataGridTextColumn { Header = "Código", Binding = new Binding(nameof(ReceiptLineDraft.Sku)), IsReadOnly = true, Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Artículo", Binding = new Binding(nameof(ReceiptLineDraft.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Pendiente", Binding = new Binding(nameof(ReceiptLineDraft.Remaining)) { StringFormat = "0.###" }, IsReadOnly = true, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Recibir ahora", Binding = new Binding(nameof(ReceiptLineDraft.QuantityToReceive)) { StringFormat = "0.###", UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 130 });
        Grid.SetRow(grid, 1); layout.Children.Add(grid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(4) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button { Content = "Confirmar recepción", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(4),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 100, 183)), Foreground = System.Windows.Media.Brushes.White };
        save.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (_lines.Any(line => line.QuantityToReceive < 0m || line.QuantityToReceive > line.Remaining || decimal.Round(line.QuantityToReceive, 3) != line.QuantityToReceive))
            { MessageBox.Show(this, "Cada cantidad debe estar entre cero y lo pendiente, con máximo tres decimales.", "Cantidad inválida", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true; Close();
        };
        actions.Children.Add(cancel); actions.Children.Add(save); Grid.SetRow(actions, 2); layout.Children.Add(actions);
        Content = layout;
    }

    public IReadOnlyList<ReceiptLineDraft> Lines => _lines;
}

internal static class TextEntryWindow
{
    public static IReadOnlyDictionary<string, string>? Show(Window owner, string title, IReadOnlyList<(string Label, string Key, string Value)> fields)
    {
        var dialog = new Window { Title = title, Width = 470, SizeToContent = SizeToContent.Height, MinHeight = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White };
        var layout = new Grid { Margin = new Thickness(18) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputs = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        for (var index = 0; index < fields.Count; index++)
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = fields[index].Label, Margin = new Thickness(0, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center };
            var input = new TextBox { Text = fields[index].Value, Margin = new Thickness(0, 3, 0, 3), MinWidth = 240 };
            Grid.SetRow(label, index); Grid.SetColumn(label, 0); Grid.SetRow(input, index); Grid.SetColumn(input, 1);
            layout.Children.Add(label); layout.Children.Add(input); inputs[fields[index].Key] = input;
        }
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var accepted = false;
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4) };
        var save = new Button { Content = "Guardar", IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 100, 183)), Foreground = System.Windows.Media.Brushes.White };
        save.Click += (_, _) => { accepted = true; dialog.Close(); };
        actions.Children.Add(cancel); actions.Children.Add(save);
        Grid.SetRow(actions, fields.Count); Grid.SetColumnSpan(actions, 2); layout.Children.Add(actions); dialog.Content = layout;
        dialog.Loaded += (_, _) => inputs.Values.FirstOrDefault()?.Focus();
        dialog.ShowDialog();
        return accepted ? inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.Ordinal) : null;
    }
}
