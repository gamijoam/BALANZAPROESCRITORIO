using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Modules.Reporting.Desktop;

public sealed class ReportingDesktopModule : IPosDesktopModule
{
    public string ModuleId => "advanced-reports";

    public void Configure(PosDesktopModuleRegistry registry)
    {
        registry.RegisterReportCenter("reports.advanced", "Reportes", "Reports.View", context => new ReportCenterWindow(context));
        registry.RegisterReportSource(Source("reports.sales.daily", "Ventas por día", "Reports.View",
            [Text("fecha", "Fecha"), Text("moneda", "Moneda"), Integer("ventas", "Ventas"), Decimal("total", "Total")], LoadDailySales,
            supportsDateRange: true));
        registry.RegisterReportSource(Source("reports.sales.detail", "Ventas detalladas", "Reports.View",
            [Text("numero", "Venta"), Date("fecha", "Fecha"), Text("cliente", "Cliente"), Text("operador", "Responsable"),
             Text("sku", "Código"), Text("producto", "Producto"), Decimal("cantidad", "Cantidad"), Decimal("precio", "Precio"),
             Decimal("importe", "Importe"), Text("moneda", "Moneda"), Text("estado", "Estado"), Text("pagos", "Pagos")], LoadSalesDetails,
            supportsDateRange: true));
        registry.RegisterReportSource(Source("reports.sales.products", "Ventas por producto", "Reports.View",
            [Text("sku", "Código"), Text("producto", "Producto"), Text("moneda", "Moneda"), Decimal("vendido", "Vendido"),
             Decimal("devuelto", "Devuelto"), Decimal("neto_unidades", "Unidades netas"), Decimal("bruto", "Importe bruto"),
             Decimal("devoluciones", "Importe devuelto"), Decimal("neto", "Importe neto")], LoadProductSales,
            supportsDateRange: true));
        registry.RegisterReportSource(Source("reports.cash.sessions", "Cajas y arqueos", "Reports.View",
            [Date("apertura", "Apertura"), Date("cierre", "Cierre"), Text("operador", "Responsable"), Text("estado", "Estado"),
             Decimal("usd_esperado", "USD esperado"), Decimal("usd_contado", "USD contado"), Decimal("usd_diferencia", "Diferencia USD"),
             Decimal("ves_esperado", "VES esperado"), Decimal("ves_contado", "VES contado"), Decimal("ves_diferencia", "Diferencia VES")], LoadCashSessions,
            supportsDateRange: true));
        registry.RegisterReportSource(Source("reports.cash.methods", "Cobros y reintegros por caja", "Reports.View",
            [Text("caja", "Turno / caja"), Date("apertura", "Apertura"), Text("operador", "Responsable"), Text("estado", "Estado"),
             Text("operacion", "Operación"), Text("metodo", "Método"), Text("moneda", "Moneda"), Decimal("importe", "Importe")], LoadCashMethods,
            supportsDateRange: true));
        registry.RegisterReportSource(Source("reports.receivables.balances", "Saldos por cobrar", "Reports.View",
            [Text("cliente", "Cliente"), Decimal("saldo_usd", "Saldo USD"), Decimal("equivalente_ves", "Equivalente VES"),
             Decimal("tasa_actual", "Tasa actual USD/VES"), Decimal("limite_usd", "Límite USD"),
             Decimal("disponible_usd", "Crédito disponible USD"), Date("ultimo_movimiento", "Último movimiento")], LoadReceivableBalances));
        registry.RegisterReportSource(Source("reports.receivables.movements", "Movimientos de cuentas por cobrar", "Reports.View",
            [Date("fecha", "Fecha"), Text("cliente", "Cliente"), Text("tipo", "Tipo"), Decimal("importe_usd", "Importe USD"),
             Decimal("importe_original", "Importe original"), Text("moneda", "Moneda original"), Decimal("tasa", "Tasa aplicada"),
             Text("referencia", "Referencia"), Text("venta", "Venta"), Text("operador", "Operador"), Text("terminal", "Terminal")],
            LoadReceivableMovements, supportsDateRange: true));
        registry.RegisterReportSource(Source("reports.inventory.products", "Existencias por producto", "Reports.Inventory",
            [Text("sku", "Código"), Text("producto", "Producto"), Decimal("existencia", "Existencia"), Decimal("precio", "Precio"), Text("moneda", "Moneda")], LoadInventory));
        registry.RegisterReportSource(Source("reports.inventory.movements", "Movimientos de inventario", "Reports.View",
            [Date("fecha", "Fecha"), Text("sku", "Código"), Text("producto", "Producto"), Decimal("variacion", "Variación"),
             Text("motivo", "Motivo"), Text("operador", "Operador"), Text("terminal", "Terminal"), Text("referencia", "Referencia")], LoadInventoryMovements,
            supportsDateRange: true));
    }

