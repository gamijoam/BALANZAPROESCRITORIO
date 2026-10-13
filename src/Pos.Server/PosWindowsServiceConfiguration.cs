using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace Pos.Server;

internal static class PosWindowsServiceConfiguration
{
    public const string ServiceName = "BalanzaProPosServer";
    private const int UiForbidden = 0x1;

    public static void ApplyFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("POS_SERVICE_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("La configuración del servicio POS requiere Windows.");
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("POS_SERVICE_CONFIG_PATH debe ser una ruta absoluta.");

        byte[]? clear = null;
        try
        {
            clear = Transform(File.ReadAllBytes(path), protect: false);
            using var document = JsonDocument.Parse(clear);
            var root = document.RootElement;
            Set("ConnectionStrings__Pos", ReadRequired(root, "connectionString"));
            Set("ASPNETCORE_URLS", ReadRequired(root, "urls"));
            Set("PosSecurity__RequireTerminalPairing", "false");
            SetOptional(root, "certificatePath", "ASPNETCORE_Kestrel__Certificates__Default__Path");
            SetOptional(root, "certificatePassword", "ASPNETCORE_Kestrel__Certificates__Default__Password");
            SetOptional(root, "moduleTrustKeyRingPath", "ModuleTrust__KeyRingPath");
            SetOptional(root, "moduleStoragePath", "Modules__StoragePath");
            SetOptional(root, "moduleBackupPath", "Modules__BackupPath");
            SetOptional(root, "postgresqlBinaryDirectory", "PostgreSql__BinaryDirectory");
            SetOptional(root, "logDirectory", "POS_MODULE_LOG_DIRECTORY");
        }
        catch (Exception exception) when (exception is IOException or JsonException or Win32Exception or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("No se pudo leer la configuración protegida del servicio POS. Vuelve a ejecutar Administrar servicio POS.cmd como administrador.", exception);
        }
        finally
        {
            if (clear is not null) Array.Clear(clear);
        }
    }

    private static string ReadRequired(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"La configuración del servicio no contiene '{name}'.");

    private static void SetOptional(JsonElement root, string property, string environmentVariable)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            Set(environmentVariable, value.GetString()!);
    }

    private static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var outputBlob = default(DataBlob);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob { Length = input.Length, Data = inputPointer };
            var success = protect
                ? CryptProtectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden | 0x4, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
            if (outputBlob.Data != IntPtr.Zero) _ = LocalFree(outputBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
