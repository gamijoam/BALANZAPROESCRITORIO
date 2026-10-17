using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Domain.Finance;
using Pos.Domain.Sales;
using Pos.Infrastructure.Modules;
using Pos.Infrastructure.Persistence;

namespace Pos.Infrastructure.Sales;

public sealed class SalesService(PosDbContext db)
{
    public async Task<SaleResult> ConfirmAsync(Guid operatorId, Guid terminalId, Guid cashSessionId, string idempotencyKey,
        CheckoutRequest request, CancellationToken cancellationToken = default, Guid? cartWorkspaceId = null)
    {
        if (request is null || request.Lines is null || request.Payments is null || request.Lines.Any(x => x is null) || request.Payments.Any(x => x is null))
            throw new CheckoutRejectedException("invalid_checkout_shape");
        ValidateRequest(idempotencyKey, request);
        var payloadHash = ComputePayloadHash(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({terminalId.ToString()}, 0))", cancellationToken);
        var prior = await db.IdempotencyRecords.SingleOrDefaultAsync(x => x.TerminalId == terminalId && x.Key == idempotencyKey, cancellationToken);
        if (prior is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return prior.PayloadHash == payloadHash ? new SaleResult(prior.SaleId, true, null) : new SaleResult(Guid.Empty, false, "idempotency_conflict");
        }

        CartWorkspaceRecord? workspace = null;
        if (cartWorkspaceId.HasValue)
        {
            workspace = await db.CartWorkspaces.FromSqlInterpolated($"SELECT * FROM cart_workspaces WHERE \"Id\" = {cartWorkspaceId.Value} AND \"TerminalId\" = {terminalId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (workspace is null || workspace.Status != "Active") throw new CheckoutRejectedException("active_workspace_required");
            if (!CartWorkspaceService.MatchesCheckout(workspace, request.CustomerId, request.Lines))
                throw new CheckoutRejectedException("workspace_cart_mismatch");
        }
        else
        {
            workspace = await db.CartWorkspaces.FromSqlInterpolated($"SELECT * FROM cart_workspaces WHERE \"TerminalId\" = {terminalId} AND \"Status\" = 'Active' FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (workspace is not null && CartWorkspaceService.ReadLines(workspace.LinesJson).Count > 0)
            {
                if (!CartWorkspaceService.MatchesCheckout(workspace, request.CustomerId, request.Lines))
                    throw new CheckoutRejectedException("workspace_id_required");
            }
            else workspace = null;
        }

        var cashSession = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM cash_sessions WHERE \"Id\" = {cashSessionId} AND \"ActiveOperatorId\" = {operatorId} AND \"TerminalId\" = {terminalId} AND \"Status\" = 'Open' FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new CheckoutRejectedException("open_cash_session_required");
        var warehouseId = await WarehouseStockBridge.GetWarehouseForTerminalAsync(db, terminalId, cancellationToken);
        var ids = request.Lines.Select(x => x.ProductId).Distinct().ToArray();
        if (request.Lines.Select(x => (x.ProductId, x.PresentationId)).Distinct().Count() != request.Lines.Count)
            throw new CheckoutRejectedException("duplicate_product_line");
        var products = await db.Products.Where(x => ids.Contains(x.Id) && x.Active).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (products.Count != ids.Length) throw new CheckoutRejectedException("product_unavailable");
        var presentationIds = request.Lines.Where(x => x.PresentationId.HasValue).Select(x => x.PresentationId!.Value).Distinct().ToArray();
        var presentations = await db.ProductPresentations.Where(x => presentationIds.Contains(x.Id) && x.Active)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (presentations.Count != presentationIds.Length || presentations.Values.Any(x => !ids.Contains(x.ProductId)))
            throw new CheckoutRejectedException("product_presentation_unavailable");
        var unitIds = products.Values.Select(x => x.BaseUnitId).Concat(presentations.Values.Select(x => x.UnitId)).Distinct().ToArray();
        var units = await db.ProductUnits.Where(x => unitIds.Contains(x.Id) && x.Active).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (units.Count != unitIds.Length) throw new CheckoutRejectedException("product_unit_unavailable");

        var taxTreatments = new Dictionary<Guid, Pos.Domain.Catalog.TaxTreatment>();
        foreach (var product in products.Values)
        {
            Pos.Domain.Catalog.TaxTreatment treatment;
            try { treatment = Pos.Domain.Catalog.TaxTreatment.FromCode(product.TaxTreatment); }
            catch (ArgumentException) { throw new CheckoutRejectedException("product_tax_treatment_invalid"); }
            if (!treatment.IsClassified) throw new CheckoutRejectedException("product_tax_treatment_unclassified");
            taxTreatments.Add(product.Id, treatment);
        }

        var currency = cashSession.Currency;
        if (currency is not ("USD" or "VES")) throw new CheckoutRejectedException("unsupported_cash_currency");
        if (products.Values.Any(x => x.Currency is not ("USD" or "VES"))) throw new CheckoutRejectedException("unsupported_product_currency");
        var creditPayments = request.Payments.Where(x => IsCreditMethod(x.Method)).ToArray();
        var hasCredit = creditPayments.Length > 0;
        if (request.ReceivableDueDate.HasValue && !hasCredit) throw new CheckoutRejectedException("receivable_due_date_without_credit");
        var needsUsdReceivableRate = creditPayments.Any(x => x.Currency == "VES");
        var needsConversion = products.Values.Any(x => x.Currency != currency) || request.Payments.Any(x => x.Currency != currency) || needsUsdReceivableRate;
        var appliedRate = needsConversion
            ? await db.ExchangeRates.Where(x => ((x.FromCurrency == "USD" && x.ToCurrency == "VES") || (x.FromCurrency == "VES" && x.ToCurrency == "USD")) && x.EffectiveAtUtc <= DateTime.UtcNow)
                .OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1).FirstOrDefaultAsync(cancellationToken)
                ?? throw new CheckoutRejectedException("exchange_rate_unavailable")
            : null;
        var referenceRate = appliedRate ?? await db.ExchangeRates.Where(x => ((x.FromCurrency == "USD" && x.ToCurrency == "VES") || (x.FromCurrency == "VES" && x.ToCurrency == "USD")) && x.EffectiveAtUtc <= DateTime.UtcNow)
            .OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1).FirstOrDefaultAsync(cancellationToken);
        if (needsConversion && request.ExchangeRateId != appliedRate!.Id)
            throw new CheckoutRejectedException("exchange_rate_changed");
        var usdToVes = appliedRate is null ? 1m : appliedRate.FromCurrency == "USD" ? appliedRate.Rate : 1m / appliedRate.Rate;
        var lineSnapshots = request.Lines.Select(line =>
        {
            if (line.Quantity <= 0 || decimal.Round(line.Quantity, 3) != line.Quantity) throw new CheckoutRejectedException("invalid_quantity");
            var product = products[line.ProductId];
            var presentation = line.PresentationId.HasValue ? presentations[line.PresentationId.Value] : null;
            if (presentation is not null && presentation.ProductId != product.Id)
                throw new CheckoutRejectedException("product_presentation_unavailable");
            var unit = units[presentation?.UnitId ?? product.BaseUnitId];
            var baseUnit = units[product.BaseUnitId];
            if (!unit.AllowsFractionalQuantities && decimal.Round(line.Quantity, 0) != line.Quantity)
                throw new CheckoutRejectedException("fractional_quantity_not_allowed");
            var conversionFactor = presentation?.ConversionFactor ?? 1m;
            var inventoryQuantity = line.Quantity * conversionFactor;
            if (inventoryQuantity <= 0m || decimal.Round(inventoryQuantity, 3) != inventoryQuantity ||
                !baseUnit.AllowsFractionalQuantities && decimal.Round(inventoryQuantity, 0) != inventoryQuantity)
                throw new CheckoutRejectedException("invalid_inventory_quantity");
            var priceTier = ParsePriceTier(line.PriceTier);
            decimal? baseUnitPrice = priceTier switch
            {
                Pos.Domain.Catalog.PriceTier.Price1 => product.UnitPrice,
                Pos.Domain.Catalog.PriceTier.Price2 => product.Price2,
                Pos.Domain.Catalog.PriceTier.Price3 => product.Price3,
                _ => throw new CheckoutRejectedException("invalid_price_tier")
            };
            var configuredPresentationPrice = presentation is null ? null : priceTier switch
            {
                Pos.Domain.Catalog.PriceTier.Price1 => presentation.Price1,
                Pos.Domain.Catalog.PriceTier.Price2 => presentation.Price2,
                Pos.Domain.Catalog.PriceTier.Price3 => presentation.Price3,
                _ => null
            };
            var originalUnitPrice = configuredPresentationPrice ?? (baseUnitPrice ?? throw new CheckoutRejectedException("price_tier_unconfigured")) * conversionFactor;
            if (originalUnitPrice <= 0) throw new CheckoutRejectedException("invalid_product_price");
            var originalCurrency = product.Currency;
            var conversionRate = originalCurrency == currency ? 1m : originalCurrency == "USD" ? usdToVes : 1m / usdToVes;
            if (!SalePriceOverridePolicy.IsValid(line.UnitPriceOverride, line.PriceOverrideReason))
                throw new CheckoutRejectedException("invalid_price_override");
            var unitPrice = line.UnitPriceOverride ?? decimal.Round(originalUnitPrice * conversionRate, 2, MidpointRounding.AwayFromZero);
            var subtotal = decimal.Round(unitPrice * line.Quantity, 2, MidpointRounding.AwayFromZero);
            var taxTreatment = taxTreatments[product.Id];
            var taxAmount = decimal.Round(subtotal * taxTreatment.Rate, 2, MidpointRounding.AwayFromZero);
            var amount = subtotal + taxAmount;
            return new SaleLineRecord
            {
                Id = Guid.NewGuid(), PresentationId = presentation?.Id,
                PresentationName = presentation?.Label ?? unit.Name, ConversionFactor = conversionFactor, InventoryQuantity = inventoryQuantity,
                ProductId = product.Id, Sku = product.Sku, Description = product.Name,
                Quantity = line.Quantity, UnitPrice = unitPrice, LineTotal = amount,
                OriginalUnitPrice = originalUnitPrice, OriginalCurrency = originalCurrency,
                ExchangeRateId = originalCurrency == currency ? null : appliedRate!.Id,
                AppliedExchangeRate = originalCurrency == currency ? null : conversionRate,
                PriceTier = priceTier.ToString(), TaxTreatment = taxTreatment.Code, TaxRate = taxTreatment.Rate, TaxAmount = taxAmount,
                UnitCost = product.UnitCost.HasValue ? product.UnitCost.Value * conversionFactor : null,
                CostCurrency = product.UnitCost.HasValue ? product.Currency : null
            };
        }).ToArray();
        var total = lineSnapshots.Sum(x => x.LineTotal);
        if (request.Payments.Count == 0 || request.Payments.Any(x => x.Amount <= 0 || decimal.Round(x.Amount, 2) != x.Amount || x.Method is null || x.Method.Length is < 2 or > 32 || x.Currency is not ("USD" or "VES")))
            throw new CheckoutRejectedException("payment_total_mismatch");
        PaymentSettlementResult paymentSettlement;
        try
        {
            paymentSettlement = PaymentSettlementPolicy.Evaluate(total, currency,
                request.Payments.Select(x => new PaymentTender(x.Amount, x.Currency)), usdToVes);
        }
        catch (ArgumentException)
        {
            throw new CheckoutRejectedException("payment_total_mismatch");
        }
        if (!paymentSettlement.IsSettled) throw new CheckoutRejectedException("payment_total_mismatch");
        var creditTenders = creditPayments.Select(x => new PaymentTender(x.Amount, x.Currency)).ToArray();
        var creditAmountUsd = CreditAccountCurrency.SumToUsd(creditTenders, usdToVes);
        var creditCurrencyGroups = creditPayments.GroupBy(x => x.Currency).ToArray();
        var creditOriginalAmount = creditCurrencyGroups.Length == 1
            ? creditCurrencyGroups[0].Sum(x => x.Amount)
            : creditAmountUsd;
        var creditOriginalCurrency = creditCurrencyGroups.Length == 1 ? creditCurrencyGroups[0].Key : "USD";
        CustomerRecord? customer = null;
        if (hasCredit)
        {
            if (request.CustomerId is null || request.CustomerId == Guid.Empty) throw new CheckoutRejectedException("credit_customer_required");
            customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM customers WHERE \"Id\" = {request.CustomerId.Value} AND \"Active\" = TRUE FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
                ?? throw new CheckoutRejectedException("customer_unavailable");
            if (customer.Currency != "USD") throw new CheckoutRejectedException("customer_account_currency_invalid");
            if (creditAmountUsd <= 0m) throw new CheckoutRejectedException("credit_amount_below_minimum");
            var balance = await db.ReceivableEntries.Where(x => x.CustomerId == customer.Id)
                .SumAsync(x => x.EntryType == "Charge" ? x.Amount : -x.Amount, cancellationToken);
            if (balance + creditAmountUsd > customer.CreditLimit) throw new CheckoutRejectedException("credit_limit_exceeded");
        }

        var saleId = Guid.NewGuid();
        var historicalUsdToVesRate = referenceRate is null ? (decimal?)null : referenceRate.FromCurrency == "USD" ? referenceRate.Rate : 1m / referenceRate.Rate;
        var sale = new SaleRecord { Id = saleId, OperatorId = operatorId, TerminalId = terminalId, CashSessionId = cashSessionId, CustomerId = request.CustomerId, Total = total, RoundingAdjustment = paymentSettlement.RoundingAdjustment, AppliedUsdToVesRate = historicalUsdToVesRate, Currency = currency, Status = "Confirmed", CreatedAtUtc = DateTime.UtcNow };
        db.Sales.Add(sale);
        db.IdempotencyRecords.Add(new IdempotencyRecord { Id = Guid.NewGuid(), TerminalId = terminalId, Key = idempotencyKey, PayloadHash = payloadHash, SaleId = saleId, CreatedAtUtc = DateTime.UtcNow });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var concurrent = await db.IdempotencyRecords.SingleOrDefaultAsync(x => x.TerminalId == terminalId && x.Key == idempotencyKey, cancellationToken);
            if (concurrent is null) throw;
            return concurrent.PayloadHash == payloadHash ? new SaleResult(concurrent.SaleId, true, null) : new SaleResult(Guid.Empty, false, "idempotency_conflict");
        }

