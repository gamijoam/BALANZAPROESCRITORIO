using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pos.Application.Licensing;

namespace Pos.Infrastructure.Licensing;

/// <summary>Renews the signed offline license in the background; network errors never block POS requests.</summary>
public sealed class PosLicenseRenewalWorker(
    IHttpClientFactory httpClientFactory,
    PosLicenseManager licenseManager,
    IConfiguration configuration,
    ILogger<PosLicenseRenewalWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromHours(4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpointText = configuration["Licensing:RenewalEndpoint"];
        if (string.IsNullOrWhiteSpace(endpointText))
        {
            logger.LogInformation("La renovación automática de licencias está desactivada: no se configuró Licensing:RenewalEndpoint.");
            return;
        }

        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            logger.LogError("Licensing:RenewalEndpoint debe ser una URL HTTPS absoluta, sin credenciales ni fragmento.");
            return;
        }

        var client = httpClientFactory.CreateClient(nameof(PosLicenseRenewalWorker));
        client.Timeout = TimeSpan.FromSeconds(20);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = RenewalInterval;
            try
            {
                var renewal = licenseManager.CreateRenewalRequest(DateTimeOffset.UtcNow);
                if (renewal is not null)
                {
                    using var response = await client.PostAsJsonAsync(endpoint, renewal, stoppingToken);
                    if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        logger.LogInformation("El VPS aún no tiene una renovación pagada para esta licencia.");
                    }
                    else if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<RenewalResponse>(cancellationToken: stoppingToken);
                        if (result?.DocumentBase64 is null || result.DocumentBase64.Length > 96 * 1024)
                            throw new InvalidDataException("El VPS devolvió un documento de licencia vacío o demasiado grande.");

                        byte[] document;
                        try
                        {
                            document = Convert.FromBase64String(result.DocumentBase64);
                        }
                        catch (FormatException exception)
                        {
                            throw new InvalidDataException("El VPS devolvió un documento de licencia mal codificado.", exception);
                        }

                        if (document.Length is 0 or > 64 * 1024)
                            throw new InvalidDataException("El VPS devolvió un documento de licencia vacío o demasiado grande.");

                        var status = licenseManager.Install(document, DateTimeOffset.UtcNow);
                        if (status.State is not ("Active" or "Grace"))
                            throw new InvalidDataException("La licencia renovada no quedó activa después de verificar su firma.");

                        logger.LogInformation("Licencia POS renovada automáticamente hasta {ExpiresAtUtc}.", status.ExpiresAtUtc);
                    }
                    else
                    {
                        logger.LogWarning("El VPS de licencias respondió con HTTP {StatusCode}; se reintentará en {RetryMinutes} minutos.",
                            (int)response.StatusCode, (int)RetryDelay.TotalMinutes);
                        delay = RetryDelay;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or
                                              InvalidDataException or PosLicenseDocumentException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "No se pudo renovar la licencia del POS; la licencia local se conserva y se reintentará en {RetryMinutes} minutos.",
                    (int)RetryDelay.TotalMinutes);
                delay = RetryDelay;
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private sealed record RenewalResponse([property: JsonPropertyName("documentBase64")] string? DocumentBase64);
}
