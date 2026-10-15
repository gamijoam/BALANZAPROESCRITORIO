namespace Pos.Modules.Finance;

internal static class FinanceSql
{
    internal static string LoadInvoiceReceiptCosts(string invoiceLinesTable) => $"""
        SELECT l.id,l.product_id,l.quantity,l.received_quantity,l.unit_price,l.discount,l.tax,l.tax_recoverable,p."Currency"
        FROM {invoiceLinesTable} l JOIN products p ON p."Id"=l.product_id
        WHERE l.invoice_id=@invoice ORDER BY l.id FOR UPDATE OF l
        """;

    internal const string ReadCurrentUsdToVesRate = """
        SELECT "Rate" FROM exchange_rates
        WHERE "FromCurrency"='USD' AND "ToCurrency"='VES' AND "EffectiveAtUtc"<=@now
        ORDER BY "EffectiveAtUtc" DESC LIMIT 1
        """;
}
