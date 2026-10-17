using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pos.Modules.Packaging;

public sealed class PosUpdateArchiveException(string message) : Exception(message);

public static class PosUpdateArchiveVerifier
{
    private const string ManifestPath = "pos-update.manifest.json";
    private const string SignaturePath = "pos-update.manifest.sig";
    private const int MaximumEntries = 4098;
    private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;

    public static string VerifyAndExtract(string archivePath, string trustedPublicKeyPem, string destination,
        string? expectedVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPublicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (new FileInfo(archivePath).Length > MaximumArchiveBytes)
            throw new PosUpdateArchiveException("El paquete del POS supera el tamaño permitido.");

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntries)
            throw new PosUpdateArchiveException("El paquete del POS tiene demasiados archivos.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName;
            if (entry.Name.Length == 0)
            {
                if (!IsSafePath(path.TrimEnd('/'))) throw new PosUpdateArchiveException("El paquete contiene una carpeta insegura.");
                continue;
            }
            if (!IsSafePath(path) || !entries.TryAdd(path, entry))
                throw new PosUpdateArchiveException("El paquete contiene rutas duplicadas o inseguras.");
        }
        if (!entries.TryGetValue(ManifestPath, out var manifestEntry) ||
            !entries.TryGetValue(SignaturePath, out var signatureEntry))
            throw new PosUpdateArchiveException("Este ZIP no contiene la firma de actualización oficial.");

        var manifestBytes = ReadBounded(manifestEntry, 2 * 1024 * 1024);
        var signature = ReadBounded(signatureEntry, 16 * 1024);
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(trustedPublicKeyPem); }
        catch (CryptographicException) { throw new PosUpdateArchiveException("La clave pública de actualización no es válida."); }
        if (!rsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new PosUpdateArchiveException("La firma de la actualización del POS no es válida.");

        PosUpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PosUpdateManifest>(manifestBytes, JsonOptions); }
        catch (JsonException) { throw new PosUpdateArchiveException("El inventario firmado del POS no es válido."); }
        if (manifest is null || manifest.SchemaVersion != 1 || manifest.Product != "balanzapro-pos-win-x64" ||
            !Regex.IsMatch(manifest.Version ?? "", @"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant) ||
            (expectedVersion is not null && !string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal)))
            throw new PosUpdateArchiveException("La versión o el producto del paquete no coincide con la actualización solicitada.");
        if (manifest.Files is null || manifest.Files.Count != entries.Count - 2)
            throw new PosUpdateArchiveException("El inventario firmado no enumera todos los archivos del POS.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            if (!IsSafePath(file.Path ?? "") || !listed.Add(file.Path!) ||
                string.Equals(file.Path, ManifestPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.Path, SignaturePath, StringComparison.OrdinalIgnoreCase) ||
                !entries.TryGetValue(file.Path!, out var entry) ||
                !string.Equals(file.Path, entry.FullName, StringComparison.Ordinal) ||
                file.Size < 0 || file.Size > MaximumFileBytes || entry.Length != file.Size ||
                !Regex.IsMatch(file.Sha256 ?? "", "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new PosUpdateArchiveException("El inventario firmado contiene un archivo inválido.");
            totalBytes += file.Size;
            if (totalBytes > MaximumTotalBytes)
                throw new PosUpdateArchiveException("El paquete descomprimido supera el tamaño permitido.");
        }
        if (!entries.Keys.Where(path => path is not ManifestPath and not SignaturePath).All(listed.Contains))
            throw new PosUpdateArchiveException("El ZIP contiene archivos ajenos al inventario firmado.");
        foreach (var required in new[] { "server/Pos.Server.exe", "desktop/Pos.Desktop.exe",
                     "scripts/Install-LocalPos.ps1", "scripts/Install-DownloadedPosUpdate.ps1", "trusted-license-keys.json",
                     "trusted-publishers/pos-publisher-vps-2026.pem", "Actualizar POS.cmd", "pos-version.json" })
            if (!listed.Contains(required)) throw new PosUpdateArchiveException($"Falta un archivo obligatorio del POS: {required}.");

        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new PosUpdateArchiveException("La carpeta temporal debe estar vacía antes de preparar la actualización.");
        Directory.CreateDirectory(destination);
        foreach (var file in manifest.Files)
        {
            var entry = entries[file.Path!];
            var target = Path.GetFullPath(Path.Combine(destination, file.Path!.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new PosUpdateArchiveException("Un archivo del paquete sale de la carpeta temporal.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                copied += read;
                if (copied > file.Size) throw new PosUpdateArchiveException("Un archivo excede el tamaño firmado.");
                output.Write(buffer, 0, read);
                digest.AppendData(buffer, 0, read);
            }
            if (copied != file.Size || !string.Equals(Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), file.Sha256,
                    StringComparison.Ordinal))
                throw new PosUpdateArchiveException($"Un archivo del POS no coincide con la firma: {file.Path}.");
        }
        var versionFile = JsonSerializer.Deserialize<PosUpdateVersion>(File.ReadAllBytes(Path.Combine(destination, "pos-version.json")), JsonOptions);
        if (!string.Equals(versionFile?.Version, manifest.Version, StringComparison.Ordinal))
            throw new PosUpdateArchiveException("La versión interna del POS no coincide con el paquete firmado.");
        return manifest.Version!;
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length <= 0 || entry.Length > maximumBytes)
            throw new PosUpdateArchiveException("El inventario o la firma tiene un tamaño inválido.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        if (output.Length > maximumBytes) throw new PosUpdateArchiveException("El inventario o la firma supera el límite.");
        return output.ToArray();
    }

    private static bool IsSafePath(string path) => !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(part => part is not ("" or "." or ".."));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record PosUpdateManifest(int SchemaVersion, string? Product, string? Version, List<PosUpdateFile>? Files);
    private sealed record PosUpdateFile(string? Path, long Size, string? Sha256);
    private sealed record PosUpdateVersion(string? Version);
}
