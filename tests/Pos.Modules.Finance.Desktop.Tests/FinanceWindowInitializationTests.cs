using System.Windows;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using Pos.Modules.Finance.Desktop;

namespace Pos.Modules.Finance.Desktop.Tests;

public sealed class FinanceWindowInitializationTests
{
    [Fact]
    public void Finance_window_can_be_constructed_before_loading_api_data()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var context = new PosDesktopModuleContext(null!, Guid.NewGuid(),
                    new HashSet<string>(StringComparer.Ordinal), "finance",
                    (_, _) => Task.FromResult<IReadOnlyList<ProductInfo>>(Array.Empty<ProductInfo>()));
                var window = new FinanceWindow(context);
                Assert.Equal("Compras, proveedores y cuentas por pagar", window.Title);
                window.Close();
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(failure is null, failure?.ToString());
    }
}
