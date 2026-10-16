using System.Windows;
using System.Net.Http;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class LoginWindow : Window
{
    private readonly PosViewModel _viewModel;
    private readonly Guid _terminalId;
    private bool _isSubmitting;

    public LoginWindow(PosViewModel viewModel, Guid terminalId, string? instruction = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _terminalId = terminalId;
        if (!string.IsNullOrWhiteSpace(instruction)) LoginInstructionText.Text = instruction;
        BaseAddressBox.Text = _viewModel.ApiBaseAddress.ToString().TrimEnd('/');
        Loaded += (_, _) => UsernameBox.Focus();
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
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await SubmitLoginAsync();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async Task SubmitLoginAsync()
    {
        if (_isSubmitting) return;
        ErrorLabel.Text = string.Empty;
        if (!Uri.TryCreate(BaseAddressBox.Text.Trim(), UriKind.Absolute, out var baseAddress) ||
            (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            ErrorLabel.Text = "Escribe una dirección HTTP o HTTPS válida.";
            ConnectionExpander.IsExpanded = true;
            BaseAddressBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(UsernameBox.Text) || string.IsNullOrWhiteSpace(PinBox.Password))
        {
            ErrorLabel.Text = "Completa el usuario y el PIN.";
            return;
        }

        _isSubmitting = true;
        LoginButton.IsEnabled = false;
        LoginButton.Content = "Validando…";
        try
        {
            _viewModel.SetBaseAddress(new Uri(baseAddress.ToString().TrimEnd('/') + "/"));
            await _viewModel.AuthenticateAsync(UsernameBox.Text, PinBox.Password, _terminalId);
            DialogResult = true;
        }
        catch (PosApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            if (_viewModel.IsAuthenticated)
            {
                ErrorLabel.Text = "El acceso fue aceptado, pero el servicio rechazó la sesión. Revisa el servicio e inténtalo de nuevo.";
            }
            else
            {
                ErrorLabel.Text = "Usuario o PIN incorrecto. Verifica ambos datos; 5 intentos fallidos bloquean el acceso por 5 minutos.";
                PinBox.Clear();
                PinBox.Focus();
            }
        }
        catch (HttpRequestException)
        {
            ErrorLabel.Text = "No se pudo conectar con el sistema. Revisa la dirección del servicio y que el servidor esté iniciado.";
            ConnectionExpander.IsExpanded = true;
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = $"No se pudo validar el acceso. {PosUserMessage.ForException(ex)}";
        }
        finally
        {
            _isSubmitting = false;
            LoginButton.IsEnabled = true;
            LoginButton.Content = "Iniciar sesión";
        }
    }
}
