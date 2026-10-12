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
        ArgumentException argument when argument.ParamName == "payments" => PaymentValidationMessage(argument),
        ArgumentException => "Revisa los datos ingresados e inténtalo de nuevo.",
        _ => Generic
    };

    public static string ForModuleException(Exception exception)
    {
        if (exception is not PosApiException api) return ForException(exception);
        if (api.Code == "module_reactivation_failed" && !string.IsNullOrWhiteSpace(api.DiagnosticId))
            return $"El servidor no pudo reactivar el módulo. Diagnóstico {api.DiagnosticId}; revisa el registro del servidor.";
        if (api.Code == "module_reactivation_failed")
            return "El servidor no pudo reactivar el módulo. Revisa el registro del servidor e inténtalo de nuevo.";
        if (api.StatusCode == HttpStatusCode.InternalServerError && api.Code == "module_plan_failed" &&
            !string.IsNullOrWhiteSpace(api.DiagnosticId))
            return $"No se pudo validar el módulo. Busca {api.DiagnosticId} en %LOCALAPPDATA%\\PuntoDeVenta\\Logs\\errores-validacion-modulos.log.";
        if (api.StatusCode == System.Net.HttpStatusCode.NotFound)
            return "El servidor conectado no tiene actualizado el servicio de módulos. Cierra el POS anterior e inicia el servidor con el paquete actualizado.";
        if (api.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable &&
            api.Detail?.Contains("confianza de publicadores", StringComparison.OrdinalIgnoreCase) == true)
            return "El servidor activo no tiene cargada la clave pública de este módulo. Cierra las versiones anteriores del POS y abre el POS actualizado desde esta entrega.";
        if (!string.IsNullOrWhiteSpace(api.Detail))
            return $"El servidor rechazó la validación del módulo: {api.Detail}";
        return ForException(api);
    }

    private static string PaymentValidationMessage(ArgumentException exception)
    {
        var message = exception.Message;
        var suffix = $" (Parameter '{exception.ParamName}')";
        return message.EndsWith(suffix, StringComparison.Ordinal) ? message[..^suffix.Length] : message;
    }

    private static string ForApiError(PosApiException exception) => exception.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => "La sesión venció. Inicia sesión nuevamente.",
        System.Net.HttpStatusCode.Forbidden when exception.Code == "administrator_credentials_invalid" => "El usuario o PIN de administrador es incorrecto o no tiene acceso.",
        System.Net.HttpStatusCode.Forbidden when exception.Code == "admin_approval_cancelled" => "No se autorizó la operación.",
        System.Net.HttpStatusCode.Forbidden => "Tu usuario no tiene permiso para realizar esta operación.",
        System.Net.HttpStatusCode.NotFound when exception.Code == "sale_not_found" => "No se encontró la venta seleccionada.",
        System.Net.HttpStatusCode.NotFound => "No se encontró el producto o el servicio local no tiene esta función actualizada. Reinicia el POS e inténtalo de nuevo.",
        _ => ForApiErrorCode(exception.Code)
    };

    private static string ForApiErrorCode(string code) => code switch
    {
        "invalid_product_image" => "La foto no es válida. Usa JPG, PNG o WebP de hasta 5 MB.",
        "admin_pin_required" => "Esta acción requiere autorización de un administrador. Vuelve a intentarlo.",
        "exchange_rate_unavailable" or "exchange_rate_changed" => "No hay una tasa USD/VES vigente. Solicita actualizarla.",
        "insufficient_stock" => "No hay existencia suficiente para completar la venta.",
        "credit_limit_exceeded" => "La venta supera el límite de crédito del cliente.",
        "customer_has_balance" => "No se puede eliminar este cliente porque aún tiene saldo pendiente.",
        "credit_amount_below_minimum" => "El monto a crédito es inferior a USD 0,01 al cambio vigente.",
        "payment_below_minimum_usd" => "El abono es inferior a USD 0,01 al cambio vigente.",
        "credit_customer_required" => "Selecciona un cliente para registrar la venta a crédito.",
        "customer_unavailable" or "product_unavailable" => "El cliente o producto seleccionado ya no está disponible.",
        "invalid_product_price" => "El producto no tiene un precio válido.",
        "invalid_price_override" => "El precio especial o su motivo no son válidos. Corrige el artículo e inténtalo de nuevo.",
        "open_cash_session_required" => "Abre una caja antes de continuar.",
        "insufficient_cash" => "El monto supera el efectivo disponible en caja.",
        "currency_mismatch" => "El pago a crédito debe estar expresado en la moneda de la venta.",
        "customer_account_currency_invalid" => "La cuenta del cliente requiere actualización a USD antes de vender a crédito.",
        "unsupported_currency" => "Selecciona USD o VES para registrar el abono.",
    "payment_total_mismatch" => "Los pagos no coinciden con el total de la venta.",
        "sale_not_returnable" => "Esta venta ya no admite devoluciones.",
        "return_reason_required" => "Escribe el motivo de la devolución.",
        "invalid_return_lines" => "Revisa los artículos y cantidades seleccionados para devolver.",
        "insufficient_cash_for_refund" => "La caja abierta no tiene efectivo suficiente para este reintegro.",
        "refund_rounding_conflict" => "No se puede distribuir el reintegro entre los pagos originales por una diferencia de redondeo.",
        "refund_exceeds_original_payments" => "El importe supera los pagos originales pendientes de reintegro.",
        "stock_record_missing" => "No se encontró el registro de existencia del producto.",
        "credit_charge_missing" => "No se pudo localizar el cargo de crédito original del cliente.",
        "credit_exchange_rate_missing" => "No se encontró la tasa original del crédito para revertirlo correctamente.",
        "returning_quantity_exceeded" => "La cantidad supera lo pendiente de devolver.",
        "sku_already_exists" => "Ese código de producto ya está registrado.",
        "customer_document_already_exists" => "Ese documento ya está registrado.",
        "rate_already_exists_for_effective_time" => "Ya existe una tasa para esa fecha y hora.",
        "unauthorized" => "La sesión venció. Inicia sesión de nuevo.",
        _ => Generic
    };
}
