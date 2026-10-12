using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pos.Desktop.Presentation;

public static class ServerCertificatePin
{
    public static string ComputeSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    public static bool IsValid(string? fingerprint)
        => fingerprint is { Length: 64 } && fingerprint.All(Uri.IsHexDigit);

    public static bool Matches(string? fingerprint, X509Certificate2? certificate)
    {
        if (!IsValid(fingerprint) || certificate is null) return false;
        var expected = Convert.FromHexString(fingerprint!);
        var actual = SHA256.HashData(certificate.RawData);
        try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    public static HttpClientHandler CreatePinnedHandler(string fingerprint)
    {
        if (!IsValid(fingerprint)) throw new ArgumentException("La huella de certificado no es válida.", nameof(fingerprint));
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => Matches(fingerprint, certificate)
        };
    }
}
