using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Pos.Desktop.Presentation;

public static class PosNumberFormat
{
    public static CultureInfo Culture { get; } = CreateCulture();

    public static string FormatAmount(decimal value) => value.ToString("N2", Culture);

    public static string FormatQuantity(decimal value) => value.ToString("0.###", Culture);

    public static bool TryParseAmount(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, Culture, out value);

    private static CultureInfo CreateCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("es-VE").Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";
        culture.NumberFormat.NumberGroupSizes = [3];
        culture.NumberFormat.CurrencyDecimalSeparator = ",";
        culture.NumberFormat.CurrencyGroupSeparator = ".";
        culture.NumberFormat.CurrencyGroupSizes = [3];
        return CultureInfo.ReadOnly(culture);
    }
}

public static class PosUserMessage
{
    private const string Generic = "No se pudo completar la operación. Revisa los datos e inténtalo de nuevo.";

    public static string ForException(Exception exception) => exception switch
    {
        PosApiException api => ForApiError(api),
        HttpRequestException => "No se pudo conectar con el sistema. Inténtalo de nuevo.",
        ArgumentException => "Revisa los datos ingresados e inténtalo de nuevo.",
        _ => Generic
    };

    private static string ForApiError(PosApiException exception) => exception.Code switch
    {
        "exchange_rate_unavailable" or "exchange_rate_changed" => "No hay una tasa de cambio vigente. Solicita actualizarla.",
        "insufficient_stock" => "No hay existencia suficiente para completar la venta.",
        "credit_limit_exceeded" => "La venta supera el límite de crédito del cliente.",
        "credit_customer_required" => "Selecciona un cliente para registrar la venta a crédito.",
        "customer_unavailable" or "product_unavailable" => "El cliente o producto seleccionado ya no está disponible.",
        "invalid_product_price" => "El producto no tiene un precio válido.",
        "open_cash_session_required" => "Abre una caja antes de continuar.",
        "insufficient_cash" => "El monto supera el efectivo disponible en caja.",
        "currency_mismatch" => "La moneda de la operación no coincide con la cuenta del cliente.",
        "payment_total_mismatch" => "Los pagos no coinciden con el total de la venta.",
        "sku_already_exists" => "Ese código de producto ya está registrado.",
        "customer_document_already_exists" => "Ese documento ya está registrado.",
        "rate_already_exists_for_effective_time" => "Ya existe una tasa para esa fecha y hora.",
        "unauthorized" when exception.StatusCode == HttpStatusCode.Unauthorized => "La sesión venció. Inicia sesión de nuevo.",
        _ when exception.StatusCode == HttpStatusCode.Unauthorized => "La sesión venció. Inicia sesión de nuevo.",
        _ when exception.StatusCode == HttpStatusCode.Forbidden => "No tienes permiso para realizar esta operación.",
        _ => Generic
    };
}
