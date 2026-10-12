namespace Pos.Desktop.Presentation;

public static class SaleHistoryReceiptMapper
{
    public static SaleReceipt ToReceipt(SaleDetailRow sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        var lines = sale.Lines.Select(line => new ReceiptLine(
            line.Sku,
            line.Description,
            line.Quantity,
            line.UnitPrice,
            line.LineTotal,
            line.OriginalUnitPrice,
            line.OriginalCurrency,
            null)).ToArray();
        var payments = sale.Payments
            .Select(payment => new SalePaymentInput(payment.Method, payment.Amount, payment.Currency))
            .ToArray();

        return new SaleReceipt(sale.Id, sale.CreatedAtUtc, sale.Currency, sale.Total, lines, payments)
        {
            RoundingAdjustment = sale.RoundingAdjustment,
            CustomerName = sale.CustomerName
        };
    }
}
