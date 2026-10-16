using System.Globalization;
using Pos.Desktop.Modules.Abstractions;

namespace Pos.Modules.Reporting.Desktop;

public sealed record ReportSummaryMetric(string Label, string Value, string? Detail = null);

internal static class ReportSummaryProjection
{
    public static IReadOnlyList<ReportSummaryMetric> Build(string reportId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var data = rows.ToArray();
        return reportId switch
        {
            "reports.receivables.balances" => ReceivableBalances(data),
            "reports.receivables.movements" => ReceivableMovements(data),
            "reports.sales.daily" => SalesDaily(data),
            "reports.sales.detail" => SalesDetails(data),
            "reports.sales.products" => ProductSales(data),
            "reports.sales.profit" => SalesProfit(data),
            "finance.payables" => Payables(data),
            "reports.cash.sessions" => CashSessions(data),
            "reports.cash.methods" => CashMethods(data),
            "reports.inventory.products" => Inventory(data, "existencia", "precio", "sku"),
            "reports.inventory.valuation" => InventoryValuation(data),
            "warehouses.stock" => Inventory(data, "existencia", "precio", "sku"),
            "reports.inventory.price-margins" => PriceMargins(data),
            "reports.inventory.movements" => InventoryMovements(data),
            "warehouses.transfers" => WarehouseTransfers(data),
            _ => [new ReportSummaryMetric("Registros", Count(data.Length), "Filas del reporte con los filtros actuales")]
        };
    }

