namespace Pos.Modules.Finance.Desktop;

public static class FinancePurchaseSelectionPolicy
{
    public static FinanceWindow.PurchaseSummaryClientInfo? Select(
        IReadOnlyList<FinanceWindow.PurchaseSummaryClientInfo> purchases,
        Guid? preferredId,
        Guid? currentId,
        FinanceWindow.PurchaseDraftRequest? preferredDraft = null)
    {
        ArgumentNullException.ThrowIfNull(purchases);
        return Find(preferredId) ?? FindDraft(preferredDraft) ?? Find(currentId);

        FinanceWindow.PurchaseSummaryClientInfo? Find(Guid? id)
            => id.HasValue ? purchases.FirstOrDefault(item => item.Id == id.Value) : null;

        FinanceWindow.PurchaseSummaryClientInfo? FindDraft(FinanceWindow.PurchaseDraftRequest? draft)
            => draft is null ? null : purchases.FirstOrDefault(item => item.SupplierId == draft.SupplierId &&
                string.Equals(item.DocumentType, draft.DocumentType, StringComparison.Ordinal) &&
                string.Equals(item.DocumentNumber, draft.DocumentNumber.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
