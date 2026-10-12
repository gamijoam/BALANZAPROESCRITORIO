using Pos.Infrastructure.ProductImages;

namespace Pos.Integration.Tests;

public sealed class ProductImageStorageTests
{
    [Fact]
    public async Task Saves_png_with_product_scoped_name_and_rejects_non_image_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "pos-image-tests", Guid.NewGuid().ToString("N"));
        var store = new ProductImageStorage(root);
        var productId = Guid.NewGuid();
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        try
        {
            await using var stream = new MemoryStream(png);
            var name = await store.SaveAsync(productId, stream, png.Length, "image/png");
            Assert.StartsWith($"{productId:N}-", name);
            Assert.EndsWith(".png", name);
            Assert.Equal(png, await File.ReadAllBytesAsync(store.GetAbsolutePath(name)));

            await using var invalid = new MemoryStream([1, 2, 3, 4]);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Guid.NewGuid(), invalid, 4, "image/png"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_oversized_upload_before_writing_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "pos-image-tests", Guid.NewGuid().ToString("N"));
        var store = new ProductImageStorage(root);
        await using var stream = new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10]);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Guid.NewGuid(), stream,
            ProductImageStorage.MaximumBytes + 1, "image/png"));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Delete_removes_only_the_named_product_image()
    {
        var root = Path.Combine(Path.GetTempPath(), "pos-image-tests", Guid.NewGuid().ToString("N"));
        var store = new ProductImageStorage(root);
        var firstProduct = Guid.NewGuid();
        var secondProduct = Guid.NewGuid();
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        try
        {
            await using var firstStream = new MemoryStream(png);
            var firstImage = await store.SaveAsync(firstProduct, firstStream, png.Length, "image/png");
            await using var secondStream = new MemoryStream(png);
            var secondImage = await store.SaveAsync(secondProduct, secondStream, png.Length, "image/png");

            store.Delete(firstImage);

            Assert.False(File.Exists(store.GetAbsolutePath(firstImage)));
            Assert.True(File.Exists(store.GetAbsolutePath(secondImage)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
