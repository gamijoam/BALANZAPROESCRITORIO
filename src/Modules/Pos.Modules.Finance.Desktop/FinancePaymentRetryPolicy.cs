namespace Pos.Modules.Finance.Desktop;

public static class FinancePaymentRetryPolicy
{
    public static FinanceWindow.SupplierPaymentRequest CreateOrReuse(Guid invoiceId, Guid? pendingInvoiceId,
        FinanceWindow.SupplierPaymentRequest? pendingRequest, decimal amount, string currency, string method,
        string receivingAccount, string? reference)
    {
        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        var normalizedMethod = method.Trim();
        var normalizedAccount = receivingAccount.Trim();
        var normalizedReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

        if (pendingRequest is not null && pendingInvoiceId == invoiceId && pendingRequest.Amount == amount &&
            string.Equals(pendingRequest.Currency, normalizedCurrency, StringComparison.Ordinal) &&
            string.Equals(pendingRequest.Method, normalizedMethod, StringComparison.Ordinal) &&
            string.Equals(pendingRequest.ReceivingAccount, normalizedAccount, StringComparison.Ordinal) &&
            string.Equals(pendingRequest.Reference, normalizedReference, StringComparison.Ordinal))
            return pendingRequest;

        return new FinanceWindow.SupplierPaymentRequest(amount, normalizedCurrency, normalizedMethod, normalizedAccount,
            normalizedReference, Guid.NewGuid().ToString("N"));
    }
}
