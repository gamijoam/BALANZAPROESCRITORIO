using Pos.Desktop.Modules.Abstractions;

namespace Pos.Modules.ThermalReceipts.Desktop;

public sealed class ThermalReceiptsDesktopModule : IPosDesktopModule
{
    public string ModuleId => "thermal-receipts";
    public void Configure(PosDesktopModuleRegistry registry)
    {
        registry.RegisterMenu("thermal-receipts.configure", "Ticket térmico", "ThermalReceipts.Print",
            context => new ThermalReceiptSettingsWindow(context));
        registry.RegisterReceiptExtension("ThermalReceipts.Print", new ThermalReceiptExtension());
    }
}
