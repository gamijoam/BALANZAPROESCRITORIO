using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pos.Modules.Abstractions;

namespace Pos.Modules.Packaging;

public sealed class ModulePackageVerificationException(string message) : Exception(message);

public sealed record VerifiedPackageFile(string Path, string Sha256, long Length);

public sealed record VerifiedModulePackage(
    ModuleManifest Module,
    string SignerKeyId,
    IReadOnlyList<VerifiedPackageFile> Files);

/// <summary>
/// Verifies a signed POS module archive without extracting files or loading assemblies.
/// The caller supplies trusted public keys from configuration outside the package.
/// </summary>
public static class ModulePackageVerifier
{
    public const string IndexPath = "package.index.json";
    public const string SignaturePath = "package.signature";
    public const string ModuleManifestPath = "module.manifest.json";

    private const int MaxEntryCount = 2048;
    private const long MaxIndexBytes = 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const long MaxSignatureBytes = 16 * 1024;
    private const long MaxFileBytes = 128 * 1024 * 1024;
    private const long MaxTotalBytes = 512 * 1024 * 1024;
    private const long MaxCompressionRatio = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly Regex KeyIdPattern = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex PackagePathPattern = new("^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);

    public static VerifiedModulePackage Verify(Stream package, IReadOnlyDictionary<string, string> trustedPublicKeysPem)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(trustedPublicKeysPem);
        if (!package.CanRead || !package.CanSeek)
            throw new ArgumentException("El paquete debe ser un flujo legible y reposicionable.", nameof(package));

        var originalPosition = package.Position;
        try
        {
            package.Position = 0;
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is 0 or > MaxEntryCount)
                throw Failure($"El paquete debe contener entre 1 y {MaxEntryCount} archivos.");

            var entriesByPath = ValidateArchiveEntries(archive.Entries);
            var indexEntry = GetRequiredEntry(entriesByPath, IndexPath);
            var signatureEntry = GetRequiredEntry(entriesByPath, SignaturePath);
            var indexBytes = ReadSmallEntry(indexEntry, MaxIndexBytes, "índice");
            var signatureText = System.Text.Encoding.ASCII.GetString(ReadSmallEntry(signatureEntry, MaxSignatureBytes, "firma"));

            var index = DeserializeIndex(indexBytes);
            if (index.FormatVersion != 1)
                throw Failure($"La versión de índice {index.FormatVersion} no es compatible.");
            if (string.IsNullOrWhiteSpace(index.SignerKeyId) || !KeyIdPattern.IsMatch(index.SignerKeyId))
                throw Failure("El identificador de la clave firmante no es válido.");

            if (!trustedPublicKeysPem.TryGetValue(index.SignerKeyId, out var publicKeyPem) || string.IsNullOrWhiteSpace(publicKeyPem))
                throw Failure($"La clave firmante '{index.SignerKeyId}' no es de confianza en este servidor.");

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signatureText.Trim());
            }
            catch (FormatException)
            {
                throw Failure("La firma del paquete no tiene formato Base64 válido.");
            }

            using (var rsa = RSA.Create())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(publicKeyPem) ||
                        !publicKeyPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
                        throw Failure($"La clave '{index.SignerKeyId}' debe estar en formato público SubjectPublicKeyInfo.");
                    rsa.ImportFromPem(publicKeyPem);
                }
                catch (ModulePackageVerificationException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is ArgumentException or CryptographicException)
                {
                    throw Failure($"La clave pública '{index.SignerKeyId}' no se pudo leer.");
                }

                if (rsa.KeySize < 2048 || !rsa.VerifyData(indexBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw Failure("La firma del índice no es válida para una clave oficial confiable.");
            }

            var expectedFiles = ValidateInventory(index.Files);
            var actualFileEntries = entriesByPath.Keys
                .Where(path => !IsReservedPath(path))
                .ToHashSet(StringComparer.Ordinal);
            if (!expectedFiles.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actualFileEntries))
                throw Failure("El contenido del ZIP no coincide exactamente con el inventario firmado.");

            if (!expectedFiles.Keys.Contains(ModuleManifestPath, StringComparer.Ordinal))
                throw Failure($"El paquete no incluye '{ModuleManifestPath}'.");

            long totalBytes = 0;
            var verifiedFiles = new List<VerifiedPackageFile>(expectedFiles.Count);
            byte[]? manifestBytes = null;
            foreach (var (path, expectedHash) in expectedFiles.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var entry = entriesByPath[path];
                var (actualHash, length, contents) = HashEntry(entry, path);
                totalBytes = checked(totalBytes + length);
                if (totalBytes > MaxTotalBytes)
                    throw Failure("El contenido descomprimido supera el límite permitido para un paquete.");

                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(expectedHash)))
                    throw Failure($"El hash SHA-256 no coincide para '{path}'.");

                if (path.Equals(ModuleManifestPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (length > MaxManifestBytes)
                        throw Failure("El manifiesto del módulo supera el tamaño permitido.");
                    manifestBytes = contents;
                }

                verifiedFiles.Add(new VerifiedPackageFile(path, actualHash, length));
            }

            ModuleManifest module;
            try
            {
                module = ModuleManifestReader.ParseAndValidate(System.Text.Encoding.UTF8.GetString(manifestBytes!));
            }
            catch (ModuleManifestException exception)
            {
                throw Failure($"El manifiesto del módulo no es válido: {exception.Message}");
            }

            if (module.EntryAssembly is not null && !verifiedFiles.Any(file => string.Equals(file.Path, module.EntryAssembly, StringComparison.Ordinal)))
                throw Failure($"El ensamblado de entrada '{module.EntryAssembly}' no está incluido en el inventario firmado.");
            if (module.DesktopEntryAssembly is not null && !verifiedFiles.Any(file => string.Equals(file.Path, module.DesktopEntryAssembly, StringComparison.Ordinal)))
                throw Failure($"El ensamblado de escritorio '{module.DesktopEntryAssembly}' no está incluido en el inventario firmado.");

            return new VerifiedModulePackage(module, index.SignerKeyId, verifiedFiles);
        }
        catch (ModulePackageVerificationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or OverflowException)
        {
            throw Failure($"No se pudo verificar el paquete: {exception.Message}");
        }
        finally
        {
            package.Position = originalPosition;
        }
    }

    public static VerifiedModulePackage Verify(Stream package, ModuleTrustKeyRing trustKeyRing)
    {
        ArgumentNullException.ThrowIfNull(trustKeyRing);
        return Verify(package, trustKeyRing.VerifierPublicKeys);
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateArchiveEntries(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ValidatePath(entry.FullName);
            if (!result.TryAdd(entry.FullName, entry))
                throw Failure($"El ZIP contiene rutas repetidas o ambiguas: '{entry.FullName}'.");

            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType is not 0 and not 0x8000)
                throw Failure($"El paquete contiene un tipo de archivo no permitido: '{entry.FullName}'.");
        }
        return result;
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            !PackagePathPattern.IsMatch(path) || path.EndsWith('/'))
            throw Failure($"La ruta '{path}' no es una ruta de archivo de paquete permitida.");

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                                    segment.EndsWith('.') || segment.EndsWith(' ') || IsWindowsDeviceName(segment)))
            throw Failure($"La ruta '{path}' contiene segmentos no permitidos.");
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static ZipArchiveEntry GetRequiredEntry(Dictionary<string, ZipArchiveEntry> entries, string path)
    {
        if (!entries.TryGetValue(path, out var entry))
            throw Failure($"Falta el archivo obligatorio '{path}'.");
        if (!entry.FullName.Equals(path, StringComparison.Ordinal))
            throw Failure($"El archivo reservado debe llamarse exactamente '{path}'.");
        return entry;
    }

    private static byte[] ReadSmallEntry(ZipArchiveEntry entry, long limit, string description)
    {
        if (entry.Length > limit)
            throw Failure($"El {description} supera el tamaño permitido.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > limit)
                throw Failure($"El {description} supera el tamaño permitido al descomprimirse.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != entry.Length)
            throw Failure($"El tamaño real del {description} no coincide con el declarado en el ZIP.");
        return output.ToArray();
    }

    private static PackageIndex DeserializeIndex(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            ValidateIndexJson(document.RootElement);
            return JsonSerializer.Deserialize<PackageIndex>(bytes, JsonOptions)
                ?? throw Failure("El índice del paquete está vacío.");
        }
        catch (JsonException exception)
        {
            throw Failure($"El índice del paquete no es JSON válido: {exception.Message}");
        }
    }

    private static void ValidateIndexJson(JsonElement root)
    {
        EnsureExactProperties(root, ["formatVersion", "signerKeyId", "files"], "índice");
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array)
            throw Failure("La propiedad files del índice debe ser una lista.");
        foreach (var file in files.EnumerateArray())
            EnsureExactProperties(file, ["path", "sha256"], "entrada del inventario");
    }

    private static void EnsureExactProperties(JsonElement element, string[] expected, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Failure($"La {description} debe ser un objeto JSON.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw Failure($"La {description} contiene la propiedad repetida '{property.Name}'.");
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                throw Failure($"La {description} contiene una propiedad desconocida '{property.Name}'.");
        }
        if (expected.Any(name => !seen.Contains(name)))
            throw Failure($"La {description} no contiene todas las propiedades obligatorias.");
    }

    private static Dictionary<string, string> ValidateInventory(IReadOnlyList<PackageFileEntry>? files)
    {
        if (files is null || files.Count == 0 || files.Count > MaxEntryCount - 2)
            throw Failure("El inventario firmado está vacío o excede el límite de archivos.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null)
                throw Failure("El inventario contiene una entrada vacía.");
            var path = file.Path;
            var hash = file.Sha256;
            if (path is null || hash is null)
                throw Failure("El inventario contiene una ruta o un hash vacío.");
            ValidatePath(path);
            if (IsReservedPath(path))
                throw Failure($"El inventario no debe declarar el archivo reservado '{path}'.");
            if (!Sha256Pattern.IsMatch(hash))
                throw Failure($"El hash declarado para '{path}' no es SHA-256 hexadecimal.");
            if (!result.TryAdd(path, hash))
                throw Failure($"El inventario contiene la ruta repetida '{path}'.");
        }
        return result;
    }

    private static (string Hash, long Length, byte[]? Contents) HashEntry(ZipArchiveEntry entry, string path)
    {
        if (entry.Length > MaxFileBytes ||
            (path.Equals(ModuleManifestPath, StringComparison.OrdinalIgnoreCase) && entry.Length > MaxManifestBytes) ||
            (entry.Length > 1024 * 1024 &&
             (entry.CompressedLength <= 0 || entry.Length > checked(entry.CompressedLength * MaxCompressionRatio))))
            throw Failure($"El archivo '{path}' supera los límites de tamaño o compresión.");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var input = entry.Open();
        using var contents = path.Equals(ModuleManifestPath, StringComparison.OrdinalIgnoreCase) ? new MemoryStream() : null;
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += read;
            if (total > MaxFileBytes)
                throw Failure($"El archivo '{path}' excede el tamaño permitido al descomprimirse.");
            sha.AppendData(buffer, 0, read);
            contents?.Write(buffer, 0, read);
        }
        if (total != entry.Length)
            throw Failure($"El tamaño real de '{path}' no coincide con el declarado en el ZIP.");

        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), total, contents?.ToArray());
    }

    private static bool IsReservedPath(string path) =>
        path.Equals(IndexPath, StringComparison.OrdinalIgnoreCase) || path.Equals(SignaturePath, StringComparison.OrdinalIgnoreCase);

    private static ModulePackageVerificationException Failure(string message) => new(message);

    private sealed record PackageIndex(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("signerKeyId")] string SignerKeyId,
        [property: JsonPropertyName("files")] IReadOnlyList<PackageFileEntry>? Files);

    private sealed record PackageFileEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sha256")] string Sha256);
}
