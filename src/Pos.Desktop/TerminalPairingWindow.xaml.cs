using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class TerminalPairingWindow : Window
{
    private readonly Uri _serverAddress;

    public string? TerminalCredential { get; private set; }

    public TerminalPairingWindow(Uri serverAddress)
    {
        InitializeComponent();
        _serverAddress = serverAddress;
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (!PairButton.IsEnabled) return;
        ErrorText.Text = string.Empty;
        var username = UsernameBox.Text.Trim();
        var pin = PinBox.Password;
        if (username.Length == 0 || pin.Length == 0)
        {
            ErrorText.Text = "Ingresa el usuario y el PIN de administrador.";
            return;
        }

        PairButton.IsEnabled = false;
        PairButton.Content = "Autorizando...";
        try
        {
            TerminalCredential = await PosApiClient.PairTerminalAsync(_serverAddress,
                TerminalIdentity.GetOrCreate(), username, pin);
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or CryptographicException)
        {
            ErrorText.Text = exception.Message;
            PinBox.Clear();
            PinBox.Focus();
        }
        finally
        {
            PairButton.Content = "Autorizar caja";
            PairButton.IsEnabled = true;
        }
    }
}
