using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pos.Modules.Packaging;

namespace Pos.Modules.Publisher;

public static class PublisherRunner
{
    private const long MaxFileBytes = 128 * 1024 * 1024;
    private const long MaxTotalBytes = 512 * 1024 * 1024;

    public static void Run(PublisherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Command is PublisherCommand.BuildUnsigned or PublisherCommand.SignPackage ||
            (options.Command == PublisherCommand.ExportPublicKey && options.PrivateKeyPemPath is not null))
        {
            RunPem(options);
            return;
        }
        using var certificate = FindCertificate(options.CertificateThumbprint, options.StoreLocation);
        Run(options, certificate);
    }

    internal static void Run(PublisherOptions options, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(certificate);
        var now = DateTime.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() < now)
            throw new InvalidOperationException("El certificado de publicación no está vigente.");

        if (options.Command == PublisherCommand.ExportPublicKey)
        {
            using var publicKey = certificate.GetRSAPublicKey()
                ?? throw new InvalidOperationException("El certificado no contiene una clave pública RSA.");
            if (publicKey.KeySize < 2048)
                throw new InvalidOperationException("La clave RSA debe tener al menos 2048 bits.");
            WriteAtomically(options.OutputPath, System.Text.Encoding.ASCII.GetBytes(publicKey.ExportSubjectPublicKeyInfoPem()));
            Console.WriteLine($"Clave pública SPKI exportada a: {Path.GetFullPath(options.OutputPath)}");
            return;
        }

        if (options.Command != PublisherCommand.Build)
            throw new InvalidOperationException("Este comando requiere una clave PEM o un certificado compatible.");

        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("El certificado encontrado no tiene una clave privada accesible para esta cuenta.");

        using var signingKey = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("La clave privada del certificado no es RSA o no se puede usar.");
        if (signingKey.KeySize < 2048)
            throw new InvalidOperationException("La clave RSA debe tener al menos 2048 bits.");

        var manifestPath = Path.GetFullPath(options.ManifestPath!);
        var stageRoot = Path.GetFullPath(options.StagingDirectory!);
        var filesListPath = Path.GetFullPath(options.FilesListPath!);
        var outputPath = Path.GetFullPath(options.OutputPath);
        EnsureDirectory(stageRoot, "directorio de preparación");
        EnsureOutsideRoot(outputPath, stageRoot, "La salida del paquete no puede guardarse dentro del directorio preparado.");

        var manifestJson = File.ReadAllText(manifestPath);
        var filePaths = PackageFileList.Parse(File.ReadAllText(filesListPath));
        var fileContents = ReadExplicitFiles(stageRoot, filePaths);
        var packageBytes = ModulePackageBuilder.Build(manifestJson, fileContents, options.KeyId!, signingKey);
        WriteAtomically(outputPath, packageBytes);
        Console.WriteLine($"Paquete firmado creado: {outputPath}");
        Console.WriteLine($"Archivos incluidos (sin contar manifiesto, índice ni firma): {fileContents.Count}");
    }

    private static void RunPem(PublisherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Command == PublisherCommand.BuildUnsigned)
        {
            var manifestPath = Path.GetFullPath(options.ManifestPath!);
            var stageRoot = Path.GetFullPath(options.StagingDirectory!);
            var filesListPath = Path.GetFullPath(options.FilesListPath!);
            var outputPath = Path.GetFullPath(options.OutputPath);
            EnsureDirectory(stageRoot, "directorio de preparación");
            EnsureOutsideRoot(outputPath, stageRoot, "La salida del paquete no puede guardarse dentro del directorio preparado.");
            var filePaths = PackageFileList.Parse(File.ReadAllText(filesListPath));
            var fileContents = ReadExplicitFiles(stageRoot, filePaths);
            var packageBytes = ModulePackageBuilder.BuildUnsigned(
                File.ReadAllText(manifestPath), fileContents, options.KeyId!);
            WriteAtomically(outputPath, packageBytes);
            Console.WriteLine($"Paquete sin firma preparado: {outputPath}");
            Console.WriteLine("El paquete no se puede instalar hasta pasar por el firmador autorizado.");
            return;
        }

        var privateKeyPemPath = Path.GetFullPath(options.PrivateKeyPemPath!);
        var privateKeyPem = File.ReadAllText(privateKeyPemPath);
        if (!privateKeyPem.Contains("PRIVATE KEY-----", StringComparison.Ordinal) ||
            privateKeyPem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
            throw new InvalidOperationException("Se requiere una clave privada RSA PEM sin contraseña, protegida por los permisos del sistema operativo.");

        using var signingKey = RSA.Create();
        try
        {
            signingKey.ImportFromPem(privateKeyPem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException("No se pudo leer la clave privada RSA PEM.", exception);
        }
        if (signingKey.KeySize < 2048)
            throw new InvalidOperationException("La clave RSA de publicación debe tener al menos 2048 bits.");

        if (options.Command == PublisherCommand.ExportPublicKey)
        {
            WriteAtomically(options.OutputPath, System.Text.Encoding.ASCII.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem()));
            Console.WriteLine($"Clave pública SPKI exportada a: {Path.GetFullPath(options.OutputPath)}");
            return;
        }

        if (options.Command != PublisherCommand.SignPackage)
            throw new InvalidOperationException("Comando PEM desconocido.");

        var inputPath = Path.GetFullPath(options.InputPath!);
        var inputInfo = new FileInfo(inputPath);
        if (!inputInfo.Exists)
            throw new FileNotFoundException("No se encontró el paquete que se debe firmar.", inputPath);
        if (inputInfo.Length > 640L * 1024 * 1024)
            throw new InvalidOperationException("El paquete sin firma supera el límite de 640 MiB.");

        var unsignedPackage = File.ReadAllBytes(inputPath);
        var signedPackage = ModulePackageSigner.SignUnsignedPackage(unsignedPackage, signingKey);
        WriteAtomically(options.OutputPath, signedPackage);
        Console.WriteLine($"Paquete firmado y validado: {Path.GetFullPath(options.OutputPath)}");
    }

    private static Dictionary<string, byte[]> ReadExplicitFiles(string stageRoot, IReadOnlyList<string> paths)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var packagePath in paths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(stageRoot, packagePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureInsideRoot(fullPath, stageRoot, $"La ruta '{packagePath}' sale del directorio de preparación.");
            EnsureNoReparsePoints(stageRoot, fullPath, packagePath);

            var info = new FileInfo(fullPath);
            if (!info.Exists)
                throw new FileNotFoundException($"No se encontró el archivo declarado: '{packagePath}'.", fullPath);
            if (info.Length > MaxFileBytes)
                throw new InvalidOperationException($"El archivo '{packagePath}' supera el límite de 128 MiB.");
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaxTotalBytes)
                throw new InvalidOperationException("La lista supera el límite total de 512 MiB.");
            files.Add(packagePath, File.ReadAllBytes(fullPath));
        }
        return files;
    }

    private static X509Certificate2 FindCertificate(string thumbprint, StoreLocation location)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        try
        {
            var now = DateTime.UtcNow;
            var valid = matches.Cast<X509Certificate2>()
                .Where(certificate => certificate.NotBefore.ToUniversalTime() <= now && certificate.NotAfter.ToUniversalTime() >= now)
                .ToArray();
            if (valid.Length != 1)
                throw new InvalidOperationException(valid.Length == 0
                    ? "No se encontró un certificado vigente con esa huella en el almacén seleccionado."
                    : "Hay más de un certificado vigente con esa huella; la publicación se canceló.");
            return new X509Certificate2(valid[0]);
        }
        finally
        {
            foreach (var match in matches.Cast<X509Certificate2>())
                match.Dispose();
        }
    }

    private static void EnsureNoReparsePoints(string root, string target, string packagePath)
    {
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        RejectReparsePoint(current, packagePath);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current, packagePath);
        }
    }

    private static void RejectReparsePoint(string path, string packagePath)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"La ruta '{packagePath}' atraviesa un enlace o punto de reanálisis; no se publicará.");
        }
    }

    private static void EnsureDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"No existe el {description}: '{path}'.");
        RejectReparseAncestors(path, $"El {description}");
    }

    private static void EnsureOutsideRoot(string path, string root, string message)
    {
        var relative = Path.GetRelativePath(root, path);
        if (!Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    private static void EnsureInsideRoot(string path, string root, string message)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    private static void WriteAtomically(string outputPath, byte[] contents)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("La salida no tiene un directorio válido.");
        Directory.CreateDirectory(directory);
        RejectReparseAncestors(directory, "El directorio de salida");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            CryptographicOperations.ZeroMemory(contents);
        }
    }

    private static void RejectReparseAncestors(string path, string description)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{description} no puede atravesar enlaces o puntos de reanálisis.");
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }
}
