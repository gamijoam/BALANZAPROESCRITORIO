using Pos.Desktop.Modules.Abstractions;
using System.Globalization;

namespace Pos.Modules.Finance.Desktop;

public sealed class FinanceDesktopModule : IPosDesktopModule
{
    public string ModuleId => "finance";

    public void Configure(PosDesktopModuleRegistry registry)
    {
        registry.RegisterReportSource(new PosDesktopReportSource("finance.payables", "Cuentas por pagar", "Purchase.View",
        [
            new("proveedor", "Proveedor", PosDesktopReportValueType.Text),
            new("tipo_documento", "Tipo", PosDesktopReportValueType.Text),
            new("documento", "Documento", PosDesktopReportValueType.Text),
            new("moneda", "Moneda", PosDesktopReportValueType.Text),
            new("fecha_emision", "Emisión", PosDesktopReportValueType.DateTime),
            new("fecha_vencimiento", "Vencimiento", PosDesktopReportValueType.DateTime),
            new("estado", "Estado", PosDesktopReportValueType.Text),
            new("dias_vencidos", "Días vencidos", PosDesktopReportValueType.Integer),
            new("total_documento", "Total del documento", PosDesktopReportValueType.Decimal),
            new("saldo_pendiente", "Saldo pendiente", PosDesktopReportValueType.Decimal),
            new("saldo_usd", "Saldo equivalente USD", PosDesktopReportValueType.Decimal),
            new("tasa", "Tasa aplicada", PosDesktopReportValueType.Decimal)
        ], LoadPayablesAsync));
        registry.RegisterMenu("finance.main", "Compras y proveedores", "Supplier.View", context => new FinanceWindow(context));
        registry.RegisterMenu("finance.summary", "Resumen financiero", "Finance.Reports", context => new FinanceWindow(context, openSummary: true));
    }

    private static async Task<PosDesktopReportResult> LoadPayablesAsync(PosDesktopModuleContext context,
        PosDesktopReportQuery query, CancellationToken cancellationToken)
    {
        const int pageSize = 1000;
        var payables = new List<PayableReportClientInfo>();
        for (var page = 1; ; page++)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture)
            };
            var result = await context.ApiClient.GetModuleAsync<List<PayableReportClientInfo>>(
                "finance", "payables", parameters, cancellationToken) ?? [];
            payables.AddRange(result);
            if (result.Count < pageSize) break;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var rows = payables
            .Select(item =>
            {
                var daysOverdue = item.DueDate is { } dueDate && dueDate < today ? today.DayNumber - dueDate.DayNumber : 0;
                var status = item.DueDate switch
                {
                    null => "Sin vencimiento",
                    { } due when due < today => "Vencida",
                    { } due when due == today => "Vence hoy",
                    { } due when due <= today.AddDays(7) => "Por vencer",
                    _ => "Vigente"
                };
                return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["proveedor_id"] = item.SupplierId,
                    ["proveedor"] = item.SupplierName,
                    ["tipo_documento"] = DocumentType(item.DocumentType),
                    ["documento"] = item.DocumentNumber,
                    ["moneda"] = item.Currency,
                    ["fecha_emision"] = item.IssueDate.ToDateTime(TimeOnly.MinValue),
                    ["fecha_vencimiento"] = item.DueDate?.ToDateTime(TimeOnly.MinValue),
                    ["estado"] = status,
                    ["dias_vencidos"] = daysOverdue,
                    ["total_documento"] = item.InvoiceAmount,
                    ["saldo_pendiente"] = item.Balance,
                    ["saldo_usd"] = item.BalanceUsdEquivalent,
                    ["tasa"] = item.ExchangeRate
                };
            }).ToArray();
        return new(rows);
    }

    private static string DocumentType(string value) => value switch
    {
        "Invoice" => "Factura",
        "DebitNote" => "Nota de débito",
        _ => value
    };
}

public sealed record PayableReportClientInfo(Guid InvoiceId, Guid SupplierId, string SupplierName, string DocumentType,
    string DocumentNumber, string Currency, DateOnly IssueDate, DateOnly? DueDate, decimal InvoiceAmount,
    decimal Balance, decimal BalanceUsdEquivalent, decimal ExchangeRate);
