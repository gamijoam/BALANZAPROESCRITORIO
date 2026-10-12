using System.Net;
using System.Net.Http.Json;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class PosApiClientImageTests
{
    [Fact]
    public async Task Product_image_can_be_uploaded_read_and_removed_through_authenticated_routes()
    {
        var productId = Guid.NewGuid();
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, png);
        try
        {
            using var handler = new ProductImageHandler(productId, png);
            using var client = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
            await client.LoginAsync("demo", "1234", Guid.NewGuid());

            await client.UploadProductImageAsync(productId, path);
            Assert.Equal(png, await client.GetProductImageAsync(productId));
            await client.RemoveProductImageAsync(productId);

            Assert.Equal(1, handler.UploadCount);
            Assert.Equal(1, handler.ReadCount);
            Assert.Equal(1, handler.DeleteCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class ProductImageHandler(Guid productId, byte[] image) : HttpMessageHandler
    {
        public int UploadCount { get; private set; }
        public int ReadCount { get; private set; }
        public int DeleteCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { operatorId = Guid.NewGuid(), terminalId = Guid.NewGuid(), expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token" })
                };

            Assert.Equal($"/api/v1/products/{productId}/image", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Post)
            {
                UploadCount++;
                Assert.Equal("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);
                Assert.False(string.IsNullOrWhiteSpace(request.Content?.Headers.ContentType?.Parameters
                    .FirstOrDefault(parameter => parameter.Name == "boundary")?.Value));
                var multipartBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Assert.True(multipartBody.AsSpan().IndexOf(image) >= 0);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Get)
            {
                ReadCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) };
            }
            if (request.Method == HttpMethod.Delete)
            {
                DeleteCount++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }
}
