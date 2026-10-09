namespace Pos.Infrastructure.ProductImages;

public sealed class ProductImageStorage(string contentRoot)
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    private readonly string _directory = Path.Combine(Path.GetFullPath(contentRoot), "product-images");

    public async Task<string> SaveAsync(Guid productId, Stream source, long declaredLength, string contentType,
        CancellationToken cancellationToken = default)
    {
        if (productId == Guid.Empty) throw new ArgumentException("Product ID is required.", nameof(productId));
        if (declaredLength is <= 0 or > MaximumBytes) throw new InvalidDataException("La imagen debe pesar como máximo 5 MB.");
        var extension = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new InvalidDataException("Usa una imagen JPG, PNG o WebP.")
        };
        await using var buffer = new MemoryStream((int)declaredLength);
        await source.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != declaredLength || buffer.Length > MaximumBytes || !HasValidSignature(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), extension))
            throw new InvalidDataException("El archivo no contiene una imagen válida o está incompleto.");
        Directory.CreateDirectory(_directory);
        var name = $"{productId:N}-{Guid.NewGuid():N}{extension}";
        await File.WriteAllBytesAsync(GetAbsolutePath(name), buffer.ToArray(), cancellationToken);
        return name;
    }

    public string GetAbsolutePath(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid image file name.", nameof(fileName));
        return Path.Combine(_directory, fileName);
    }

    public void Delete(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        var path = GetAbsolutePath(fileName);
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool HasValidSignature(ReadOnlySpan<byte> bytes, string extension) => extension switch
    {
        ".png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        ".webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };
}
