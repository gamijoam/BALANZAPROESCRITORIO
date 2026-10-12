using System.Security.Cryptography;

namespace Pos.Modules.Packaging;

public sealed class ModulePackageStoreException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record StoredModulePackage(
    string ModuleId,
    Version Version,
    string PackagePath,
    string Sha256,
    bool AlreadyStored);

/// <summary>
/// Stores the original verified archive in an immutable, versioned folder. It deliberately
/// does not extract or load package contents; both require later installation gates.
/// </summary>
public static class ModulePackageStore
{
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(100);

    public static async Task<StoredModulePackage> StoreAsync(
        Stream package,
        string storageRoot,
        ModuleTrustKeyRing trustKeyRing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(trustKeyRing);
        if (!package.CanRead)
            throw new ArgumentException("El paquete debe ser legible.", nameof(package));
        if (string.IsNullOrWhiteSpace(storageRoot) || !Path.IsPathFullyQualified(storageRoot))
            throw new ModulePackageStoreException("module_store_path_invalid", "La carpeta de módulos debe ser una ruta absoluta.");

        var root = Path.GetFullPath(storageRoot);
        EnsureSafeDirectoryPath(root);
        await using var installationLock = await AcquireInstallationLockAsync(root, cancellationToken);

        var stagingDirectory = Path.Combine(root, $".staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            EnsureNotReparsePoint(stagingDirectory);
            var stagedPackagePath = Path.Combine(stagingDirectory, "package.zip");
            await CopyPackageAsync(package, stagedPackagePath, cancellationToken);

            VerifiedModulePackage verified;
            await using (var stagedPackage = new FileStream(stagedPackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                verified = ModulePackageVerifier.Verify(stagedPackage, trustKeyRing);

            var hash = await HashFileAsync(stagedPackagePath, cancellationToken);
            var moduleDirectory = Path.Combine(root, verified.Module.Id);
            EnsureSafeDirectoryPath(moduleDirectory);
            var versionDirectory = Path.Combine(moduleDirectory, verified.Module.Version.ToString(3));
            var finalPackagePath = Path.Combine(versionDirectory, "package.zip");

            if (Directory.Exists(versionDirectory))
            {
                EnsureNotReparsePoint(versionDirectory);
                if (!File.Exists(finalPackagePath))
                    throw VersionConflict(verified.Module.Id, verified.Module.Version);
                EnsureNotReparsePoint(finalPackagePath);
                var existingHash = await HashFileAsync(finalPackagePath, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(existingHash)))
                    throw VersionConflict(verified.Module.Id, verified.Module.Version);

                return new StoredModulePackage(verified.Module.Id, verified.Module.Version, finalPackagePath, hash, AlreadyStored: true);
            }

            Directory.Move(stagingDirectory, versionDirectory);
            return new StoredModulePackage(verified.Module.Id, verified.Module.Version, finalPackagePath, hash, AlreadyStored: false);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static async Task CopyPackageAsync(Stream source, string destinationPath, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total = checked(total + read);
            if (total > MaxPackageBytes)
                throw new ModulePackageStoreException("module_package_too_large", "El paquete supera el límite de 512 MiB.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0)
            throw new ModulePackageStoreException("module_package_empty", "El paquete está vacío.");
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<FileStream> AcquireInstallationLockAsync(string root, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(root, ".installation.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(lockPath)) EnsureNotReparsePoint(lockPath);
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                EnsureNotReparsePoint(lockPath);
                return stream;
            }
            catch (IOException)
            {
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }
    }

    private static void EnsureSafeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
            throw new ModulePackageStoreException("module_store_path_invalid", "La ruta de almacenamiento no tiene una raíz válida.");

        var current = pathRoot;
        EnsureDirectoryExistsWithoutReparse(current);
        var segments = fullPath[pathRoot.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            EnsureDirectoryExistsWithoutReparse(current);
        }
    }

    private static void EnsureDirectoryExistsWithoutReparse(string path)
    {
        if (File.Exists(path))
            throw new ModulePackageStoreException("module_store_path_invalid", "Una parte de la ruta de almacenamiento es un archivo.");
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        EnsureNotReparsePoint(path);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new ModulePackageStoreException("module_store_reparse_point", "El almacén contiene un enlace o punto de reanálisis y no se usará.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static ModulePackageStoreException VersionConflict(string moduleId, Version version) =>
        new("module_version_conflict", $"Ya existe un paquete distinto para '{moduleId}' versión {version.ToString(3)}.");
}
