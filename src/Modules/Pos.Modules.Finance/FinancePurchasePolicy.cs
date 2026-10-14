using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pos.Domain.Inventory;

namespace Pos.Modules.Finance;

/// <summary>Pure, decimal-only purchase, inventory-cost, and supplier-payment calculations.</summary>
public static class FinancePurchasePolicy
{
    public static decimal CalculateInvoiceTotal(decimal subtotal, decimal discount, decimal freight, decimal tax)
    {
        if (subtotal < 0m || discount < 0m || freight < 0m || tax < 0m || discount > subtotal)
            throw new ArgumentOutOfRangeException(nameof(subtotal), "Los importes de la factura no son válidos.");
        return Round(subtotal - discount + freight + tax);
    }

    public static decimal ConvertUnitCost(decimal invoiceUnitPrice, string invoiceCurrency, string productCurrency, decimal usdToVesRate)
    {
        if (invoiceUnitPrice < 0m) throw new ArgumentOutOfRangeException(nameof(invoiceUnitPrice));
        ValidateCurrency(invoiceCurrency, nameof(invoiceCurrency));
        ValidateCurrency(productCurrency, nameof(productCurrency));
        if (invoiceCurrency == productCurrency) return Round(invoiceUnitPrice);
        if (usdToVesRate <= 0m) throw new ArgumentOutOfRangeException(nameof(usdToVesRate));
        return Round(invoiceCurrency == "USD" ? invoiceUnitPrice * usdToVesRate : invoiceUnitPrice / usdToVesRate);
    }

    public static decimal? WeightedAverageUnitCost(decimal currentQuantity, decimal? currentUnitCost,
        decimal receivedQuantity, decimal receivedUnitCost)
        => InventoryCostPolicy.WeightedAverageUnitCost(currentQuantity, currentUnitCost, receivedQuantity, receivedUnitCost);

    public static decimal CalculateItemUnitCost(decimal quantity, decimal unitPrice, decimal discount,
        string invoiceCurrency, string productCurrency, decimal usdToVesRate)
    {
        if (quantity <= 0m || unitPrice < 0m || discount < 0m ||
            discount > quantity * unitPrice)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Los componentes del costo de compra no son válidos.");
        var invoiceUnitCost = Round((quantity * unitPrice - discount) / quantity);
        return ConvertUnitCost(invoiceUnitCost, invoiceCurrency, productCurrency, usdToVesRate);
    }

    public static decimal? CalculateSuggestedPrice(decimal? unitCost, decimal? targetGrossMargin)
    {
        if (unitCost is null || targetGrossMargin is null) return null;
        if (unitCost < 0m || targetGrossMargin < 0m || targetGrossMargin >= 1m)
            throw new ArgumentOutOfRangeException(nameof(targetGrossMargin), "El costo debe ser no negativo y el margen debe estar entre 0 % y menos de 100 %.");
        return Round(unitCost.Value / (1m - targetGrossMargin.Value));
    }

