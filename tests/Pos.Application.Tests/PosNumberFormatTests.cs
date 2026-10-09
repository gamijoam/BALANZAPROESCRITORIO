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
        Assert.Equal("No hay una tasa de cambio vigente. Solicita actualizarla.",
            PosUserMessage.ForException(new PosApiException(HttpStatusCode.Conflict, "exchange_rate_unavailable")));
        Assert.Equal("No se pudo completar la operación. Revisa los datos e inténtalo de nuevo.",
            PosUserMessage.ForException(new InvalidOperationException("provider internals")));
    }
}
