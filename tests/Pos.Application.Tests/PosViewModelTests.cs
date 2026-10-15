using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosViewModelTests
{
    [Fact]
    public void Login_form_can_read_the_service_address_configured_in_the_application()
    {
        var api = new FakePosApiClient { BaseAddress = new Uri("http://localhost:5157") };
        var viewModel = new PosViewModel(api);
        Assert.Equal(api.BaseAddress, viewModel.ApiBaseAddress);
    }

    [Fact]
    public async Task Login_reports_post_authentication_failure_separately_and_allows_retry()
    {
        var api = new FakePosApiClient { FailNextExchangeRateFetch = true };
        var viewModel = new PosViewModel(api);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.LoginAsync("demo", "4826", Guid.NewGuid()));

        Assert.True(viewModel.IsAuthenticated);
        Assert.Contains("Acceso aceptado", viewModel.StatusMessage);

        await viewModel.LoginAsync("demo", "4826", Guid.NewGuid());

        Assert.Contains("Sesión iniciada", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Failed_pin_then_correct_pin_authenticates_before_loading_startup_data()
    {
        var api = new FakePosApiClient { FailNextLogin = true };
        var viewModel = new PosViewModel(api);

        await Assert.ThrowsAsync<PosApiException>(() => viewModel.AuthenticateAsync("demo", "4526", Guid.NewGuid()));
        Assert.False(viewModel.IsAuthenticated);
        Assert.Contains("Usuario o PIN incorrecto", viewModel.StatusMessage);

        await viewModel.AuthenticateAsync("demo", "4826", Guid.NewGuid());
        Assert.True(viewModel.IsAuthenticated);
        Assert.Equal(0, api.ExchangeRateFetchCount);

        await viewModel.LoadInitialPosStateAsync();
        Assert.Contains("Sesión iniciada", viewModel.StatusMessage);
        Assert.Equal(1, api.ExchangeRateFetchCount);
    }

    [Fact]
    public async Task Pausing_a_sale_saves_it_on_the_server_before_clearing_the_cart()
    {
        var api = new FakePosApiClient { FailFirstCheckout = false };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        var product = ClassifiedProduct(Guid.NewGuid(), "HOLD-1", "Artículo pausado", 10m, "VES", stockQuantity: 20m) with { Price2 = 12m };
        viewModel.AddProduct(product);
        Assert.True(viewModel.EditCartLine(viewModel.CartLines.Single(), 3m, "Price2", null, null));

        await viewModel.PauseCurrentSaleAsync("Cliente volverá luego");

        Assert.Empty(viewModel.CartLines);
        Assert.Equal(1, api.PauseCount);
        Assert.Equal(0, api.CheckoutCount);
        var savedLine = Assert.Single(api.LastWorkspaceSave!.Lines);
        Assert.Equal(product.Id, savedLine.ProductId);
        Assert.Equal(3m, savedLine.Quantity);
        Assert.Equal("Price2", savedLine.PriceTier);
        Assert.Equal("Cliente volverá luego", api.LastPauseRequest!.Note);
    }

    [Fact]
    public async Task Failed_pause_keeps_the_cart_visible_and_does_not_claim_it_was_paused()
    {
        var api = new FakePosApiClient { FailFirstCheckout = false, FailPause = true };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        var product = ClassifiedProduct(Guid.NewGuid(), "HOLD-FAIL", "No perder", 10m, "VES", stockQuantity: 20m);
        viewModel.AddProduct(product);

        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.PauseCurrentSaleAsync(null));

        Assert.Equal(product.Id, Assert.Single(viewModel.CartLines).Product.Id);
        Assert.Equal(0, api.PauseCount);
        Assert.Contains("sin pausar", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retrying_a_workspace_save_reuses_its_idempotency_key_if_the_response_was_lost()
    {
        var api = new FakePosApiClient { FailAfterFirstWorkspaceSave = true };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "SAVE-RETRY", "Artículo", 5m, "VES", stockQuantity: 5m));

        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.FlushWorkspaceSyncAsync());
        var firstMutationId = Assert.Single(api.WorkspaceSaveMutationIds);
        Assert.True(viewModel.IsWorkspaceSyncPending);

        await viewModel.FlushWorkspaceSyncAsync();

        Assert.Equal(2, api.WorkspaceSaveMutationIds.Count);
        Assert.Equal(firstMutationId, api.WorkspaceSaveMutationIds[1]);
        Assert.False(viewModel.IsWorkspaceSyncPending);
        Assert.Equal(2, api.ActiveWorkspace!.Version);
    }

    [Fact]
    public async Task Retrying_a_pause_reuses_its_idempotency_key_if_the_server_already_paused_it()
    {
        var api = new FakePosApiClient { FailAfterFirstPause = true };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "PAUSE-RETRY", "Artículo", 5m, "VES", stockQuantity: 5m));

        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.PauseCurrentSaleAsync(null));
        var firstMutationId = Assert.Single(api.PauseMutationIds);
        Assert.Single(viewModel.CartLines);

        await viewModel.PauseCurrentSaleAsync(null);

        Assert.Empty(viewModel.CartLines);
        Assert.Equal(1, api.PauseCount);
        Assert.Equal(2, api.PauseMutationIds.Count);
        Assert.Equal(firstMutationId, api.PauseMutationIds[1]);
        Assert.Equal("Active", api.ActiveWorkspace!.Status);
    }

    [Fact]
    public async Task A_new_sale_after_pausing_uses_the_new_active_workspace()
    {
        var api = new FakePosApiClient();
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "PAUSE-FIRST", "Primero", 5m, "VES", stockQuantity: 5m));

        await viewModel.PauseCurrentSaleAsync("Luego vuelve");
        var newWorkspaceId = api.ActiveWorkspace!.Id;
        var secondProduct = ClassifiedProduct(Guid.NewGuid(), "SALE-SECOND", "Segundo", 8m, "VES", stockQuantity: 5m);
        viewModel.AddProduct(secondProduct);
        await viewModel.FlushWorkspaceSyncAsync();

        Assert.Equal(newWorkspaceId, api.LastWorkspaceSave!.WorkspaceId);
        Assert.Equal(secondProduct.Id, Assert.Single(api.LastWorkspaceSave.Lines).ProductId);
        Assert.Equal("Active", api.ActiveWorkspace.Status);
    }

    [Fact]
    public async Task Startup_restores_the_server_active_cart_with_its_quantity_and_price_tier()
    {
        var productId = Guid.NewGuid();
        var api = new FakePosApiClient
        {
            FailFirstCheckout = false,
            ActiveWorkspace = Workspace(productId, "RESTORE-1", "Arroz", 4m, "Price3", 7.25m)
        };
        var viewModel = new PosViewModel(api);

        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal(productId, line.Product.Id);
        Assert.Equal(4m, line.Quantity);
        Assert.Equal("Price3", line.PriceTier);
        Assert.Equal(1, api.ActiveWorkspaceReadCount);
    }

    [Fact]
    public void Paused_sale_review_explains_current_total_price_tax_and_stock_changes()
    {
        var productId = Guid.NewGuid();
        var paused = Workspace(productId, "REVIEW-1", "Arroz", 2m, "Price1", 10m) with
        {
            Status = "Paused",
            Currency = "VES",
            TotalAtPause = 23.20m,
            Lines = [new Pos.Shared.Contracts.CartWorkspaceLineSnapshot(productId, "REVIEW-1", "Arroz", 2m,
                "Price1", null, null, 10m, "VES", 0.16m, 23.20m, true, 1m, 12m, null, null,
                "VES", "Taxable8")]
        };
        var viewModel = new PosViewModel(new FakePosApiClient());

        var review = viewModel.BuildPausedSaleReview(paused);

        Assert.Contains("Total al pausar: VES", review);
        Assert.Contains("23", review);
        Assert.Contains("25", review);
        Assert.Contains("hay 1 disponibles", review);
        Assert.Contains("precio, impuesto o moneda cambió", review);
    }

    [Fact]
    public void Paused_sale_review_uses_the_current_server_currency_and_exchange_rate()
    {
        var productId = Guid.NewGuid();
        var paused = Workspace(productId, "RATE-REVIEW", "Artículo", 1m, "Price1", 0.20m) with
        {
            Status = "Paused",
            Currency = "VES",
            TotalAtPause = 0.20m,
            CurrentSettlementCurrency = "USD",
            CurrentExchangeRate = new Pos.Shared.Contracts.CartWorkspaceExchangeRateSnapshot(
                Guid.NewGuid(), "VES", "USD", 0.02m, DateTime.UtcNow, "test", Guid.NewGuid()),
            Lines = [new Pos.Shared.Contracts.CartWorkspaceLineSnapshot(productId, "RATE-REVIEW", "Artículo", 1m,
                "Price1", null, null, 0.20m, "USD", 0m, 0.20m, true, 10m, 12m, null, null,
                "VES", "Taxable16")]
        };

        var review = new PosViewModel(new FakePosApiClient()).BuildPausedSaleReview(paused);

        Assert.Contains($"Total con los valores actuales: USD {0.28m:N2}", review);
        Assert.Contains("precio, impuesto o moneda cambió", review);
    }

    [Fact]
    public async Task Resuming_a_paused_sale_sends_the_confirmed_pricing_fingerprint()
    {
        var api = new FakePosApiClient { FailFirstCheckout = false };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        var preview = Workspace(Guid.NewGuid(), "RESUME-1", "Arroz", 1m, "Price1", 7m) with
        {
            Status = "Paused",
            PricingFingerprint = new string('a', 64)
        };
        api.ResumeWorkspace = preview;

        await viewModel.ResumePausedSaleAsync(preview, acceptCurrentValues: true);

        Assert.Equal(preview.PricingFingerprint, api.LastWorkspaceTransition?.ExpectedPricingFingerprint);
    }

    [Fact]
    public async Task Resuming_uses_the_current_server_exchange_rate_for_the_active_cart()
    {
        var productId = Guid.NewGuid();
        var rateId = Guid.NewGuid();
        var preview = Workspace(productId, "RATE-RESUME", "Artículo", 1m, "Price1", 0.20m) with
        {
            Status = "Paused",
            PricingFingerprint = new string('b', 64),
            CurrentSettlementCurrency = "USD",
            CurrentExchangeRate = new Pos.Shared.Contracts.CartWorkspaceExchangeRateSnapshot(
                rateId, "VES", "USD", 0.02m, DateTime.UtcNow, "test", Guid.NewGuid()),
            Lines = [new Pos.Shared.Contracts.CartWorkspaceLineSnapshot(productId, "RATE-RESUME", "Artículo", 1m,
                "Price1", null, null, 0.20m, "USD", 0m, 0.20m, true, 10m, 12m, null, null,
                "VES", "Taxable16")]
        };
        var api = new FakePosApiClient
        {
            FailFirstCheckout = false,
            ResumeWorkspace = preview,
            CurrentCashSessionInfo = new CashSessionInfo(Guid.NewGuid(), "USD", 0m, DateTime.UtcNow,
                OpenedByOperatorName: "cashier", ActiveOperatorName: "cashier", IsActiveForCurrentOperator: true)
        };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());

        await viewModel.ResumePausedSaleAsync(preview, acceptCurrentValues: true);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal(0.24m, line.UnitPrice);
        Assert.Equal(0.28m, line.LineTotal);
        Assert.Equal(rateId, line.ExchangeRateId);
    }

    private static Pos.Shared.Contracts.CartWorkspaceSnapshot Workspace(Guid productId, string sku, string name,
        decimal quantity, string tier, decimal savedPrice)
    {
        var now = DateTime.UtcNow;
        return new Pos.Shared.Contracts.CartWorkspaceSnapshot(Guid.NewGuid(), 2, "Active", null, null,
            Guid.NewGuid(), "cashier", now, now, savedPrice * quantity,
            [new Pos.Shared.Contracts.CartWorkspaceLineSnapshot(productId, sku, name, quantity, tier, null, null,
                savedPrice, "VES", 0.16m, savedPrice * quantity, true, 20m, 8m, 9m, 10m, "VES", "Taxable16")]);
    }

    [Fact]
    public void Adding_same_product_twice_increments_one_cart_line_and_decimal_total()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        var product = ClassifiedProduct(Guid.NewGuid(), "SKU-1", "Arroz", 12.35m, "VES");

        viewModel.AddProduct(product);
        viewModel.AddProduct(product);

        var line = Assert.Single(viewModel.CartLines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(28.65m, line.LineTotal);
        Assert.Equal(2m, viewModel.ItemCount);
        Assert.Equal(28.65m, viewModel.Total);
    }

    [Fact]
    public void Editing_cart_quantity_directly_refreshes_sale_total_and_item_count()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "INLINE-QTY", "Producto", 10m, "VES"));
        var line = Assert.Single(viewModel.CartLines);

        line.Quantity = 3m;

        Assert.Equal(34.80m, viewModel.Total);
        Assert.Equal(3m, viewModel.ItemCount);
    }

    [Fact]
    public async Task Mouse_edit_of_cart_line_updates_quantity_total_and_authorized_sale_payload()
    {
        var api = new FakePosApiClient { FailFirstCheckout = false };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "SKU-EDIT", "Artículo", 10m, "VES"));
        var line = Assert.Single(viewModel.CartLines);

        Assert.True(viewModel.EditCartLine(line, 2m, 8m, "Precio acordado"));
        Assert.Equal(18.56m, viewModel.Total);
        await viewModel.CompleteSaleAsync([new SalePaymentInput("cash", 18.56m, "VES")]);

        var submittedLine = Assert.Single(api.LastLines!);
        Assert.Equal(2m, submittedLine.Quantity);
        Assert.Equal(8m, submittedLine.UnitPriceOverride);
        Assert.Equal("Precio acordado", submittedLine.PriceOverrideReason);
    }

    [Fact]
    public void Adding_a_product_reported_with_zero_stock_is_rejected()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        var product = ClassifiedProduct(Guid.NewGuid(), "SKU-EMPTY", "Agotado", 12m, "VES", stockQuantity: 0m);

        var added = viewModel.AddProduct(product);

        Assert.False(added);
        Assert.Empty(viewModel.CartLines);
        Assert.Contains("agotado", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Product_without_tax_classification_is_blocked_before_it_enters_the_cart()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        var product = new ProductInfo(Guid.NewGuid(), "PENDING-TAX", "Artículo pendiente", 12m, "VES");

        var added = viewModel.AddProduct(product);

        Assert.False(added);
        Assert.Empty(viewModel.CartLines);
        Assert.Contains("pendiente", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Selected_catalog_tier_and_tax_are_reflected_in_total_and_sale_payload()
    {
        var api = new FakePosApiClient { FailFirstCheckout = false };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        var product = ClassifiedProduct(Guid.NewGuid(), "MULTI-PRICE", "Producto con niveles", 10m, "VES",
            price2: 8m, price3: 5m, taxTreatment: "Taxable8");

        Assert.True(viewModel.AddProduct(product));
        var line = Assert.Single(viewModel.CartLines);
        Assert.True(viewModel.EditCartLine(line, 2m, "Price2", null, null));

        Assert.Equal(8m, line.CatalogUnitPrice);
        Assert.Equal(1.28m, line.TaxAmount);
        Assert.Equal(17.28m, viewModel.Total);
        await viewModel.CompleteSaleAsync([new SalePaymentInput("cash", 17.28m, "VES")]);

        var submitted = Assert.Single(api.LastLines!);
        Assert.Equal("Price2", submitted.PriceTier);
    }

    [Fact]
    public void Removing_last_unit_removes_line_and_resets_total()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "SKU-2", "Leche", 4.50m, "VES"));
        var line = Assert.Single(viewModel.CartLines);

        viewModel.RemoveProduct(line);

        Assert.Empty(viewModel.CartLines);
        Assert.Equal(0m, viewModel.Total);
        Assert.Equal(0, viewModel.ItemCount);
    }

    [Fact]
    public void Removing_the_previewed_product_switches_preview_to_last_remaining_cart_product()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        var first = ClassifiedProduct(Guid.NewGuid(), "SKU-1", "Arroz", 12.35m, "VES");
        var second = ClassifiedProduct(Guid.NewGuid(), "SKU-2", "Leche", 4.50m, "VES");
        viewModel.AddProduct(first);
        viewModel.AddProduct(second);

        viewModel.RemoveProduct(viewModel.CartLines.Single(x => x.Product.Id == second.Id));

        Assert.Equal(first, viewModel.CurrentProduct);
        viewModel.RemoveProduct(viewModel.CartLines.Single());
        Assert.Null(viewModel.CurrentProduct);
    }

    [Fact]
    public async Task Mixed_currency_products_are_added_and_shown_in_the_open_cash_currency()
    {
        var api = new FakePosApiClient { CashCurrency = "USD", CurrentUsdToVesRate = 40m };
        api.CurrentCashSessionInfo = new CashSessionInfo(Guid.NewGuid(), "USD", 0m, DateTime.UtcNow,
            OpenedByOperatorName: "cashier", ActiveOperatorName: "cashier", IsActiveForCurrentOperator: true);
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());

        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "USD-1", "Cable USD", 2m, "USD"));
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "VES-1", "Cable VES", 80m, "VES"));

        Assert.Equal(2, viewModel.CartLines.Count);
        Assert.Equal("USD", viewModel.Currency);
        Assert.Equal(4.64m, viewModel.Total);
        Assert.Equal(2m, viewModel.CartLines[1].UnitPrice);
        Assert.Equal("USD 2,00", viewModel.ReferenceCurrentPriceLabel);
        Assert.Equal("REF USD 4,64", viewModel.ReferenceTotalLabel);
    }

    [Fact]
    public async Task Retry_after_connection_loss_reuses_the_same_checkout_snapshot()
    {
        var api = new FakePosApiClient();
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "SKU-3", "Café", 10m, "VES"));
        var originalPayments = new[] { new SalePaymentInput("cash", 11.60m, "VES") };

        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.CompleteSaleAsync(originalPayments));
        var lockedPayments = Assert.Single(viewModel.PendingPayments!);
        Assert.False(viewModel.CanEditCart);
        Assert.Equal(11.60m, lockedPayments.Amount);

        var receipt = await viewModel.CompleteSaleAsync([new SalePaymentInput("transfer", 10m, "VES")]);

        Assert.Equal(api.FirstIdempotencyKey, api.LastIdempotencyKey);
        Assert.Equal("cash", Assert.Single(api.LastPayments!).Method);
        Assert.Equal(11.60m, receipt.Total);
        Assert.Empty(viewModel.CartLines);
        Assert.False(viewModel.HasPendingCheckout);
    }

    [Fact]
    public async Task Ves_credit_checkout_keeps_customer_and_rate_snapshot_for_usd_account_conversion()
    {
        var api = new FakePosApiClient { CashCurrency = "VES", CurrentUsdToVesRate = 875.65m, FailFirstCheckout = false };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "VES-1", "Artículo VES", 875.65m, "VES", taxTreatment: "Taxable0"));
        var customer = new CustomerInfo(Guid.NewGuid(), "Gabriel", null, null, "USD", 100m);
        viewModel.SelectCustomer(customer);

        await viewModel.CompleteSaleAsync([new SalePaymentInput("Crédito", 875.65m, "VES")]);

        Assert.Equal(customer.Id, api.LastCustomerId);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), api.LastExchangeRateId);
    }

    [Fact]
    public async Task Ves_sale_accepts_usd_credit_tender_and_keeps_customer_and_rate_snapshot()
    {
        var api = new FakePosApiClient { CashCurrency = "VES", CurrentUsdToVesRate = 875.65m, FailFirstCheckout = false };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "VES-USD-CREDIT", "Artículo VES", 875.65m, "VES", taxTreatment: "Taxable0"));
        var customer = new CustomerInfo(Guid.NewGuid(), "Gabriel", null, null, "USD", 100m);
        viewModel.SelectCustomer(customer);

        await viewModel.CompleteSaleAsync([new SalePaymentInput("Crédito", 1m, "USD")]);

        Assert.Equal(customer.Id, api.LastCustomerId);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), api.LastExchangeRateId);
        var payment = Assert.Single(api.LastPayments!);
        Assert.Equal("Crédito", payment.Method);
        Assert.Equal(1m, payment.Amount);
        Assert.Equal("USD", payment.Currency);
    }

    [Fact]
    public async Task Logout_requires_closed_cash_and_empty_cart_then_clears_operator_state()
    {
        var api = new FakePosApiClient();
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.LogoutAsync());
        Assert.Equal(0, api.LogoutCount);

        await viewModel.CloseCashSessionAsync(0m, 0m);
        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "SKU-1", "Arroz", 12m, "VES"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.LogoutAsync());
        Assert.Equal(0, api.LogoutCount);

        viewModel.RemoveProduct(viewModel.CartLines.Single());
        await viewModel.LogoutAsync();

        Assert.Equal(1, api.LogoutCount);
        Assert.False(viewModel.IsAuthenticated);
        Assert.Empty(viewModel.OperatorName);
        Assert.Empty(viewModel.CartLines);
        Assert.Null(viewModel.CashSession);
    }

    [Fact]
    public async Task Switching_operator_preserves_open_cash_on_terminal_and_requires_empty_cart()
    {
        var api = new FakePosApiClient();
        var viewModel = new PosViewModel(api);
        var terminalId = Guid.NewGuid();
        await viewModel.LoginAsync("cashier", "1234", terminalId);
        var openCash = viewModel.CashSession;

        await viewModel.SwitchOperatorAsync();

        Assert.Equal(1, api.LogoutCount);
        Assert.Null(api.AccessToken);
        Assert.Null(viewModel.CashSession);
        await viewModel.LoginAsync("admin", "1234", terminalId);
        Assert.Equal(openCash?.Id, viewModel.CashSession?.Id);
        Assert.Equal("cashier", viewModel.CashSession?.OpenedByOperatorName);
        Assert.Equal("Caja abierta · VES · turno de cashier", viewModel.CashStatus);
        Assert.Equal(1, api.CashSessionReleaseCount);
        Assert.Equal(1, api.CashSessionClaimCount);

        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "SKU-1", "Arroz", 12m, "VES"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.SwitchOperatorAsync());
        Assert.Equal(1, api.LogoutCount);
        Assert.True(viewModel.IsAuthenticated);
        Assert.NotNull(viewModel.CashSession);
    }

    [Fact]
    public async Task Session_action_label_changes_when_cash_is_open()
    {
        var viewModel = new PosViewModel(new FakePosApiClient());
        await viewModel.LoginAsync("cashier", "1234", Guid.NewGuid());

        Assert.Equal("Cambiar operador", viewModel.OperatorSessionActionLabel);
        Assert.Contains("manteniendo abierto el turno", viewModel.OperatorSessionActionToolTip);

        viewModel.AddProduct(ClassifiedProduct(Guid.NewGuid(), "SKU-1", "Arroz", 12m, "VES"));
        Assert.False(viewModel.CanUseSessionAction);
        Assert.Contains("retira los artículos", viewModel.OperatorSessionActionToolTip);
        viewModel.RemoveProduct(viewModel.CartLines.Single());

        await viewModel.CloseCashSessionAsync(0m, 0m);

        Assert.Equal("Cerrar sesión", viewModel.OperatorSessionActionLabel);
        Assert.Contains("vuelve al inicio", viewModel.OperatorSessionActionToolTip);
    }

    [Fact]
    public async Task Login_explains_when_another_operator_is_using_the_terminal_cash_session()
    {
        var api = new FakePosApiClient
        {
            CurrentCashSessionInfo = new CashSessionInfo(Guid.NewGuid(), "VES", 0m, DateTime.UtcNow,
                OpenedByOperatorName: "cashier", ActiveOperatorName: "cashier", IsActiveForCurrentOperator: false)
        };
        var viewModel = new PosViewModel(api);

        await viewModel.LoginAsync("admin", "1234", Guid.NewGuid());

        Assert.Equal("Caja abierta · en uso por cashier", viewModel.CashStatus);
        Assert.Contains("reservado para cashier", viewModel.StatusMessage);
        Assert.False(viewModel.CanCharge);
        Assert.Equal(0, api.CashSessionClaimCount);
    }

    [Fact]
    public async Task Switching_from_non_active_operator_does_not_release_another_operators_cash_session()
    {
        var api = new FakePosApiClient
        {
            CurrentCashSessionInfo = new CashSessionInfo(Guid.NewGuid(), "VES", 0m, DateTime.UtcNow,
                OpenedByOperatorName: "cashier", ActiveOperatorName: "cashier", IsActiveForCurrentOperator: false)
        };
        var viewModel = new PosViewModel(api);
        await viewModel.LoginAsync("admin", "1234", Guid.NewGuid());

        await viewModel.SwitchOperatorAsync();

        Assert.Equal(1, api.LogoutCount);
        Assert.Equal(0, api.CashSessionReleaseCount);
        Assert.Equal("cashier", api.CurrentCashSessionInfo?.ActiveOperatorName);
    }

    private static ProductInfo ClassifiedProduct(Guid id, string sku, string name, decimal unitPrice, string currency,
        bool hasImage = false, decimal? stockQuantity = null, decimal? price2 = null, decimal? price3 = null, string taxTreatment = "Taxable16")
        => new(id, sku, name, unitPrice, currency, hasImage, stockQuantity, price2, price3, taxTreatment);

    private sealed class FakePosApiClient : IPosApiClient
    {
        private bool _firstCheckout = true;
        private readonly Dictionary<string, Pos.Shared.Contracts.CartWorkspaceSnapshot> _savedWorkspaceMutations = [];
        private readonly Dictionary<string, Pos.Shared.Contracts.CartWorkspaceSnapshot> _pauseReplays = [];
        public Uri BaseAddress { get; set; } = new("http://localhost:5155");
        public string? AccessToken { get; private set; }
        public string? FirstIdempotencyKey { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public Guid? LastCustomerId { get; private set; }
        public Guid? LastExchangeRateId { get; private set; }
        public IReadOnlyList<SaleLineInput>? LastLines { get; private set; }
        public Pos.Shared.Contracts.CartWorkspaceSnapshot? ActiveWorkspace { get; set; }
        public Pos.Shared.Contracts.CartWorkspaceSaveRequest? LastWorkspaceSave { get; private set; }
        public Pos.Shared.Contracts.CartWorkspacePauseRequest? LastPauseRequest { get; private set; }
        public Pos.Shared.Contracts.CartWorkspaceTransitionRequest? LastWorkspaceTransition { get; private set; }
        public Pos.Shared.Contracts.CartWorkspaceSnapshot? ResumeWorkspace { get; set; }
        public int ActiveWorkspaceReadCount { get; private set; }
        public int PauseCount { get; private set; }
        public int CheckoutCount { get; private set; }
        public bool FailPause { get; set; }
        public bool FailAfterFirstWorkspaceSave { get; set; }
        public bool FailAfterFirstPause { get; set; }
        public List<string> WorkspaceSaveMutationIds { get; } = [];
        public List<string> PauseMutationIds { get; } = [];
        public IReadOnlyList<SalePaymentInput>? LastPayments { get; private set; }
        public string CashCurrency { get; set; } = "VES";
        public decimal? CurrentUsdToVesRate { get; set; }
        public bool FailFirstCheckout { get; set; } = true;
        public bool FailNextExchangeRateFetch { get; set; }
        public bool FailNextLogin { get; set; }
        public int ExchangeRateFetchCount { get; private set; }
        public int LogoutCount { get; private set; }
        public int CashSessionClaimCount { get; private set; }
        public int CashSessionReleaseCount { get; private set; }
        public string LastUsername { get; private set; } = string.Empty;
        public CashSessionInfo? CurrentCashSessionInfo { get; set; } = new(Guid.NewGuid(), "VES", 0m, DateTime.UtcNow,
            OpenedByOperatorName: "cashier", ActiveOperatorName: "cashier", IsActiveForCurrentOperator: true);
        public Task<LoginResult> LoginAsync(string username, string pin, Guid terminalId, CancellationToken cancellationToken = default)
        {
            if (FailNextLogin)
            {
                FailNextLogin = false;
                throw new PosApiException(System.Net.HttpStatusCode.Unauthorized, "unauthorized");
            }
            LastUsername = username;
            AccessToken = "test-token";
            return Task.FromResult(new LoginResult(Guid.NewGuid(), terminalId, DateTime.UtcNow.AddHours(1), AccessToken));
        }
        public Task<CashSessionInfo?> GetCurrentCashSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentCashSessionInfo);
        public Task<CashSessionInfo?> ClaimCurrentCashSessionAsync(CancellationToken cancellationToken = default)
        {
            CashSessionClaimCount++;
            if (CurrentCashSessionInfo is not null)
                CurrentCashSessionInfo = CurrentCashSessionInfo with { ActiveOperatorName = LastUsername, IsActiveForCurrentOperator = true };
            return Task.FromResult(CurrentCashSessionInfo);
        }
        public Task ReleaseCurrentCashSessionAsync(CancellationToken cancellationToken = default)
        {
            CashSessionReleaseCount++;
            if (CurrentCashSessionInfo is not null)
                CurrentCashSessionInfo = CurrentCashSessionInfo with { ActiveOperatorName = null, IsActiveForCurrentOperator = false };
            return Task.CompletedTask;
        }
        public Task<CashSessionInfo> OpenCashSessionAsync(decimal openingBalance, string currency, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CashCloseInfo> CloseCashSessionAsync(Guid cashSessionId, decimal countedClosingBalance, CancellationToken cancellationToken = default)
        {
            CurrentCashSessionInfo = null;
            return Task.FromResult(new CashCloseInfo(0, countedClosingBalance, 0));
        }
        public Task RecordCashMovementAsync(Guid cashSessionId, CashMovementInput movement, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<InventoryItemInfo>> GetInventoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InventoryItemInfo>>([]);
        public Task<InventoryItemInfo> CreateProductAsync(string sku, string name, decimal unitPrice, string currency, decimal initialStock, CancellationToken cancellationToken = default) => Task.FromResult(new InventoryItemInfo(Guid.NewGuid(), sku, name, unitPrice, currency, initialStock));
        public Task<decimal> AdjustStockAsync(Guid productId, decimal quantityDelta, string reason, CancellationToken cancellationToken = default) => Task.FromResult(quantityDelta);
        public Task<StockMovementPageInfo> GetStockMovementsAsync(Guid productId, int offset = 0, int limit = 100, CancellationToken cancellationToken = default) => Task.FromResult(new StockMovementPageInfo([], false));
        public Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid> CheckoutAsync(Guid cashSessionId, IReadOnlyList<SaleLineInput> lines, IReadOnlyList<SalePaymentInput> payments, Guid? customerId, string idempotencyKey, Guid? exchangeRateId = null, CancellationToken cancellationToken = default)
        {
            CheckoutCount++;
            FirstIdempotencyKey ??= idempotencyKey;
            LastIdempotencyKey = idempotencyKey;
            LastPayments = payments.ToArray();
            LastLines = lines.ToArray();
            LastCustomerId = customerId;
            LastExchangeRateId = exchangeRateId;
            if (FailFirstCheckout && _firstCheckout)
            {
                _firstCheckout = false;
                throw new HttpRequestException("Simulated connection loss");
            }
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<Pos.Shared.Contracts.CartWorkspaceSnapshot> GetActiveCartWorkspaceAsync(CancellationToken cancellationToken = default)
        {
            ActiveWorkspaceReadCount++;
            ActiveWorkspace ??= EmptyWorkspace();
            return Task.FromResult(ActiveWorkspace);
        }
        public Task<Pos.Shared.Contracts.CartWorkspaceSnapshot> SaveActiveCartWorkspaceAsync(Pos.Shared.Contracts.CartWorkspaceSaveRequest request, CancellationToken cancellationToken = default)
        {
            LastWorkspaceSave = request;
            WorkspaceSaveMutationIds.Add(request.MutationId);
            if (_savedWorkspaceMutations.TryGetValue(request.MutationId, out var replay))
                return Task.FromResult(replay);
            ActiveWorkspace ??= EmptyWorkspace();
            if (request.WorkspaceId != ActiveWorkspace.Id || request.ExpectedVersion != ActiveWorkspace.Version)
                throw new PosApiException(System.Net.HttpStatusCode.Conflict, "workspace_version_conflict");
            ActiveWorkspace = ActiveWorkspace with { Version = ActiveWorkspace.Version + 1, CustomerId = request.CustomerId };
            _savedWorkspaceMutations[request.MutationId] = ActiveWorkspace;
            if (FailAfterFirstWorkspaceSave)
            {
                FailAfterFirstWorkspaceSave = false;
                throw new HttpRequestException("simulated lost save response");
            }
            return Task.FromResult(ActiveWorkspace);
        }
        public Task<Pos.Shared.Contracts.CartWorkspaceSnapshot> PauseActiveCartWorkspaceAsync(Pos.Shared.Contracts.CartWorkspacePauseRequest request, CancellationToken cancellationToken = default)
        {
            LastPauseRequest = request;
            PauseMutationIds.Add(request.MutationId);
            if (_pauseReplays.TryGetValue(request.MutationId, out var replay))
                return Task.FromResult(replay);
            if (FailPause) throw new HttpRequestException("simulated pause failure");
            ActiveWorkspace ??= EmptyWorkspace();
            if (request.ExpectedVersion != ActiveWorkspace.Version)
                throw new PosApiException(System.Net.HttpStatusCode.Conflict, "workspace_version_conflict");
            var paused = ActiveWorkspace with { Status = "Paused", Note = request.Note, Version = ActiveWorkspace.Version + 1 };
            ActiveWorkspace = EmptyWorkspace() with { Id = Guid.NewGuid(), Version = 1 };
            _pauseReplays[request.MutationId] = ActiveWorkspace;
            PauseCount++;
            if (FailAfterFirstPause)
            {
                FailAfterFirstPause = false;
                throw new HttpRequestException("simulated lost pause response");
            }
            return Task.FromResult(ActiveWorkspace);
        }
        public Task<IReadOnlyList<Pos.Shared.Contracts.CartWorkspaceSnapshot>> GetPausedCartWorkspacesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Pos.Shared.Contracts.CartWorkspaceSnapshot>>([]);
        public Task<Pos.Shared.Contracts.CartWorkspaceSnapshot> GetPausedCartWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<Pos.Shared.Contracts.CartWorkspaceSnapshot> ResumeCartWorkspaceAsync(Guid workspaceId, Pos.Shared.Contracts.CartWorkspaceTransitionRequest request, CancellationToken cancellationToken = default)
        {
            LastWorkspaceTransition = request;
            ActiveWorkspace = (ResumeWorkspace ?? EmptyWorkspace()) with { Status = "Active" };
            return Task.FromResult(ActiveWorkspace);
        }
        public Task DiscardCartWorkspaceAsync(Guid workspaceId, Pos.Shared.Contracts.CartWorkspaceTransitionRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        private Pos.Shared.Contracts.CartWorkspaceSnapshot EmptyWorkspace()
        {
            var now = DateTime.UtcNow;
            var currency = CurrentCashSessionInfo?.Currency ?? CashCurrency;
            var currentRate = CurrentUsdToVesRate is { } rate
                ? new Pos.Shared.Contracts.CartWorkspaceExchangeRateSnapshot(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"), "USD", "VES", rate,
                    now.AddMinutes(-1), "test", Guid.Empty)
                : null;
            return new Pos.Shared.Contracts.CartWorkspaceSnapshot(Guid.NewGuid(), 1, "Active", null, null,
                Guid.Empty, "cashier", now, now, 0m, [], CurrentSettlementCurrency: currency, CurrentExchangeRate: currentRate);
        }
        public Task<IReadOnlyList<CustomerInfo>> GetCustomersAsync(string? query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerInfo>>([]);
        public Task<CustomerInfo> CreateCustomerAsync(string name, string? documentNumber, string? phone, string currency, decimal creditLimit, CancellationToken cancellationToken = default) => Task.FromResult(new CustomerInfo(Guid.NewGuid(), name, documentNumber, phone, currency, creditLimit));
        public Task<CustomerInfo> UpdateCustomerAsync(Guid customerId, string name, string? documentNumber, string? phone, decimal creditLimit, CancellationToken cancellationToken = default) => Task.FromResult(new CustomerInfo(customerId, name, documentNumber, phone, "USD", creditLimit));
        public Task DeactivateCustomerAsync(Guid customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ChangePriceAsync(Guid productId, decimal newPrice, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExchangeRateInfo>> GetExchangeRatesAsync(CancellationToken cancellationToken = default)
        {
            ExchangeRateFetchCount++;
            if (FailNextExchangeRateFetch)
            {
                FailNextExchangeRateFetch = false;
                throw new InvalidOperationException("Simulated startup data failure");
            }
            return Task.FromResult<IReadOnlyList<ExchangeRateInfo>>(
                CurrentUsdToVesRate is { } rate ? [new ExchangeRateInfo(Guid.Parse("22222222-2222-2222-2222-222222222222"), "USD", "VES", rate, DateTime.UtcNow.AddMinutes(-1), "test", Guid.NewGuid())] : []);
        }
        public Task SetExchangeRateAsync(string fromCurrency, string toCurrency, decimal rate, DateTime effectiveAtUtc, string source, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SalesReportInfo> GetSalesReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) => Task.FromResult(new SalesReportInfo(fromUtc, toUtc, []));
        public Task<IReadOnlyList<OperatorInfo>> GetOperatorsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OperatorInfo>>([]);
        public Task<IReadOnlyList<OperatorPermissionSetting>> GetOperatorPermissionsAsync(Guid operatorId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OperatorPermissionSetting>>([]);
        public Task SetOperatorPermissionsAsync(Guid operatorId, IReadOnlyList<OperatorPermissionSetting> permissions, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateOperatorAsync(string username, string pin, string role, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetOperatorRoleAsync(Guid operatorId, string role, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetOperatorActiveAsync(Guid operatorId, bool active, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditInfo>> GetAuditAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditInfo>>([]);
        public Task<IReadOnlyList<InventoryReportRow>> GetInventoryReportAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InventoryReportRow>>([]);
        public Task<ReceivableInfo> GetReceivablesAsync(Guid customerId, CancellationToken cancellationToken = default) => Task.FromResult(new ReceivableInfo(customerId, "", "VES", 0m, 0m, []));
        public Task RecordReceivablePaymentAsync(Guid customerId, decimal amount, string currency, string? reference, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) { LogoutCount++; AccessToken = null; return Task.CompletedTask; }
    }

}
