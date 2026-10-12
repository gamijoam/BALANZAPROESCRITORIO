using System.Net;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosNumberFormatTests
{
    [Fact]
    public void FormatsAndParsesAmountsWithVenezuelanSeparators()
    {
        Assert.Equal("1.000,60", PosNumberFormat.FormatAmount(1000.60m));
        Assert.True(PosNumberFormat.TryParseAmount("1.000,60", out var amount));
        Assert.Equal(1000.60m, amount);
    }

    [Fact]
    public void FormatsAmountsWithExactlyTwoDecimals()
    {
        Assert.Equal("1.000,00", PosNumberFormat.FormatAmount(1000m));
    }

    [Fact]
    public void ConvertsTechnicalApiErrorsToShortSpanishMessages()
    {
        Assert.Equal("No hay una tasa USD/VES vigente. Solicita actualizarla.",
            PosUserMessage.ForException(new PosApiException(HttpStatusCode.Conflict, "exchange_rate_unavailable")));
        Assert.Equal("No se pudo completar la operación. Revisa los datos e inténtalo de nuevo.",
            PosUserMessage.ForException(new InvalidOperationException("provider internals")));
    }

    [Fact]
    public void Shows_the_specific_checkout_validation_reason_instead_of_a_generic_data_error()
    {
        Assert.Equal("Selecciona un cliente antes de cobrar una parte a crédito.",
            PosUserMessage.ForException(new ArgumentException("Selecciona un cliente antes de cobrar una parte a crédito.", "payments")));
    }

    [Fact]
    public void Converts_product_image_errors_to_actionable_messages()
    {
        Assert.Equal("La foto no es válida. Usa JPG, PNG o WebP de hasta 5 MB.",
            PosUserMessage.ForException(new PosApiException(HttpStatusCode.BadRequest, "invalid_product_image")));
        Assert.Equal("Tu usuario no tiene permiso para realizar esta operación.",
            PosUserMessage.ForException(new PosApiException(HttpStatusCode.Forbidden, "forbidden")));
    }

    [Fact]
    public void Shows_module_plan_diagnostic_id_for_unexpected_server_failures()
    {
        var exception = new PosApiException(HttpStatusCode.InternalServerError, "module_plan_failed",
            "No se pudo validar el paquete.", "abc123");

        Assert.Equal("No se pudo validar el módulo. Busca abc123 en %LOCALAPPDATA%\\PuntoDeVenta\\Logs\\errores-validacion-modulos.log.",
            PosUserMessage.ForModuleException(exception));
    }

    [Fact]
    public void Shows_actionable_diagnostic_when_module_reactivation_fails_on_the_server()
    {
        var exception = new PosApiException(HttpStatusCode.InternalServerError, "module_reactivation_failed",
            "No se pudo reactivar el módulo.", "reactivate123");

        Assert.Equal("El servidor no pudo reactivar el módulo. Diagnóstico reactivate123; revisa el registro del servidor.",
            PosUserMessage.ForModuleException(exception));
    }
}
