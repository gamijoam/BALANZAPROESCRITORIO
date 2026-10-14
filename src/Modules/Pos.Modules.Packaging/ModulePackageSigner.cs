using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pos.Modules.Packaging;

/// <summary>Validates and signs an unsigned package without extracting its contents to disk.</summary>
public static class ModulePackageSigner
{
    private const int MaxEntryCount = 2048;
    private const long MaxEntryBytes = 128 * 1024 * 1024;
    private const long MaxTotalBytes = 512 * 1024 * 1024;
    private const long MaxIndexBytes = 1024 * 1024;
    private static readonly DateTimeOffset StableZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] SignUnsignedPackage(byte[] unsignedPackage, RSA signingKey)
    {
        ArgumentNullException.ThrowIfNull(unsignedPackage);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.KeySize < 2048)
            throw new InvalidOperationException("La clave RSA de publicación debe tener al menos 2048 bits.");
        byte[] privateKeyProbe;
        try { privateKeyProbe = signingKey.SignData(ReadOnlySpan<byte>.Empty, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("La clave PEM no permite firmar porque no contiene una clave privada RSA.");
        }
        CryptographicOperations.ZeroMemory(privateKeyProbe);

        var entries = ReadUnsignedEntries(unsignedPackage, out var indexBytes, out var signerKeyId);

        // Verify all paths, inventory hashes and manifest rules before a production signature is made.
        using var validationKey = RSA.Create(2048);
        var validationSignature = validationKey.SignData(indexBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var validationArchive = Rewrite(entries, validationSignature);
        using (var validationStream = new MemoryStream(validationArchive, writable: false))
        {
            ModulePackageVerifier.Verify(validationStream, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [signerKeyId] = validationKey.ExportSubjectPublicKeyInfoPem()
            });
        }

        var signature = signingKey.SignData(indexBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return Rewrite(entries, signature);
    }

    private static List<(string Path, byte[] Contents)> ReadUnsignedEntries(
        byte[] packageBytes,
        out byte[] indexBytes,
        out string signerKeyId)
    {
        try
        {
            using var input = new MemoryStream(packageBytes, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count is 0 or > MaxEntryCount)
                throw new ModulePackageVerificationException("El paquete debe contener una cantidad permitida de archivos.");

            var indexEntries = archive.Entries.Where(entry => entry.FullName.Equals(ModulePackageVerifier.IndexPath, StringComparison.Ordinal)).ToArray();
            if (indexEntries.Length != 1)
                throw new ModulePackageVerificationException("El paquete debe tener exactamente un índice con el nombre oficial.");
            if (archive.Entries.Any(entry => entry.FullName.Equals(ModulePackageVerifier.SignaturePath, StringComparison.OrdinalIgnoreCase)))
                throw new ModulePackageVerificationException("El paquete de entrada ya contiene una firma; se esperaba un paquete sin firmar.");

            var indexEntry = indexEntries[0];
            if (indexEntry.Length > MaxIndexBytes)
                throw new ModulePackageVerificationException("El índice supera el tamaño permitido.");
            using (var indexStream = indexEntry.Open())
            using (var indexContents = new MemoryStream())
            {
                indexStream.CopyTo(indexContents);
                indexBytes = indexContents.ToArray();
            }

            using var document = JsonDocument.Parse(indexBytes);
            if (!document.RootElement.TryGetProperty("signerKeyId", out var keyIdElement) ||
                keyIdElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(keyIdElement.GetString()))
                throw new ModulePackageVerificationException("El índice no contiene un identificador de clave válido.");
            signerKeyId = keyIdElement.GetString()!;

            var files = new List<(string Path, byte[] Contents)>(archive.Entries.Count - 1);
            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry == indexEntry)
                    continue;
                ModulePackageVerifier.ValidatePath(entry.FullName);
                if (entry.Length > MaxEntryBytes)
                    throw new ModulePackageVerificationException($"El archivo '{entry.FullName}' supera el tamaño permitido.");
                totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > MaxTotalBytes)
                    throw new ModulePackageVerificationException("El contenido del paquete supera el tamaño total permitido.");
                using var source = entry.Open();
                using var contents = new MemoryStream(checked((int)entry.Length));
                source.CopyTo(contents);
                files.Add((entry.FullName, contents.ToArray()));
            }

            files.Add((ModulePackageVerifier.IndexPath, indexBytes));
            return files;
        }
        catch (ModulePackageVerificationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or OverflowException or ArgumentException)
        {
            throw new ModulePackageVerificationException($"No se pudo leer el paquete sin firma: {exception.Message}");
        }
    }

    private static byte[] Rewrite(IReadOnlyCollection<(string Path, byte[] Contents)> entries, byte[] signature)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, contents) in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
                WriteEntry(archive, path, contents);
            WriteEntry(archive, ModulePackageVerifier.SignaturePath, Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)));
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = StableZipTimestamp;
        entry.ExternalAttributes = 0;
        using var output = entry.Open();
        output.Write(contents);
    }
}