    private static PosDesktopReportSource Source(string id, string title, string permission,
        IReadOnlyList<PosDesktopReportColumn> columns,
        Func<PosDesktopModuleContext, PosDesktopReportQuery, CancellationToken, Task<PosDesktopReportResult>> load,
        bool supportsDateRange = false) => new(id, title, permission, columns, load, supportsDateRange);

    private static PosDesktopReportColumn Text(string id, string title) => new(id, title, PosDesktopReportValueType.Text);
    private static PosDesktopReportColumn Date(string id, string title) => new(id, title, PosDesktopReportValueType.DateTime);
    private static PosDesktopReportColumn Decimal(string id, string title) => new(id, title, PosDesktopReportValueType.Decimal);
    private static PosDesktopReportColumn Integer(string id, string title) => new(id, title, PosDesktopReportValueType.Integer);

    private static async Task<PosDesktopReportResult> LoadDailySales(PosDesktopModuleContext context, PosDesktopReportQuery query, CancellationToken ct)
    {
        var api = RequireReports(context);
        var report = await api.GetSalesReportAsync(From(query), To(query), ct);
        var rows = report.Sales.Select(row => Row(
            ("fecha", row.Day), ("moneda", row.Currency), ("ventas", row.Count), ("total", row.Total)));
        return Rows(rows);
    }

    private static async Task<PosDesktopReportResult> LoadSalesDetails(PosDesktopModuleContext context, PosDesktopReportQuery query, CancellationToken ct)
    {
        var api = RequireReports(context); var result = new List<IReadOnlyDictionary<string, object?>>();
        var currency = Filter(query, "currency");
        for (var page = 1; ; page++)
        {
            var report = await api.GetSalesDetailReportAsync(From(query), To(query), currency, page, ct);
            foreach (var sale in report.Sales)
            foreach (var line in sale.Lines)
                result.Add(Row(("numero", sale.SaleNumber), ("fecha", sale.CreatedAtUtc), ("cliente", sale.CustomerName),
                    ("operador", sale.OperatorName), ("sku", line.Sku), ("producto", line.Description), ("cantidad", line.Quantity),
                    ("precio", line.UnitPrice), ("importe", line.LineTotal), ("moneda", sale.Currency), ("estado", sale.StatusLabel),
                    ("pagos", sale.PaymentCurrenciesLabel)));
            if (report.Sales.Count == 0 || page * report.PageSize >= report.TotalCount) break;
        }
        return new(result);
    }

    private static async Task<PosDesktopReportResult> LoadProductSales(PosDesktopModuleContext context, PosDesktopReportQuery query, CancellationToken ct)
    {
        var report = await RequireReports(context).GetProductSalesReportAsync(From(query), To(query), Filter(query, "currency"), ct);
        return Rows(report.Products.Select(row => Row(("sku", row.Sku), ("producto", row.Name), ("moneda", row.Currency),
            ("vendido", row.SoldQuantity), ("devuelto", row.ReturnedQuantity), ("neto_unidades", row.NetQuantity),
            ("bruto", row.GrossAmount), ("devoluciones", row.ReturnedAmount), ("neto", row.NetAmount))));
    }

