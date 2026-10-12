using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class InstallationSetupWindow : Window
{
    private readonly string _configurationDirectory;

    public PosInstallationProfile? Profile { get; private set; }

    public InstallationSetupWindow(string configurationDirectory, PosInstallationMode? initialMode = null)
    {
        InitializeComponent();
        _configurationDirectory = configurationDirectory;
        if (initialMode == PosInstallationMode.ClientTerminal)
        {
            ClientOption.IsChecked = true;
            Mode_Changed(ClientOption, new RoutedEventArgs());
        }
        Loaded += (_, _) =>
        {
            if (initialMode == PosInstallationMode.ClientTerminal) ServerUrlBox.Focus();
            else StandaloneOption.Focus();
        };
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (ServerUrlBox is null || ErrorText is null) return;
        var clientMode = ClientOption.IsChecked == true;
        var serverMode = ServerOption.IsChecked == true;
        ServerAddressLabel.Text = clientMode ? "IP o nombre de la caja principal" : "Dirección de esta caja principal";
        ServerUrlBox.IsEnabled = clientMode;
        ServerUrlBox.IsReadOnly = serverMode;
        ConnectionHint.Text = clientMode
            ? "Las dos computadoras deben estar en la misma red local. Escribe la IP que aparece en la caja principal."
            : "El certificado HTTPS y el puerto 5155 se preparan automáticamente para esta red privada.";
        FirstTrustNotice.Visibility = serverMode ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = string.Empty;

        if (!serverMode) return;
        try
        {
            var address = LocalLanServerConfiguration.FindServerAddress();
            ServerUrlBox.Text = $"https://{address}:{LocalLanServerConfiguration.Port}";
        }
        catch (InvalidOperationException exception)
        {
            ServerUrlBox.Clear();
            ErrorText.Text = exception.Message;
        }
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!ContinueButton.IsEnabled) return;
        ErrorText.Text = string.Empty;
        if (StandaloneOption.IsChecked == true)
        {
            Profile = new PosInstallationProfile(PosInstallationMode.Standalone, "http://127.0.0.1:5155");
            DialogResult = true;
            return;
        }

        try
        {
            if (ServerOption.IsChecked == true)
            {
                var address = LocalLanServerConfiguration.FindServerAddress();
                var endpoint = new UriBuilder(Uri.UriSchemeHttps, address.ToString(), LocalLanServerConfiguration.Port).Uri;
                var certificate = LocalLanServerConfiguration.CreateServerCertificate(address, _configurationDirectory);
                LocalLanServerConfiguration.EnsurePrivateNetworkFirewallRule();
                Profile = new PosInstallationProfile(PosInstallationMode.Server, endpoint.ToString(),
                    ServerCertificatePath: certificate.CertificatePath,
                    ServerCertificatePassword: certificate.CertificatePassword,
                    ServerCertificateSha256: certificate.CertificateSha256);
                Profile.Validate();
                DialogResult = true;
                return;
            }

            var serverAddress = PosInstallationProfile.CreateClientEndpoint(ServerUrlBox.Text);
            ContinueButton.IsEnabled = false;
            ContinueButton.Content = "Comprobando servidor...";
            var fingerprint = await ReadFirstServerFingerprintAsync(serverAddress);
            if (!ServerCertificatePin.IsValid(fingerprint))
                throw new InvalidOperationException("El servidor no presentó un certificado HTTPS válido.");

            var displayedFingerprint = string.Join(' ', Enumerable.Range(0, fingerprint!.Length / 4)
                .Select(index => fingerprint.Substring(index * 4, 4)));
            var trust = MessageBox.Show(this,
                $"Vas a conectar esta caja con {serverAddress.Host}.\n\nHuella del servidor (SHA-256):\n{displayedFingerprint}\n\n¿Confías en esta caja principal?",
                "Confirmar servidor", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (trust != MessageBoxResult.Yes) return;

            Profile = new PosInstallationProfile(PosInstallationMode.ClientTerminal, serverAddress.ToString(),
                ServerCertificateSha256: fingerprint);
            Profile.Validate();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or CryptographicException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = exception.Message;
            if (ClientOption.IsChecked == true)
            {
                ServerUrlBox.Focus();
                ServerUrlBox.SelectAll();
            }
        }
        finally
        {
            ContinueButton.Content = "Continuar";
            ContinueButton.IsEnabled = true;
        }
    }

    private static async Task<string?> ReadFirstServerFingerprintAsync(Uri endpoint)
    {
        string? fingerprint = null;
        using var handler = new HttpClientHandler
        {
            // This one request carries no credentials. It obtains the certificate so the
            // operator can approve and pin it before any operator PIN is sent.
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null) return false;
                using var copy = new X509Certificate2(certificate);
                var now = DateTime.UtcNow;
                if ((errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
                    now < copy.NotBefore.ToUniversalTime() || now > copy.NotAfter.ToUniversalTime()) return false;
                fingerprint = ServerCertificatePin.ComputeSha256(copy);
                return true;
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var response = await client.GetAsync(new Uri(endpoint, "health/ready"));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("No se pudo conectar con el servidor o PostgreSQL no está listo. Revisa la IP y el puerto 5155.");
        using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (!document.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ready")
            throw new InvalidOperationException("El servidor respondió, pero todavía no está listo para recibir ventas.");
        return fingerprint;
    }
}
