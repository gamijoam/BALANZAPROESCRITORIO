namespace Pos.Modules.Finance.Desktop;

public static class FinanceDraftValidation
{
    public static bool HasDocumentNumber(string? documentNumber)
        => !string.IsNullOrWhiteSpace(documentNumber);
}