    public static ImmediateSettlement CalculateImmediateSettlement(decimal invoiceTotal, string invoiceCurrency,
        decimal paymentAmount, string paymentCurrency, decimal currentUsdToVesRate, decimal invoiceUsdToVesRate)
    {
        if (invoiceTotal <= 0m || paymentAmount <= 0m || invoiceUsdToVesRate <= 0m)
            throw new ArgumentOutOfRangeException(nameof(invoiceTotal), "El importe y la tasa de la factura deben ser positivos.");
        ValidateCurrency(invoiceCurrency, nameof(invoiceCurrency));
        ValidateCurrency(paymentCurrency, nameof(paymentCurrency));

        var currentRate = currentUsdToVesRate;
        if (invoiceCurrency != paymentCurrency && currentRate <= 0m)
            throw new InvalidOperationException("exchange_rate_unavailable");
        var conversionRate = invoiceCurrency == paymentCurrency ? 1m : currentRate;
        var principal = ConvertPaymentToInvoiceCurrency(paymentAmount, paymentCurrency, invoiceCurrency, conversionRate);
        if (principal != Round(invoiceTotal))
            throw new InvalidOperationException("immediate_payment_must_equal_invoice_total");

        var paymentUsdRate = currentRate > 0m ? currentRate : paymentCurrency == "USD" ? 1m : 0m;
        decimal? paymentUsd = paymentUsdRate > 0m ? ToUsdEquivalent(paymentAmount, paymentCurrency, paymentUsdRate) : null;
        decimal? fxDifference = paymentUsd.HasValue
            ? CalculateRealizedFxGainUsd(principal, invoiceCurrency, invoiceUsdToVesRate, paymentAmount, paymentCurrency, paymentUsdRate)
            : null;
        var appliedRate = invoiceCurrency == paymentCurrency ? 1m : paymentCurrency == "USD" ? currentRate : 1m / currentRate;
        return new(principal, appliedRate, paymentUsd, fxDifference);
    }

    public static decimal ConvertPaymentToInvoiceCurrency(decimal paymentAmount, string paymentCurrency,
        string invoiceCurrency, decimal usdToVesRate)
    {
        if (paymentAmount <= 0m) throw new ArgumentOutOfRangeException(nameof(paymentAmount));
        ValidateCurrency(paymentCurrency, nameof(paymentCurrency));
        ValidateCurrency(invoiceCurrency, nameof(invoiceCurrency));
        if (paymentCurrency == invoiceCurrency) return Round(paymentAmount);
        if (usdToVesRate <= 0m) throw new ArgumentOutOfRangeException(nameof(usdToVesRate));
        return Round(paymentCurrency == "VES" ? paymentAmount / usdToVesRate : paymentAmount * usdToVesRate);
    }

    public static decimal ToUsdEquivalent(decimal amount, string currency, decimal usdToVesRate)
    {
        if (amount < 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        ValidateCurrency(currency, nameof(currency));
        if (currency == "USD") return Round(amount);
        if (usdToVesRate <= 0m) throw new ArgumentOutOfRangeException(nameof(usdToVesRate));
        return Round(amount / usdToVesRate);
    }

    /// <summary>Positive result is a realized gain in USD; negative result is a realized loss.</summary>
    public static decimal CalculateRealizedFxGainUsd(decimal invoiceAmountReduced, string invoiceCurrency,
        decimal invoiceUsdToVesRate, decimal paymentAmount, string paymentCurrency, decimal paymentUsdToVesRate)
    {
        ValidateCurrency(invoiceCurrency, nameof(invoiceCurrency));
        ValidateCurrency(paymentCurrency, nameof(paymentCurrency));
        var carryingValueUsd = ToUsdEquivalent(invoiceAmountReduced, invoiceCurrency, invoiceUsdToVesRate);
        var paymentValueUsd = ToUsdEquivalent(paymentAmount, paymentCurrency, paymentUsdToVesRate);
        return Round(carryingValueUsd - paymentValueUsd);
    }

    public static decimal Round(decimal amount) => decimal.Round(amount, 4, MidpointRounding.AwayFromZero);

    private static void ValidateCurrency(string currency, string parameterName)
    {
        if (currency is not ("USD" or "VES")) throw new ArgumentOutOfRangeException(parameterName, "Solo se admiten USD y VES.");
    }
}

public sealed record ImmediateSettlement(decimal InvoiceCurrencyAmount, decimal AppliedExchangeRate,
    decimal? PaymentUsdEquivalent, decimal? FxDifferenceUsd);

public static class FinanceIdempotencyPolicy
{
    public static string HashPayment(Guid invoiceId, FinanceModule.SupplierPaymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new
        {
            invoiceId,
            request.Amount,
            request.Currency,
            method = request.Method.Trim(),
            account = request.ReceivingAccount.Trim(),
            reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)))).ToLowerInvariant();
    }
}
