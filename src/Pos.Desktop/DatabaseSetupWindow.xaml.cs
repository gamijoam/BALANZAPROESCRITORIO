using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class DatabaseSetupWindow : Window
{
    public LocalDatabaseConfiguration? Configuration { get; private set; }

    public DatabaseSetupWindow(LocalDatabaseConfiguration? previous = null)
    {
        InitializeComponent();
        if (previous is null) return;
        HostBox.Text = previous.Host;
        PortBox.Text = previous.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        DatabaseBox.Text = previous.Database;
        UsernameBox.Text = previous.Username;
        PasswordBox.Password = previous.Password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (!int.TryParse(PortBox.Text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            ErrorText.Text = "El puerto debe ser un número entre 1 y 65535.";
            PortBox.Focus();
            return;
        }

        try
        {
            var configuration = new LocalDatabaseConfiguration(
                HostBox.Text.Trim(), port, DatabaseBox.Text.Trim(), UsernameBox.Text.Trim(), PasswordBox.Password);
            _ = configuration.ToConnectionString();
            Configuration = configuration;
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ErrorText.Text = exception.Message;
        }
    }
}
