namespace Pos.Modules.Finance.Desktop;

public static class FinanceReceiptRetryPolicy
{
    public static FinanceWindow.PurchaseReceiptRequest CreateRetryRequest(FinanceWindow.PurchaseReceiptClientInfo pendingReceipt)
    {
        ArgumentNullException.ThrowIfNull(pendingReceipt);
        return new(pendingReceipt.Header.IdempotencyKey, pendingReceipt.Header.WarehouseId,
            pendingReceipt.Lines.Select(line => new FinanceWindow.PurchaseReceiptLineRequest(line.InvoiceLineId, line.Quantity)).ToArray());
    }
}
