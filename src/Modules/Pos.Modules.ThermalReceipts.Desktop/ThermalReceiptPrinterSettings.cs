using System.Printing;
using System.Text.Json;
using System.IO;

namespace Pos.Modules.ThermalReceipts.Desktop;

internal sealed record ThermalReceiptPrinterSettings(string? PrinterName, int Copies)
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PuntoDeVenta", "Configuracion", "thermal-receipts-printer.json");
    public static ThermalReceiptPrinterSettings Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<ThermalReceiptPrinterSettings>(File.ReadAllText(SettingsPath));
            return value is { Copies: >= 1 and <= 3 } ? value : new(null, 1);
        }
        catch { return new(null, 1); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static IReadOnlyList<string> InstalledPrinters()
    {
        using var server = new LocalPrintServer();
        return server.GetPrintQueues().Select(queue => queue.Name).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
