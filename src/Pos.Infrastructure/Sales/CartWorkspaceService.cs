using System.Net;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Sales;
using Pos.Infrastructure.Persistence;
using Pos.Shared.Contracts;

namespace Pos.Infrastructure.Sales;

public sealed class CartWorkspaceService(PosDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CartWorkspaceSnapshot> GetActiveAsync(Guid terminalId, Guid operatorId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockTerminalAsync(terminalId, cancellationToken);
        var workspace = await FindWorkspaceAsync(terminalId, "Active", cancellationToken);
        if (workspace is null)
        {
            workspace = NewActive(terminalId, operatorId);
            db.CartWorkspaces.Add(workspace);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await ToSnapshotAsync(workspace, cancellationToken);
    }

    public async Task<CartWorkspaceSnapshot> SaveActiveAsync(Guid terminalId, Guid operatorId,
        CartWorkspaceSaveRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSave(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockTerminalAsync(terminalId, cancellationToken);
        var workspace = await FindWorkspaceAsync(terminalId, "Active", cancellationToken);
        var payloadHash = Hash(JsonSerializer.Serialize(request with { MutationId = string.Empty }, JsonOptions));
        if (workspace is not null && TryReplay(workspace, request.MutationId, payloadHash))
        {
            await transaction.CommitAsync(cancellationToken);
            return await ToSnapshotAsync(workspace, cancellationToken);
        }
        if (workspace is not null && (request.WorkspaceId != workspace.Id || request.ExpectedVersion != workspace.Version))
            throw Conflict("workspace_version_conflict");
        if (workspace is null && (request.WorkspaceId.HasValue || request.ExpectedVersion != 0))
            throw Conflict("active_workspace_changed");

        workspace ??= NewActive(terminalId, operatorId);
        var currency = await GetSettlementCurrencyAsync(terminalId, workspace.Currency, cancellationToken);
        var snapshots = await BuildLineSnapshotsAsync(request.Lines, currency, workspace.LinesJson, cancellationToken);
        if (request.CustomerId.HasValue && !await db.Customers.AnyAsync(x => x.Id == request.CustomerId.Value, cancellationToken))
            throw NotFound("customer_not_found");
        var now = DateTime.UtcNow;
        if (workspace.Id == Guid.Empty) workspace.Id = Guid.NewGuid();
        if (db.Entry(workspace).State == EntityState.Detached) db.CartWorkspaces.Add(workspace);
        else if (db.Entry(workspace).State == EntityState.Added) { }
        else workspace.Version++;
        workspace.CustomerId = request.CustomerId;
        workspace.Currency = currency;
        workspace.LinesJson = JsonSerializer.Serialize(snapshots, JsonOptions);
        workspace.TotalAtPause = snapshots.Sum(x => x.SavedLineTotal);
        workspace.UpdatedAtUtc = now;
        workspace.LastMutationId = request.MutationId;
        workspace.LastMutationHash = payloadHash;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ToSnapshotAsync(workspace, cancellationToken);
    }

    public async Task<CartWorkspaceSnapshot> PauseActiveAsync(Guid terminalId, Guid operatorId,
        CartWorkspacePauseRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMutation(request.MutationId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockTerminalAsync(terminalId, cancellationToken);
        var active = await FindWorkspaceAsync(terminalId, "Active", cancellationToken);
        var payloadHash = Hash(JsonSerializer.Serialize(new { request.ExpectedVersion, Note = NormalizeNote(request.Note) }, JsonOptions));
        if (active is not null && TryReplay(active, request.MutationId, payloadHash))
        {
            await transaction.CommitAsync(cancellationToken);
            return await ToSnapshotAsync(active, cancellationToken);
        }
        if (active is null || active.Version != request.ExpectedVersion) throw Conflict("workspace_version_conflict");
        var lines = ReadLines(active.LinesJson);
        if (lines.Count == 0) throw Conflict("workspace_empty");
        var now = DateTime.UtcNow;
        active.Status = "Paused";
        active.Note = NormalizeNote(request.Note);
        active.OperatorId = operatorId;
        active.Version++;
        active.UpdatedAtUtc = now;
        active.LastMutationId = request.MutationId;
        active.LastMutationHash = payloadHash;
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = operatorId, TerminalId = terminalId, Action = "cart.paused",
            Payload = JsonSerializer.Serialize(new { workspaceId = active.Id, lineCount = lines.Count, active.TotalAtPause, active.Currency, note = active.Note }, JsonOptions),
            OccurredAtUtc = now
        });
        var nextActive = NewActive(terminalId, operatorId);
        nextActive.LastMutationId = request.MutationId;
        nextActive.LastMutationHash = payloadHash;
        db.CartWorkspaces.Add(nextActive);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ToSnapshotAsync(nextActive, cancellationToken);
    }

    public async Task<IReadOnlyList<CartWorkspaceSnapshot>> GetPausedAsync(Guid terminalId, CancellationToken cancellationToken = default)
    {
        var workspaces = await db.CartWorkspaces.AsNoTracking()
            .Where(x => x.TerminalId == terminalId && x.Status == "Paused")
            .OrderByDescending(x => x.UpdatedAtUtc).ToListAsync(cancellationToken);
        var result = new List<CartWorkspaceSnapshot>(workspaces.Count);
        foreach (var workspace in workspaces) result.Add(await ToSnapshotAsync(workspace, cancellationToken));
        return result;
    }

    public async Task<CartWorkspaceSnapshot> GetPausedAsync(Guid terminalId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await db.CartWorkspaces.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == workspaceId && x.TerminalId == terminalId && x.Status == "Paused", cancellationToken)
            ?? throw NotFound("paused_workspace_not_found");
        return await ToSnapshotAsync(workspace, cancellationToken);
    }

    public async Task<CartWorkspaceSnapshot> ResumeAsync(Guid terminalId, Guid operatorId, Guid workspaceId,
        CartWorkspaceTransitionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMutation(request.MutationId);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await LockTerminalAsync(terminalId, cancellationToken);
        var target = await db.CartWorkspaces.FromSqlInterpolated($"SELECT * FROM cart_workspaces WHERE \"Id\" = {workspaceId} AND \"TerminalId\" = {terminalId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw NotFound("paused_workspace_not_found");
        var payloadHash = Hash(JsonSerializer.Serialize(new { request.ExpectedVersion, workspaceId, request.ExpectedPricingFingerprint }, JsonOptions));
        if (target.Status == "Active" && TryReplay(target, request.MutationId, payloadHash))
        {
            await transaction.CommitAsync(cancellationToken);
            return await ToSnapshotAsync(target, cancellationToken);
        }
        if (target.Status != "Paused" || target.Version != request.ExpectedVersion) throw Conflict("workspace_version_conflict");
        var active = await FindWorkspaceAsync(terminalId, "Active", cancellationToken);
        if (active is not null && ReadLines(active.LinesJson).Count > 0) throw Conflict("active_workspace_not_empty");
        if (string.IsNullOrWhiteSpace(request.ExpectedPricingFingerprint) || request.ExpectedPricingFingerprint.Length != 64)
            throw new CartWorkspaceException("pricing_confirmation_required", HttpStatusCode.BadRequest);
        var currentSnapshot = await ToSnapshotAsync(target, cancellationToken);
        if (!string.Equals(request.ExpectedPricingFingerprint, currentSnapshot.PricingFingerprint, StringComparison.Ordinal))
            throw Conflict("cart_values_changed");
        var now = DateTime.UtcNow;
        if (active is not null)
        {
            active.Status = "Completed";
            active.UpdatedAtUtc = now;
            active.Version++;
        }
        target.Status = "Active";
        target.OperatorId = operatorId;
        target.Version++;
        target.UpdatedAtUtc = now;
        target.LastMutationId = request.MutationId;
        target.LastMutationHash = payloadHash;
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = operatorId, TerminalId = terminalId, Action = "cart.resumed",
            Payload = JsonSerializer.Serialize(new { workspaceId = target.Id, target.TotalAtPause, target.Currency }, JsonOptions),
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        var resumedSnapshot = await ToSnapshotAsync(target, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return resumedSnapshot;
    }

    public async Task DiscardAsync(Guid terminalId, Guid operatorId, bool mayDiscardAny, Guid workspaceId,
        CartWorkspaceTransitionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMutation(request.MutationId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockTerminalAsync(terminalId, cancellationToken);
        var workspace = await db.CartWorkspaces.FromSqlInterpolated($"SELECT * FROM cart_workspaces WHERE \"Id\" = {workspaceId} AND \"TerminalId\" = {terminalId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw NotFound("paused_workspace_not_found");
        var payloadHash = Hash(JsonSerializer.Serialize(new { request.ExpectedVersion, workspaceId }, JsonOptions));
        if (workspace.Status == "Discarded" && TryReplay(workspace, request.MutationId, payloadHash))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (workspace.Status != "Paused" || workspace.Version != request.ExpectedVersion) throw Conflict("workspace_version_conflict");
        if (!mayDiscardAny && workspace.OperatorId != operatorId) throw new CartWorkspaceException("discard_not_allowed", HttpStatusCode.Forbidden);
        var now = DateTime.UtcNow;
        workspace.Status = "Discarded";
        workspace.Version++;
        workspace.UpdatedAtUtc = now;
        workspace.LastMutationId = request.MutationId;
        workspace.LastMutationHash = payloadHash;
        db.AuditEvents.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), ActorId = operatorId, TerminalId = terminalId, Action = "cart.discarded",
            Payload = JsonSerializer.Serialize(new { workspaceId = workspace.Id, lineCount = ReadLines(workspace.LinesJson).Count, workspace.TotalAtPause }, JsonOptions),
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static bool MatchesCheckout(CartWorkspaceRecord workspace, Guid? customerId,
        IReadOnlyList<CheckoutLine> requestedLines)
    {
        var savedLines = ReadLines(workspace.LinesJson);
        return workspace.CustomerId == customerId && savedLines.Count == requestedLines.Count &&
            savedLines.OrderBy(x => x.ProductId).ThenBy(x => x.PresentationId).Zip(requestedLines.OrderBy(x => x.ProductId).ThenBy(x => x.PresentationId))
                .All(pair => pair.First.ProductId == pair.Second.ProductId && pair.First.Quantity == pair.Second.Quantity &&
                    pair.First.PresentationId == pair.Second.PresentationId &&
                    pair.First.PriceTier == pair.Second.PriceTier && pair.First.UnitPriceOverride == pair.Second.UnitPriceOverride &&
                    string.Equals(pair.First.PriceOverrideReason, pair.Second.PriceOverrideReason, StringComparison.Ordinal));
    }

    private async Task<List<CartWorkspaceLineSnapshot>> BuildLineSnapshotsAsync(IReadOnlyList<CartWorkspaceLineInput> inputs,
        string settlementCurrency, string currentJson, CancellationToken cancellationToken)
    {
        var prior = ReadLines(currentJson).ToDictionary(x => (x.ProductId, x.PresentationId));
        var ids = inputs.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var stocks = await db.Stocks.AsNoTracking().Where(x => ids.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, cancellationToken);
        var presentationIds = inputs.Where(x => x.PresentationId.HasValue).Select(x => x.PresentationId!.Value).Distinct().ToArray();
        var presentations = await db.ProductPresentations.AsNoTracking().Where(x => presentationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var unitIds = products.Values.Select(x => x.BaseUnitId).Concat(presentations.Values.Select(x => x.UnitId)).Distinct().ToArray();
        var units = await db.ProductUnits.AsNoTracking().Where(x => unitIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var rateRecord = await GetUsdToVesRateRecordAsync(cancellationToken);
        var rate = NormalizeUsdToVesRate(rateRecord);
        var result = new List<CartWorkspaceLineSnapshot>(inputs.Count);
        foreach (var line in inputs)
        {
            prior.TryGetValue((line.ProductId, line.PresentationId), out var old);
            if (!products.TryGetValue(line.ProductId, out var product))
            {
                if (old is null) throw new CartWorkspaceException("product_unavailable", HttpStatusCode.UnprocessableEntity);
                result.Add(old with { Quantity = line.Quantity, PriceTier = line.PriceTier, UnitPriceOverride = line.UnitPriceOverride,
                    PriceOverrideReason = line.PriceOverrideReason, ProductActive = false, CurrentStock = 0m });
                continue;
            }

            var presentation = line.PresentationId.HasValue ? presentations.GetValueOrDefault(line.PresentationId.Value) : null;
            if (line.PresentationId.HasValue && (presentation is null || presentation.ProductId != product.Id || !presentation.Active))
                throw new CartWorkspaceException("product_presentation_unavailable", HttpStatusCode.UnprocessableEntity);
            var unit = units.GetValueOrDefault(presentation?.UnitId ?? product.BaseUnitId)
                ?? throw new CartWorkspaceException("product_unit_unavailable", HttpStatusCode.UnprocessableEntity);
            var baseUnit = units.GetValueOrDefault(product.BaseUnitId)
                ?? throw new CartWorkspaceException("product_unit_unavailable", HttpStatusCode.UnprocessableEntity);
            if (!unit.AllowsFractionalQuantities && decimal.Round(line.Quantity, 0) != line.Quantity)
                throw new CartWorkspaceException("fractional_quantity_not_allowed", HttpStatusCode.UnprocessableEntity);
            var conversionFactor = presentation?.ConversionFactor ?? 1m;
            var inventoryQuantity = line.Quantity * conversionFactor;
            if (inventoryQuantity <= 0m || decimal.Round(inventoryQuantity, 3) != inventoryQuantity ||
                !baseUnit.AllowsFractionalQuantities && decimal.Round(inventoryQuantity, 0) != inventoryQuantity)
                throw new CartWorkspaceException("invalid_inventory_quantity", HttpStatusCode.UnprocessableEntity);
            var baseTierPrice = line.PriceTier switch
            {
                "Price1" => product.UnitPrice,
                "Price2" => product.Price2,
                "Price3" => product.Price3,
                _ => null
            };
            var presentationPrice = presentation is null ? null : line.PriceTier switch
            {
                "Price1" => presentation.Price1,
                "Price2" => presentation.Price2,
                "Price3" => presentation.Price3,
                _ => null
            };
            var listedPrice = presentationPrice ?? (baseTierPrice.HasValue ? baseTierPrice.Value * conversionFactor : null);
            if (listedPrice is null && old is null) throw new CartWorkspaceException("price_tier_unconfigured", HttpStatusCode.UnprocessableEntity);
            var originalPrice = listedPrice ?? old!.SavedUnitPrice;
            decimal converted;
            if (line.UnitPriceOverride.HasValue) converted = line.UnitPriceOverride.Value;
            else if (product.Currency == settlementCurrency) converted = decimal.Round(originalPrice, 2, MidpointRounding.AwayFromZero);
            else if (rate is > 0m)
                converted = decimal.Round(originalPrice * (product.Currency == "USD" ? rate.Value : 1m / rate.Value), 2, MidpointRounding.AwayFromZero);
            else if (old is not null) converted = old.SavedUnitPrice;
            else throw new CartWorkspaceException("exchange_rate_unavailable", HttpStatusCode.UnprocessableEntity);

            var taxRate = product.TaxTreatment switch { "Taxable16" => 0.16m, "Taxable8" => 0.08m, _ => 0m };
            var subtotal = decimal.Round(converted * line.Quantity, 2, MidpointRounding.AwayFromZero);
            var lineTotal = subtotal + decimal.Round(subtotal * taxRate, 2, MidpointRounding.AwayFromZero);
            var stock = stocks.GetValueOrDefault(product.Id)?.Quantity ?? 0m;
            var salePrice1 = presentation?.Price1 ?? product.UnitPrice * conversionFactor;
            var salePrice2 = presentation?.Price2 ?? (product.Price2.HasValue ? product.Price2.Value * conversionFactor : null);
            var salePrice3 = presentation?.Price3 ?? (product.Price3.HasValue ? product.Price3.Value * conversionFactor : null);
            var label = presentation?.Label ?? unit.Name;
            result.Add(new CartWorkspaceLineSnapshot(product.Id, old?.Sku ?? product.Sku, old?.Name ?? product.Name,
                line.Quantity, line.PriceTier, line.UnitPriceOverride, line.PriceOverrideReason, converted, settlementCurrency,
                taxRate, lineTotal, product.Active, stock, product.UnitPrice, product.Price2, product.Price3, product.Currency,
                product.TaxTreatment, product.Sku, product.Name, line.PresentationId, label, unit.Name, unit.Abbreviation,
                unit.Id, conversionFactor, inventoryQuantity, salePrice1, salePrice2, salePrice3,
                unit.AllowsFractionalQuantities, presentation?.Active ?? true,
                label, unit.Name, unit.Abbreviation, unit.Id, conversionFactor, unit.AllowsFractionalQuantities,
                product.BaseUnitId, baseUnit.Name, baseUnit.Abbreviation, baseUnit.AllowsFractionalQuantities));
        }
        return result;
    }

    private async Task<CartWorkspaceSnapshot> ToSnapshotAsync(CartWorkspaceRecord workspace, CancellationToken cancellationToken)
    {
        var saved = ReadLines(workspace.LinesJson);
        var ids = saved.Select(x => x.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var stocks = await db.Stocks.AsNoTracking().Where(x => ids.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, cancellationToken);
        var presentationIds = saved.Where(x => x.PresentationId.HasValue).Select(x => x.PresentationId!.Value).Distinct().ToArray();
        var presentations = await db.ProductPresentations.AsNoTracking().Where(x => presentationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var unitIds = products.Values.Select(x => x.BaseUnitId).Concat(presentations.Values.Select(x => x.UnitId)).Distinct().ToArray();
        var units = await db.ProductUnits.AsNoTracking().Where(x => unitIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var lines = saved.Select(line =>
        {
            if (!products.TryGetValue(line.ProductId, out var product))
                return line with { ProductActive = false, CurrentStock = 0m, CurrentPrice2 = null, CurrentPrice3 = null,
                    CurrentTaxTreatment = "Unclassified", CurrentSku = null, CurrentName = null };
            var presentation = line.PresentationId.HasValue ? presentations.GetValueOrDefault(line.PresentationId.Value) : null;
            var presentationActive = line.PresentationId is null || presentation?.Active == true;
            var unitId = presentation?.UnitId ?? product.BaseUnitId;
            var unit = units.GetValueOrDefault(unitId);
            var baseUnit = units.GetValueOrDefault(product.BaseUnitId);
            var factor = presentation?.ConversionFactor ?? (line.PresentationId is null ? 1m : line.ConversionFactor);
            var salePrice1 = presentation?.Price1 ?? product.UnitPrice * factor;
            var salePrice2 = presentation?.Price2 ?? (product.Price2.HasValue ? product.Price2.Value * factor : null);
            var salePrice3 = presentation?.Price3 ?? (product.Price3.HasValue ? product.Price3.Value * factor : null);
            var label = presentation?.Label ?? (line.PresentationId is null ? unit?.Name ?? line.PresentationName : line.PresentationName);
            return line with
            {
                ProductActive = product.Active,
                CurrentStock = stocks.GetValueOrDefault(product.Id)?.Quantity ?? 0m,
                CurrentPrice1 = product.UnitPrice,
                CurrentPrice2 = product.Price2,
                CurrentPrice3 = product.Price3,
                CurrentCurrency = product.Currency,
                CurrentTaxTreatment = product.TaxTreatment,
                CurrentSku = product.Sku,
                CurrentName = product.Name,
                CurrentSaleUnitPrice1 = salePrice1,
                CurrentSaleUnitPrice2 = salePrice2,
                CurrentSaleUnitPrice3 = salePrice3,
                PresentationActive = presentationActive,
                CurrentPresentationName = label,
                CurrentPresentationUnitName = unit?.Name ?? line.PresentationUnitName,
                CurrentPresentationUnitAbbreviation = unit?.Abbreviation ?? line.PresentationUnitAbbreviation,
                CurrentPresentationUnitId = unit?.Id ?? line.PresentationUnitId,
                CurrentConversionFactor = factor,
                CurrentSaleUnitAllowsFractionalQuantities = unit?.AllowsFractionalQuantities ?? line.SaleUnitAllowsFractionalQuantities,
                BaseUnitId = baseUnit?.Id ?? line.BaseUnitId,
                BaseUnitName = baseUnit?.Name ?? line.BaseUnitName,
                BaseUnitAbbreviation = baseUnit?.Abbreviation ?? line.BaseUnitAbbreviation,
                BaseUnitAllowsFractionalQuantities = baseUnit?.AllowsFractionalQuantities ?? line.BaseUnitAllowsFractionalQuantities
            };
        }).ToArray();
        var operatorName = await db.OperatorUsers.AsNoTracking().Where(x => x.Id == workspace.OperatorId)
            .Select(x => x.Username).SingleOrDefaultAsync(cancellationToken) ?? "Operador";
        var customer = workspace.CustomerId.HasValue
            ? await db.Customers.AsNoTracking().Where(x => x.Id == workspace.CustomerId.Value)
                .Select(x => new CartWorkspaceCustomerSnapshot(x.Id, x.Name, x.DocumentNumber, x.Phone, x.Currency, x.CreditLimit))
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var settlementCurrency = await GetSettlementCurrencyAsync(workspace.TerminalId, workspace.Currency, cancellationToken);
        var currentRateRecord = await GetUsdToVesRateRecordAsync(cancellationToken);
        var currentRate = NormalizeUsdToVesRate(currentRateRecord);
        var pricingFingerprint = Hash(JsonSerializer.Serialize(new
        {
            settlementCurrency,
            exchangeRateId = currentRateRecord?.Id,
            usdToVesRate = currentRate,
            lines = lines.OrderBy(x => x.ProductId).ThenBy(x => x.PresentationId).Select(x => new
            {
                x.ProductId, x.ProductActive, x.CurrentStock, x.CurrentPrice1, x.CurrentPrice2, x.CurrentPrice3,
                x.CurrentCurrency, x.CurrentTaxTreatment, x.PresentationActive, x.CurrentPresentationName,
                x.CurrentConversionFactor, x.CurrentSaleUnitPrice1, x.CurrentSaleUnitPrice2, x.CurrentSaleUnitPrice3,
                x.BaseUnitId, x.BaseUnitName, x.BaseUnitAbbreviation
            })
        }, JsonOptions));
        return new CartWorkspaceSnapshot(workspace.Id, workspace.Version, workspace.Status, workspace.CustomerId, workspace.Note,
            workspace.OperatorId, operatorName, workspace.CreatedAtUtc, workspace.UpdatedAtUtc, workspace.TotalAtPause, lines, customer,
            workspace.Currency, pricingFingerprint, settlementCurrency,
            currentRateRecord is null ? null : new CartWorkspaceExchangeRateSnapshot(currentRateRecord.Id,
                currentRateRecord.FromCurrency, currentRateRecord.ToCurrency, currentRateRecord.Rate,
                currentRateRecord.EffectiveAtUtc, currentRateRecord.Source, currentRateRecord.ActorId));
    }

    private async Task<string> GetSettlementCurrencyAsync(Guid terminalId, string fallback, CancellationToken cancellationToken)
        => await db.CashSessions.AsNoTracking().Where(x => x.TerminalId == terminalId && x.Status == "Open")
            .Select(x => x.Currency).SingleOrDefaultAsync(cancellationToken) ?? (fallback is "USD" or "VES" ? fallback : "VES");

    private async Task<ExchangeRateRecord?> GetUsdToVesRateRecordAsync(CancellationToken cancellationToken)
        => await db.ExchangeRates.AsNoTracking()
            .Where(x => ((x.FromCurrency == "USD" && x.ToCurrency == "VES") || (x.FromCurrency == "VES" && x.ToCurrency == "USD")) && x.EffectiveAtUtc <= DateTime.UtcNow)
            .OrderByDescending(x => x.EffectiveAtUtc).ThenBy(x => x.FromCurrency == "USD" ? 0 : 1).FirstOrDefaultAsync(cancellationToken);

    private static decimal? NormalizeUsdToVesRate(ExchangeRateRecord? rate)
        => rate is null ? null : rate.FromCurrency == "USD" ? rate.Rate : rate.Rate == 0m ? null : 1m / rate.Rate;

    private async Task<CartWorkspaceRecord?> FindWorkspaceAsync(Guid terminalId, string status, CancellationToken cancellationToken)
        => await db.CartWorkspaces.FromSqlInterpolated($"SELECT * FROM cart_workspaces WHERE \"TerminalId\" = {terminalId} AND \"Status\" = {status} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private async Task LockTerminalAsync(Guid terminalId, CancellationToken cancellationToken)
        => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({terminalId.ToString()}, 0))", cancellationToken);

    private static CartWorkspaceRecord NewActive(Guid terminalId, Guid operatorId)
    {
        var now = DateTime.UtcNow;
        return new CartWorkspaceRecord { Id = Guid.NewGuid(), TerminalId = terminalId, OperatorId = operatorId,
            Status = "Active", Version = 1, Currency = "VES", LinesJson = "[]", CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    private static void ValidateSave(CartWorkspaceSaveRequest request)
    {
        if (request is null || request.Lines is null || request.Lines.Count > 100 || request.Lines.Any(x => x is null))
            throw new CartWorkspaceException("invalid_workspace_shape", HttpStatusCode.BadRequest);
        ValidateMutation(request.MutationId);
        if (request.ExpectedVersion < 0 || request.Lines.Select(x => (x.ProductId, x.PresentationId)).Distinct().Count() != request.Lines.Count)
            throw new CartWorkspaceException("invalid_workspace_lines", HttpStatusCode.BadRequest);
        foreach (var line in request.Lines)
        {
            if (line.ProductId == Guid.Empty || line.Quantity <= 0m || decimal.Round(line.Quantity, 3) != line.Quantity ||
                line.PriceTier is not ("Price1" or "Price2" or "Price3") ||
                !SalePriceOverridePolicy.IsValid(line.UnitPriceOverride, line.PriceOverrideReason))
                throw new CartWorkspaceException("invalid_workspace_line", HttpStatusCode.UnprocessableEntity);
        }
    }

    private static void ValidateMutation(string mutationId)
    {
        if (mutationId is null || mutationId.Length is < 8 or > 64 || mutationId.Any(char.IsControl))
            throw new CartWorkspaceException("invalid_mutation_id", HttpStatusCode.BadRequest);
    }

    private static string? NormalizeNote(string? note)
    {
        var value = note?.Trim();
        if (value?.Length > 250) throw new CartWorkspaceException("note_too_long", HttpStatusCode.BadRequest);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool TryReplay(CartWorkspaceRecord workspace, string mutationId, string payloadHash)
    {
        if (!string.Equals(workspace.LastMutationId, mutationId, StringComparison.Ordinal)) return false;
        if (!string.Equals(workspace.LastMutationHash, payloadHash, StringComparison.Ordinal)) throw Conflict("mutation_id_reused");
        return true;
    }

    public static List<CartWorkspaceLineSnapshot> ReadLines(string json)
        => JsonSerializer.Deserialize<List<CartWorkspaceLineSnapshot>>(json, JsonOptions) ?? [];

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static CartWorkspaceException Conflict(string code) => new(code, HttpStatusCode.Conflict);
    private static CartWorkspaceException NotFound(string code) => new(code, HttpStatusCode.NotFound);
}

public sealed class CartWorkspaceException(string code, HttpStatusCode statusCode) : Exception(code)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