    private static async Task<PosDesktopReportResult> LoadCashSessions(PosDesktopModuleContext context, PosDesktopReportQuery query, CancellationToken ct)
    {
        var api = RequireReports(context); var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var page = 1; ; page++)
        {
            var report = await api.GetCashSessionReportPageAsync(From(query), To(query), null, page, 250, ct);
            rows.AddRange(report.Sessions.Select(row => Row(("apertura", row.OpenedAtUtc), ("cierre", row.ClosedAtUtc),
                ("operador", row.OperatorName), ("estado", row.StatusLabel), ("usd_esperado", row.ExpectedUsd),
                ("usd_contado", row.CountedUsd), ("usd_diferencia", row.VarianceUsd), ("ves_esperado", row.ExpectedVes),
                ("ves_contado", row.CountedVes), ("ves_diferencia", row.VarianceVes))));
            if (report.Sessions.Count == 0 || page * report.PageSize >= report.TotalCount) break;
        }
        return new(rows);
    }

    private static async Task<PosDesktopReportResult> LoadCashMethods(PosDesktopModuleContext context, PosDesktopReportQuery query, CancellationToken ct)
    {
        var api = RequireReports(context); var rows = new List<IReadOnlyDictionary<string, object?>>();
        var status = Filter(query, "status");
        for (var page = 1; ; page++)
        {
            var report = await api.GetCashSessionReportPageAsync(From(query), To(query), status, page, 250, ct);
            foreach (var session in report.Sessions)
            foreach (var payment in session.Payments)
                rows.Add(Row(("caja", session.Id.ToString("N")), ("apertura", session.OpenedAtUtc),
                    ("operador", session.OperatorName), ("estado", session.StatusLabel), ("operacion", payment.Operation),
                    ("metodo", payment.Method), ("moneda", payment.Currency), ("importe", payment.Amount)));
            if (report.Sessions.Count == 0 || page * report.PageSize >= report.TotalCount) break;
        }
        return new(rows);
    }

    private static async Task<PosDesktopReportResult> LoadReceivableBalances(PosDesktopModuleContext context,
        PosDesktopReportQuery query, CancellationToken ct)
    {
        var api = RequireReports(context); var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var page = 1; ; page++)
        {
            var report = await api.GetReceivableBalanceReportPageAsync(page, 250, ct);
            rows.AddRange(report.Customers.Select(customer => Row(("cliente", customer.CustomerName),
                ("saldo_usd", customer.BalanceUsd), ("equivalente_ves", customer.BalanceVesEquivalent),
                ("tasa_actual", report.CurrentUsdToVesRate), ("limite_usd", customer.CreditLimitUsd),
                ("disponible_usd", customer.AvailableCreditUsd), ("ultimo_movimiento", customer.LastMovementAtUtc))));
            if (report.Customers.Count == 0 || page * report.PageSize >= report.TotalCount) break;
        }
        return new(rows);
    }

    private static async Task<PosDesktopReportResult> LoadReceivableMovements(PosDesktopModuleContext context,
        PosDesktopReportQuery query, CancellationToken ct)
    {
        var api = RequireReports(context); var rows = new List<IReadOnlyDictionary<string, object?>>();
        var currency = Filter(query, "currency");
        for (var page = 1; ; page++)
        {
            var report = await api.GetReceivableMovementReportPageAsync(From(query), To(query), currency, page, 250, ct);
            rows.AddRange(report.Movements.Select(movement => Row(("fecha", movement.OccurredAtUtc),
                ("cliente", movement.CustomerName), ("tipo", movement.EntryType == "Charge" ? "Cargo" : "Abono"),
                ("importe_usd", movement.AmountUsd), ("importe_original", movement.OriginalAmount),
                ("moneda", movement.OriginalCurrency ?? "USD"), ("tasa", movement.AppliedExchangeRate),
                ("referencia", movement.Reference), ("venta", movement.SaleId?.ToString("N")),
                ("operador", movement.OperatorName), ("terminal", movement.TerminalId.ToString("N")[..8]))));
            if (report.Movements.Count == 0 || page * report.PageSize >= report.TotalCount) break;
        }
        return new(rows);
    }

    private static async Task<PosDesktopReportResult> LoadInventory(PosDesktopModuleContext context, PosDesktopReportQuery query, CancellationToken ct)
    {
        var products = await RequireReports(context).GetInventoryAsync(ct);
        return Rows(products.Select(item => Row(("sku", item.Sku), ("producto", item.Name), ("existencia", item.Quantity),
            ("precio", item.UnitPrice), ("moneda", item.Currency))));
    }

    private static async Task<PosDesktopReportResult> LoadInventoryMovements(PosDesktopModuleContext context, PosDesktopReportQuery query, CancellationToken ct)
    {
        var api = RequireReports(context); var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var page = 1; ; page++)
        {
            var chunk = await api.GetInventoryMovementReportAsync(From(query), To(query), page, 250, ct);
            rows.AddRange(chunk.Select(item => Row(("fecha", item.OccurredAtUtc), ("sku", item.Sku), ("producto", item.ProductName),
                ("variacion", item.QuantityDelta), ("motivo", item.Reason), ("operador", item.Actor),
                ("terminal", item.TerminalId.ToString("N")[..8]), ("referencia", item.Reference ?? ""))));
            if (chunk.Count < 250) break;
        }
        return new(rows);
    }

    private static IPosApiClient RequireReports(PosDesktopModuleContext context)
        => context.ReportApiClient ?? throw new InvalidOperationException("El cliente de reportes no está disponible en esta versión del POS.");

    private static DateTime From(PosDesktopReportQuery query) => query.FromUtc ?? DateTime.UtcNow.Date.AddDays(-30);
    private static DateTime To(PosDesktopReportQuery query) => query.ToUtc ?? DateTime.UtcNow.Date.AddDays(1);
    private static string? Filter(PosDesktopReportQuery query, string key) => query.Filters.GetValueOrDefault(key);
    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values)
        => values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    private static PosDesktopReportResult Rows(IEnumerable<IReadOnlyDictionary<string, object?>> rows) => new(rows.ToArray());
}
