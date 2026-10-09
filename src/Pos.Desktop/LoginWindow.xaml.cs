using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class LoginWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly Guid _terminalId;

    public LoginWindow(PosViewModel viewModel, Guid terminalId)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _terminalId = terminalId;
        BaseAddressBox.Text = Environment.GetEnvironmentVariable("POS_API_URL") ?? "http://localhost:5155";
        UsernameBox.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            PinBox.Focus();
        };
        PinBox.KeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            await SubmitLoginAsync();
        };
        UsernameBox.Focus();
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await SubmitLoginAsync();

    private async Task SubmitLoginAsync()
    {
        ErrorLabel.Text = string.Empty;
        if (!Uri.TryCreate(BaseAddressBox.Text.Trim(), UriKind.Absolute, out var baseAddress) ||
            (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            ErrorLabel.Text = "Escribe una dirección HTTP o HTTPS válida.";
            return;
        }
        if (string.IsNullOrWhiteSpace(UsernameBox.Text) || string.IsNullOrWhiteSpace(PinBox.Password))
        {
            ErrorLabel.Text = "Completa el usuario y el PIN.";
            return;
        }

        try
        {
            _viewModel.SetBaseAddress(new Uri(baseAddress.ToString().TrimEnd('/') + "/"));
            await _viewModel.LoginAsync(UsernameBox.Text, PinBox.Password, _terminalId);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = PosUserMessage.ForException(ex);
        }
    }
}