    private static IReadOnlyList<ReportSummaryMetric> ReceivableBalances(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        =>
        [
            new("Clientes con deuda", Count(rows.Count(row => Number(row, "saldo_usd") > 0m)), "Clientes que tienen saldo pendiente"),
            new("Deuda total USD", Money(Sum(rows, "saldo_usd")), "Saldos pendientes convertidos a USD"),
            new("Equivalente total VES",
                rows.Any(row => row.GetValueOrDefault("equivalente_ves") is null) ? "No disponible" : Money(Sum(rows, "equivalente_ves")),
                rows.Any(row => row.GetValueOrDefault("equivalente_ves") is null)
                    ? "No hay una tasa vigente para convertir todos los saldos"
                    : "Conversión a la tasa actual mostrada en el reporte"),
            new("Crédito disponible USD", Money(Sum(rows, "disponible_usd")), "Disponible acumulado de los límites de crédito")
        ];

    private static IReadOnlyList<ReportSummaryMetric> ReceivableMovements(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        =>
        [
            new("Movimientos", Count(rows.Count), "Cargos y abonos del período"),
            new("Clientes", Count(rows.Select(row => Text(row, "cliente")).Where(value => value.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Count()), "Clientes con movimientos en los filtros actuales"),
            new("Cargos USD", Money(Sum(rows.Where(row => Text(row, "tipo") == "Cargo"), "importe_usd")), "Importe normalizado a USD"),
            new("Abonos USD", Money(Sum(rows.Where(row => Text(row, "tipo") == "Abono"), "importe_usd")), "Importe normalizado a USD")
        ];

    private static IReadOnlyList<ReportSummaryMetric> SalesDaily(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var result = new List<ReportSummaryMetric>
        {
            new("Ventas", Count(rows.Sum(row => (long)Number(row, "ventas"))), "Cantidad de operaciones registradas")
        };
        result.AddRange(SumsByCurrency(rows, "total", "Total vendido"));
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> SalesDetails(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var result = new List<ReportSummaryMetric>
        {
            new("Ventas", Count(rows.Select(row => Text(row, "numero")).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Count()), "Boletas representadas en las filas filtradas"),
            new("Líneas", Count(rows.Count), "Artículos detallados en el reporte"),
            new("Unidades", Quantity(Sum(rows, "cantidad")), "Cantidad de artículos antes de devoluciones")
        };
        result.AddRange(SumsByCurrency(rows, "importe", "Importe bruto"));
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> ProductSales(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var result = new List<ReportSummaryMetric>
        {
            new("Productos", Count(rows.Select(row => Text(row, "sku")).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count()), "Productos con ventas en los filtros actuales")
        };
        foreach (var group in rows.GroupBy(row => Currency(row)).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            result.Add(new($"Unidades netas {group.Key}", Quantity(group.Sum(row => Number(row, "neto_unidades"))), "Unidades vendidas menos las devueltas"));
            result.Add(new($"Venta neta {group.Key}", Money(group.Sum(row => Number(row, "neto"))), "Importe bruto menos devoluciones"));
        }
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> SalesProfit(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var calculable = rows.Where(row => Text(row, "estado_costo") == "Calculado con costo histórico" &&
                                           Text(row, "estado_impuesto") != "Impuesto histórico no registrado").ToArray();
        var result = new List<ReportSummaryMetric>
        {
            new("Ventas", Count(rows.Select(row => Text(row, "venta")).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Count()), "Ventas incluidas en el período"),
            new("Artículos", Count(rows.Count), "Líneas de venta incluidas en el período"),
            new("Líneas sin costo histórico", Count(rows.Count(row => Text(row, "estado_costo") == "Costo histórico no registrado")), "No se puede calcular su utilidad sin el costo guardado al vender"),
            new("Líneas sin conversión", Count(rows.Count(row => Text(row, "estado_costo") == "Moneda o tasa no disponible")), "Hay costo registrado, pero falta la moneda o tasa histórica para convertirlo"),
            new("Líneas sin impuesto histórico", Count(rows.Count(row => Text(row, "estado_impuesto") == "Impuesto histórico no registrado")), "No se puede separar el impuesto; venta neta y utilidad excluidas del resumen")
        };
        foreach (var group in rows.Where(row => TryNumber(row, "venta_neta", out _)).GroupBy(Currency).OrderBy(group => group.Key, StringComparer.Ordinal))
            result.Add(new($"Venta neta {group.Key}", Money(group.Sum(row => Number(row, "venta_neta"))), "Impuestos registrados excluidos; menos devoluciones"));
        foreach (var group in calculable.GroupBy(Currency).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var revenue = group.Sum(row => Number(row, "venta_neta"));
            var profit = group.Sum(row => Number(row, "utilidad_bruta"));
            result.Add(new($"Costo de venta {group.Key}", Money(group.Sum(row => Number(row, "costo_total"))), "Costo histórico de las unidades no reingresadas"));
            result.Add(new($"Utilidad bruta {group.Key}", Money(profit), "Calculada solo en líneas con costo e impuesto histórico disponibles"));
            if (revenue > 0m)
                result.Add(new($"Margen bruto {group.Key}", Percent(profit / revenue * 100m), "Utilidad bruta ÷ venta neta calculable"));
        }
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> Payables(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var overdue = rows.Where(row => Text(row, "estado") == "Vencida").ToArray();
        var dueSoon = rows.Where(row => Text(row, "estado") is "Vence hoy" or "Por vencer").ToArray();
        var result = new List<ReportSummaryMetric>
        {
            new("Documentos pendientes", Count(rows.Count), "Facturas y notas con saldo abierto"),
            new("Proveedores", Count(rows.Select(row => Text(row, "proveedor_id")).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Count()), "Proveedores con saldo abierto"),
            new("Saldo total equivalente USD", Money(Sum(rows, "saldo_usd")), "Saldos convertidos según la tasa registrada en cada documento"),
            new("Vencido equivalente USD", Money(Sum(overdue, "saldo_usd")), "Saldo de documentos vencidos"),
            new("Por vencer equivalente USD", Money(Sum(dueSoon, "saldo_usd")), "Saldo que vence hoy o dentro de los próximos siete días")
        };
        result.AddRange(rows.GroupBy(Currency).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReportSummaryMetric($"Saldo pendiente {group.Key}", Money(group.Sum(row => Number(row, "saldo_pendiente"))), "Saldo abierto en la moneda del documento")));
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> CashSessions(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        =>
        [
            new("Turnos de caja", Count(rows.Count), "Turnos incluidos en el período"),
            new("Turnos abiertos", Count(rows.Count(row => Text(row, "estado").Contains("abierta", StringComparison.CurrentCultureIgnoreCase))), "Siguen sin cierre de caja"),
            new("Diferencia USD", Money(Sum(rows, "usd_diferencia")), "Suma de diferencias registradas en los cierres"),
            new("Diferencia VES", Money(Sum(rows, "ves_diferencia")), "Suma de diferencias registradas en los cierres")
        ];

    private static IReadOnlyList<ReportSummaryMetric> CashMethods(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var result = new List<ReportSummaryMetric>
        {
            new("Operaciones", Count(rows.Count), "Cobros y reintegros registrados")
        };
        result.AddRange(rows.GroupBy(row => (Operation: Text(row, "operacion"), Currency: Currency(row)))
            .OrderBy(group => group.Key.Operation, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Key.Currency, StringComparer.Ordinal)
            .Select(group => new ReportSummaryMetric($"{group.Key.Operation} {group.Key.Currency}",
                Money(group.Sum(row => Number(row, "importe"))), "Total por moneda y tipo de operación")));
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> Inventory(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string quantityKey, string priceKey, string productKey)
    {
        var result = new List<ReportSummaryMetric>
        {
            new("Productos", Count(rows.Select(row => Text(row, productKey)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count()), "Productos visibles con los filtros actuales")
        };
        foreach (var group in rows.GroupBy(row => Currency(row)).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var value = group.Sum(row => Number(row, quantityKey) * Number(row, priceKey));
            result.Add(new($"Valor estimado {group.Key}", Money(value), "Existencia × precio de venta; no representa costo ni utilidad"));
        }
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> PriceMargins(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var validRows = rows.Where(row => TryNumber(row, "ganancia_unitaria", out _) &&
                                          TryNumber(row, "precio", out var price) && price > 0m &&
                                          TryNumber(row, "margen_pct", out _)).ToArray();
        var result = new List<ReportSummaryMetric>
        {
            new("Precios evaluables", Count(validRows.Length), "Registros con precio y costo disponibles")
        };
        foreach (var group in validRows.GroupBy(row => (Currency: Currency(row), Tier: Text(row, "nivel")))
                     .OrderBy(group => group.Key.Tier, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(group => group.Key.Currency, StringComparer.Ordinal))
        {
            var totalPrice = group.Sum(row => Number(row, "precio"));
            var weightedMargin = totalPrice > 0m
                ? group.Sum(row => Number(row, "ganancia_unitaria")) / totalPrice * 100m
                : 0m;
            result.Add(new($"Margen {group.Key.Tier} {group.Key.Currency}", Percent(weightedMargin),
                $"Promedio ponderado por precio · {Count(group.Count())} productos"));
        }
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> InventoryValuation(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var stocked = rows.Where(row => Number(row, "existencia") > 0m).ToArray();
        var valued = stocked.Where(row => TryNumber(row, "valor_costo", out _)).ToArray();
        var result = new List<ReportSummaryMetric>
        {
            new("Productos con existencia", Count(stocked.Length), "Productos activos o inactivos con unidades disponibles"),
            new("Productos sin costo", Count(stocked.Count(row => !TryNumber(row, "valor_costo", out _))), "No se pueden incluir en la valoración"),
            new("Unidades disponibles", Quantity(stocked.Sum(row => Number(row, "existencia"))), "Unidades físicas registradas en inventario")
        };
        foreach (var group in valued.GroupBy(Currency).OrderBy(group => group.Key, StringComparer.Ordinal))
            result.Add(new($"Valor al costo {group.Key}", Money(group.Sum(row => Number(row, "valor_costo"))),
                $"Costo unitario actual × existencia · {Count(group.Count())} productos con costo"));
        return result;
    }

    private static IReadOnlyList<ReportSummaryMetric> InventoryMovements(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        =>
        [
            new("Movimientos", Count(rows.Count), "Registros del período"),
            new("Productos afectados", Count(rows.Select(row => Text(row, "sku")).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count()), "Códigos distintos con movimientos"),
            new("Entradas", Count(rows.Count(row => Number(row, "variacion") > 0m)), "Movimientos con variación positiva"),
            new("Salidas", Count(rows.Count(row => Number(row, "variacion") < 0m)), "Movimientos con variación negativa")
        ];

    private static IReadOnlyList<ReportSummaryMetric> WarehouseTransfers(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var result = new List<ReportSummaryMetric>
        {
            new("Traslados", Count(rows.Count), "Traslados del período"),
            new("Líneas de producto", Count((long)rows.Sum(row => Number(row, "productos"))), "Productos incluidos en los traslados")
        };
        result.AddRange(rows.GroupBy(row => Text(row, "estado"))
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new ReportSummaryMetric(group.Key, Count(group.Count()), "Traslados en este estado")));
        return result;
    }

    private static IEnumerable<ReportSummaryMetric> SumsByCurrency(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string valueKey, string label)
        => rows.GroupBy(row => Currency(row)).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReportSummaryMetric($"{label} {group.Key}", Money(group.Sum(row => Number(row, valueKey))), "Total de las filas filtradas"));

    private static decimal Sum(IEnumerable<IReadOnlyDictionary<string, object?>> rows, string key)
        => rows.Sum(row => Number(row, key));

    private static decimal Number(IReadOnlyDictionary<string, object?> row, string key)
        => TryNumber(row, key, out var value) ? value : 0m;

    private static bool TryNumber(IReadOnlyDictionary<string, object?> row, string key, out decimal value)
    {
        value = row.GetValueOrDefault(key) switch
        {
            decimal amount => amount,
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            sbyte number => number,
            ushort number => number,
            uint number => number,
            ulong number => number,
            float number => (decimal)number,
            double number => (decimal)number,
            _ => 0m
        };
        return row.GetValueOrDefault(key) is decimal or byte or short or int or long or sbyte or ushort or uint or ulong or float or double;
    }

    private static string Currency(IReadOnlyDictionary<string, object?> row)
        => string.IsNullOrWhiteSpace(Text(row, "moneda")) ? "USD" : Text(row, "moneda");

    private static string Text(IReadOnlyDictionary<string, object?> row, string key)
        => row.GetValueOrDefault(key)?.ToString()?.Trim() ?? string.Empty;

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private static string Quantity(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private static string Percent(decimal value) => $"{value:N2}%";
    private static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
