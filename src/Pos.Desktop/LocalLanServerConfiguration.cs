using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pos.Desktop;

public static class LocalLanServerConfiguration
{
    public const int Port = 5155;
    private const string FirewallRuleName = "Punto de Venta POS HTTPS 5155 (red privada)";

    public static IPAddress FindServerAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                              network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .SelectMany(network =>
            {
                var hasGateway = network.GetIPProperties().GatewayAddresses
                    .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(gateway.Address));
                return network.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address.Address))
                    .Select(address => (Address: address.Address, HasGateway: hasGateway));
            })
            .OrderByDescending(candidate => candidate.HasGateway)
            .Select(candidate => candidate.Address)
            .Distinct()
            .ToArray();

        return addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Conecta esta PC a la red local y vuelve a iniciar el POS para configurar las otras cajas.");
    }

    public static (string CertificatePath, string CertificatePassword, string CertificateSha256) CreateServerCertificate(IPAddress address, string configurationDirectory)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateIpv4(address))
            throw new ArgumentException("El certificado de red debe usar una dirección IPv4 privada.", nameof(address));

        Directory.CreateDirectory(configurationDirectory);
        var certificatePath = Path.Combine(configurationDirectory, "servidor-lan.pfx");
        var certificatePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={address}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(address);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var enhancedKeyUsage = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsage, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        var pfx = certificate.Export(X509ContentType.Pfx, certificatePassword);
        try { File.WriteAllBytes(certificatePath, pfx); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        return (certificatePath, certificatePassword, Pos.Desktop.Presentation.ServerCertificatePin.ComputeSha256(certificate));
    }

    public static void EnsurePrivateNetworkFirewallRule()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={Port} profile=private",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows no pudo solicitar permiso para habilitar el POS en redes privadas.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Windows no pudo habilitar el puerto 5155. Acepta el permiso de administrador y vuelve a intentarlo.");
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
