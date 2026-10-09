using System.Globalization;
using System.Threading;
using System.Windows;
using Pos.Desktop.Presentation;

namespace Pos.Desktop;

public partial class App : Application
{
    private PosApiClient? _apiClient;

    protected override void OnStartup(StartupEventArgs e)
    {
        CultureInfo.DefaultThreadCurrentCulture = PosNumberFormat.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = PosNumberFormat.Culture;
        Thread.CurrentThread.CurrentCulture = PosNumberFormat.Culture;
        Thread.CurrentThread.CurrentUICulture = PosNumberFormat.Culture;
        base.OnStartup(e);
        _apiClient = new PosApiClient(new Uri("http://localhost:5155"));
        var viewModel = new PosViewModel(_apiClient);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _apiClient?.Dispose();
        base.OnExit(e);
    }
}
