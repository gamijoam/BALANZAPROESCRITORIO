using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Infrastructure.Licensing;

public interface IPosLicenseClockProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Best-effort persisted high-water mark for detecting local clock rollback.</summary>
public sealed class PosLicenseClockGuard
{
    private const int MaximumMarkerBytes = 4096;
    private readonly string _markerPath;
    private readonly IPosLicenseClockProtector _protector;
    private readonly TimeSpan _rollbackTolerance;
    private readonly TimeSpan _persistenceInterval;
    private readonly object _sync = new();
    private bool _initialized;
    private DateTimeOffset _highestObservedUtc;
    private DateTimeOffset _lastPersistedUtc;

    public PosLicenseClockGuard(string dataDirectory, IPosLicenseClockProtector protector,
        TimeSpan? rollbackTolerance = null, TimeSpan? persistenceInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(protector);
        if (!Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("La carpeta del marcador horario debe ser una ruta absoluta.", nameof(dataDirectory));

        _markerPath = Path.Combine(Path.GetFullPath(dataDirectory), "license-clock.dpapi");
        _protector = protector;
        _rollbackTolerance = rollbackTolerance ?? TimeSpan.FromMinutes(5);
        _persistenceInterval = persistenceInterval ?? TimeSpan.FromMinutes(5);
        if (_rollbackTolerance < TimeSpan.Zero || _persistenceInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rollbackTolerance), "Los intervalos del marcador horario no son válidos.");
    }

    public string? CheckAndRecord(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("La hora del marcador debe estar expresada en UTC.", nameof(nowUtc));

        lock (_sync)
        {
            try
            {
                Initialize(nowUtc);
                if (nowUtc < _highestObservedUtc.Subtract(_rollbackTolerance))
                    return "El reloj del servidor retrocedió más de cinco minutos desde la última hora registrada.";

                if (nowUtc > _highestObservedUtc)
                {
                    if (nowUtc - _lastPersistedUtc >= _persistenceInterval)
                    {
                        Persist(nowUtc);
                        _lastPersistedUtc = nowUtc;
                    }
                    _highestObservedUtc = nowUtc;
                }

                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or
                                              FormatException or OverflowException or ArgumentException or Win32Exception)
            {
                return $"No se pudo validar el marcador horario protegido: {exception.Message}";
            }
        }
    }

    private void Initialize(DateTimeOffset nowUtc)
    {
        if (_initialized) return;
        if (File.Exists(_markerPath))
        {
            var file = new FileInfo(_markerPath);
            if (file.Length is <= 0 or > MaximumMarkerBytes)
                throw new InvalidDataException("El marcador horario está vacío o supera el tamaño permitido.");
            var encrypted = File.ReadAllBytes(_markerPath);
            var plaintext = _protector.Unprotect(encrypted);
            if (plaintext.Length != sizeof(long))
                throw new InvalidDataException("El marcador horario tiene un formato inválido.");
            _highestObservedUtc = DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(plaintext));
            _lastPersistedUtc = _highestObservedUtc;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
            _highestObservedUtc = nowUtc;
            Persist(nowUtc);
            _lastPersistedUtc = nowUtc;
        }
        _initialized = true;
    }

    private void Persist(DateTimeOffset timestampUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
        var encrypted = _protector.Protect(BitConverter.GetBytes(timestampUtc.ToUnixTimeMilliseconds()));
        var temporaryPath = _markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                output.Write(encrypted);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

internal sealed class WindowsDpapiLicenseClockProtector : IPosLicenseClockProtector
{
    private const int CryptProtectLocalMachine = 0x4;
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BalanzaPro.POS-LICENSE-CLOCK.v1");

    public byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public byte[] Unprotect(byte[] protectedData) => Transform(protectedData, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        var inputBlob = Allocate(input);
        var entropyBlob = Allocate(Entropy);
        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, "BalanzaPro POS license clock", ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden | CryptProtectLocalMachine, out outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out outputBlob);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI rechazó el marcador horario.");

            var output = new byte[outputBlob.DataSize];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.Data);
            if (entropyBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.Data);
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { DataSize = bytes.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int DataSize;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
