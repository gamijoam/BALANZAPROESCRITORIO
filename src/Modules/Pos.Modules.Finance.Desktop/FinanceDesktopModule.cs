using Pos.Desktop.Modules.Abstractions;

namespace Pos.Modules.Finance.Desktop;

public sealed class FinanceDesktopModule : IPosDesktopModule
{
    public string ModuleId => "finance";

    public void Configure(PosDesktopModuleRegistry registry)
    {
        registry.RegisterMenu("finance.main", "Compras y proveedores", "Supplier.View", context => new FinanceWindow(context));
        registry.RegisterMenu("finance.summary", "Resumen financiero", "Finance.Reports", context => new FinanceWindow(context, openSummary: true));
    }
}
