using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pos.Modules.Abstractions;

namespace Pos.Modules.Packaging;

public sealed class ModulePackageBuildException(string message) : Exception(message);

/// <summary>Creates v1 package archives. The caller owns and protects any signing key.</summary>
public static class ModulePackageBuilder
{
    private const int MaxEntryCount = 2048;
    private const long MaxIndexBytes = 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxFileBytes = 128 * 1024 * 1024;
    private const long MaxTotalBytes = 512 * 1024 * 1024;
    private static readonly Regex KeyIdPattern = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly DateTimeOffset StableZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds a ZIP in memory from explicit files. The input map must not contain the reserved manifest/index/signature names.
    /// Entry ordering and timestamps are stable. RSA-PSS signatures are intentionally randomized.
    /// </summary>
    public static byte[] Build(
        string moduleManifestJson,
        IReadOnlyDictionary<string, byte[]> moduleFiles,
        string signerKeyId,
        RSA signingKey)
    {
        ArgumentNullException.ThrowIfNull(moduleFiles);
        ArgumentNullException.ThrowIfNull(signingKey);
        return BuildCore(moduleManifestJson, moduleFiles, signerKeyId, signingKey);
    }

    /// <summary>
    /// Builds a package for a separate signing service. The resulting archive is not installable until
    /// <see cref="ModulePackageSigner.SignUnsignedPackage(byte[], RSA)"/> adds its signature.
    /// </summary>
    public static byte[] BuildUnsigned(
        string moduleManifestJson,
        IReadOnlyDictionary<string, byte[]> moduleFiles,
        string signerKeyId)
    {
        ArgumentNullException.ThrowIfNull(moduleFiles);
        return BuildCore(moduleManifestJson, moduleFiles, signerKeyId, null);
    }

    private static byte[] BuildCore(
        string moduleManifestJson,
        IReadOnlyDictionary<string, byte[]> moduleFiles,
        string signerKeyId,
        RSA? signingKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(moduleManifestJson))
                throw BuildFailure("El manifiesto del módulo está vacío.");
            if (moduleManifestJson.Length > MaxManifestBytes)
                throw BuildFailure("El manifiesto del módulo supera el tamaño permitido.");
            if (string.IsNullOrWhiteSpace(signerKeyId) || !KeyIdPattern.IsMatch(signerKeyId))
                throw BuildFailure("El identificador de clave firmante no es válido.");

            var manifestBytes = Encoding.UTF8.GetBytes(moduleManifestJson);
            if (manifestBytes.Length > MaxManifestBytes)
                throw BuildFailure("El manifiesto del módulo supera el tamaño permitido en UTF-8.");
            ModuleManifestReader.ParseAndValidate(moduleManifestJson);

            if (moduleFiles.Count + (signingKey is null ? 2 : 3) > MaxEntryCount)
                throw BuildFailure("El paquete excede el límite de archivos.");

            var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allFiles = new List<(string Path, byte[] Contents)>
            {
                (ModulePackageVerifier.ModuleManifestPath, manifestBytes)
            };
            long totalBytes = manifestBytes.Length;
            foreach (var (path, contents) in moduleFiles)
            {
                ModulePackageVerifier.ValidatePath(path);
                if (IsReservedPath(path))
                    throw BuildFailure($"La ruta '{path}' es reservada y no se acepta como archivo del módulo.");
                if (!normalizedPaths.Add(path))
                    throw BuildFailure($"La entrada '{path}' está duplicada o difiere solo en mayúsculas/minúsculas.");
                if (contents is null)
                    throw BuildFailure($"El contenido de '{path}' es nulo.");
                if (contents.LongLength > MaxFileBytes)
                    throw BuildFailure($"El archivo '{path}' supera el tamaño permitido.");
                totalBytes = checked(totalBytes + contents.LongLength);
                if (totalBytes > MaxTotalBytes)
                    throw BuildFailure("El contenido del módulo supera el tamaño total permitido.");
                allFiles.Add((path, contents));
            }

            if (signingKey is not null && signingKey.KeySize < 2048)
                throw BuildFailure("La clave de publicación debe ser una clave RSA de al menos 2048 bits.");

            var inventory = allFiles
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new PackageFileEntry(
                    file.Path,
                    Convert.ToHexString(SHA256.HashData(file.Contents)).ToLowerInvariant()))
                .ToArray();
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(
                new PackageIndex(1, signerKeyId, inventory), JsonOptions);
            if (indexBytes.LongLength > MaxIndexBytes)
                throw BuildFailure("El índice excede el tamaño permitido.");

            var signature = signingKey?.SignData(indexBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, ModulePackageVerifier.IndexPath, indexBytes);
                if (signature is not null)
                    WriteEntry(archive, ModulePackageVerifier.SignaturePath, Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)));
                foreach (var file in allFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
                    WriteEntry(archive, file.Path, file.Contents);
            }

            return output.ToArray();
        }
        catch (ModulePackageBuildException)
        {
            throw;
        }
        catch (ModuleManifestException exception)
        {
            throw BuildFailure($"El manifiesto del módulo no es válido: {exception.Message}");
        }
        catch (ModulePackageVerificationException exception)
        {
            throw BuildFailure(exception.Message);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or OverflowException or ArgumentException)
        {
            throw BuildFailure($"No se pudo construir el paquete: {exception.Message}");
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = StableZipTimestamp;
        entry.ExternalAttributes = 0;
        using var output = entry.Open();
        output.Write(contents);
    }

    private static bool IsReservedPath(string path) =>
        path.Equals(ModulePackageVerifier.IndexPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(ModulePackageVerifier.SignaturePath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(ModulePackageVerifier.ModuleManifestPath, StringComparison.OrdinalIgnoreCase);

    private static ModulePackageBuildException BuildFailure(string message) => new(message);

    private sealed record PackageIndex(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("signerKeyId")] string SignerKeyId,
        [property: JsonPropertyName("files")] IReadOnlyList<PackageFileEntry> Files);

    private sealed record PackageFileEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sha256")] string Sha256);
}
