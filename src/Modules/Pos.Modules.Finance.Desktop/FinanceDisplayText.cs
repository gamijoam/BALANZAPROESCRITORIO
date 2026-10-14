namespace Pos.Modules.Finance.Desktop;

public static class FinanceDisplayText
{
    public static string PurchaseStatus(string? status) => status switch
    {
        "Draft" => "Borrador",
        "Posted" => "Contabilizada",
        "Partial" => "Pago parcial",
        "Paid" => "Pagada",
        "Reversed" => "Reversada",
        _ => "Estado desconocido"
    };

    public static string DocumentType(string? documentType) => documentType switch
    {
        "Invoice" => "Factura",
        "DebitNote" => "Nota de débito",
        _ => "Documento"
    };

    public static string PaymentTerm(string? paymentTerm) => paymentTerm switch
    {
        "Credit" => "Crédito",
        "Immediate" => "Contado",
        _ => "Condición desconocida"
    };
}