        foreach (var line in lineSnapshots)
        {
            if (warehouseId.HasValue && !await WarehouseStockBridge.ConsumeAsync(db, warehouseId.Value, line.ProductId, line.InventoryQuantity, cancellationToken))
                throw new CheckoutRejectedException("insufficient_stock");
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stocks SET \"Quantity\" = \"Quantity\" - {line.InventoryQuantity}, \"Version\" = \"Version\" + 1 WHERE \"ProductId\" = {line.ProductId} AND \"Quantity\" >= {line.InventoryQuantity}", cancellationToken);
            if (affected != 1) throw new CheckoutRejectedException("insufficient_stock");
            db.StockMovements.Add(new StockMovementRecord
            {
                Id = Guid.NewGuid(),
                ProductId = line.ProductId,
                QuantityDelta = -line.InventoryQuantity,
                Reason = $"Venta {saleId:N}",
                ActorId = operatorId,
                TerminalId = terminalId,
                OccurredAtUtc = sale.CreatedAtUtc
            });
        }
        if (warehouseId.HasValue)
            await WarehouseStockBridge.RecordSaleAsync(db, saleId, warehouseId.Value, cancellationToken);
        foreach (var line in lineSnapshots) line.SaleId = saleId;
        db.SaleLines.AddRange(lineSnapshots);
        db.Payments.AddRange(request.Payments.Select(x => new PaymentRecord
        {
            Id = Guid.NewGuid(), SaleId = saleId, Method = x.Method, Amount = x.Amount, Currency = x.Currency,
            ExchangeRateId = x.Currency == currency ? null : appliedRate!.Id,
            AppliedExchangeRate = x.Currency == currency ? null : x.Currency == "USD" ? usdToVes : 1m / usdToVes
        }));
        if (hasCredit)
        {
            db.ReceivableEntries.Add(new ReceivableEntryRecord
            {
                Id = Guid.NewGuid(), CustomerId = customer!.Id, SaleId = saleId, EntryType = "Charge",
                Amount = creditAmountUsd, Currency = "USD", OriginalAmount = creditOriginalAmount, OriginalCurrency = creditOriginalCurrency,
                AppliedExchangeRate = creditOriginalCurrency == "VES" ? CreditAccountCurrency.UsdPerVes(usdToVes) : null,
                DueDate = request.ReceivableDueDate ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
                Reason = $"Sale {saleId:N}", ActorId = operatorId, TerminalId = terminalId, OccurredAtUtc = DateTime.UtcNow
            });
        }
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = operatorId, TerminalId = terminalId, Action = "sale.confirmed",
            Payload = JsonSerializer.Serialize(new
            {
                saleId, total, currency, roundingAdjustment = paymentSettlement.RoundingAdjustment,
                receivableDueDate = request.ReceivableDueDate,
                allowedRoundingDifference = paymentSettlement.AllowedDifference,
                exchangeRateId = appliedRate?.Id, usdToVesRate = appliedRate is null ? (decimal?)null : usdToVes,
                lines = lineSnapshots.Select(x => new
                {
                    x.ProductId, x.OriginalUnitPrice, x.OriginalCurrency, x.UnitPrice, x.LineTotal,
                    x.PresentationId, x.PresentationName, x.ConversionFactor, x.InventoryQuantity,
                    x.AppliedExchangeRate, x.ExchangeRateId, x.PriceTier, x.TaxTreatment, x.TaxRate, x.TaxAmount, x.UnitCost, x.CostCurrency,
                    priceOverride = request.Lines.Single(input => input.ProductId == x.ProductId && input.PresentationId == x.PresentationId).UnitPriceOverride,
                    priceOverrideReason = request.Lines.Single(input => input.ProductId == x.ProductId && input.PresentationId == x.PresentationId).PriceOverrideReason
                })
            }), OccurredAtUtc = DateTime.UtcNow
        });
        if (workspace is not null)
        {
            workspace.Status = "Completed";
            workspace.SaleId = saleId;
            workspace.Version++;
            workspace.UpdatedAtUtc = sale.CreatedAtUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SaleResult(saleId, false, null);
    }

    private static void ValidateRequest(string key, CheckoutRequest request)
    {
        if (key.Length is < 8 or > 128 || key.Any(char.IsControl)) throw new CheckoutRejectedException("invalid_idempotency_key");
        if (request.Lines.Count is < 1 or > 100 || request.Payments.Count is < 1 or > 10) throw new CheckoutRejectedException("invalid_checkout_shape");
        if (request.Lines.Any(line => !SalePriceOverridePolicy.IsValid(line.UnitPriceOverride, line.PriceOverrideReason)))
            throw new CheckoutRejectedException("invalid_price_override");
    }

    private static string ComputePayloadHash(CheckoutRequest request)
    {
        var canonical = new
        {
            request.CustomerId,
            request.ExchangeRateId,
            request.ReceivableDueDate,
            lines = request.Lines.OrderBy(x => x.ProductId).ThenBy(x => x.PresentationId).Select(x => new { x.ProductId, x.PresentationId, x.Quantity, x.UnitPriceOverride, x.PriceOverrideReason, x.PriceTier }),
            payments = request.Payments.OrderBy(x => x.Currency, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal).ThenBy(x => x.Amount).Select(x => new { x.Method, x.Amount, x.Currency })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }

    private static bool IsCreditMethod(string method) => string.Equals(method, "credit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method, "fiado", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "credito", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(method, "crédito", StringComparison.OrdinalIgnoreCase);

    private static Pos.Domain.Catalog.PriceTier ParsePriceTier(string? value) => value switch
    {
        "Price1" => Pos.Domain.Catalog.PriceTier.Price1,
        "Price2" => Pos.Domain.Catalog.PriceTier.Price2,
        "Price3" => Pos.Domain.Catalog.PriceTier.Price3,
        _ => throw new CheckoutRejectedException("invalid_price_tier")
    };
}

public sealed record CheckoutRequest(IReadOnlyList<CheckoutLine> Lines, IReadOnlyList<CheckoutPayment> Payments, Guid? CustomerId = null, Guid? ExchangeRateId = null, DateOnly? ReceivableDueDate = null);
public sealed record CheckoutLine(Guid ProductId, decimal Quantity, decimal? UnitPriceOverride = null, string? PriceOverrideReason = null, string PriceTier = "Price1", Guid? PresentationId = null);
public sealed record CheckoutPayment(string Method, decimal Amount, string Currency);
public sealed record SaleResult(Guid SaleId, bool Replayed, string? Error);
public sealed class CheckoutRejectedException(string code) : Exception(code) { public string Code { get; } = code; }
