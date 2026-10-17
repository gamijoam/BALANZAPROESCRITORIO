using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Pos.Desktop;

internal sealed record UpdateRelease(string Tag, string Version, string Title, string Notes, string AssetName,
    Uri DownloadUrl, long Size, string? ModuleId = null);
internal sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes);

internal static class UpdateReleaseCatalog
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const string ReleasesUrl = "https://api.github.com/repos/gamijoam/BALANZAPROESCRITORIO/releases";

    static UpdateReleaseCatalog()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("BalanzaPro-POS-Updater/1.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static async Task<IReadOnlyList<UpdateRelease>> GetAsync(string prefix, string assetSuffix,
        CancellationToken cancellationToken = default)
    {
        var releases = new List<UpdateRelease>();
        for (var page = 1; page <= 5; page++)
        {
            using var response = await Client.GetAsync($"{ReleasesUrl}?per_page=100&page={page}", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                var tag = release.GetProperty("tag_name").GetString() ?? "";
                if (!tag.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var versionText = tag[prefix.Length..];
                string? moduleId = null;
                if (prefix == "module-")
                {
                    var separator = versionText.LastIndexOf("-v", StringComparison.Ordinal);
                    if (separator <= 0) continue;
                    moduleId = versionText[..separator];
                    versionText = versionText[(separator + 2)..];
                    if (!System.Text.RegularExpressions.Regex.IsMatch(moduleId, "^[a-z][a-z0-9-]*$")) continue;
                }
                if (!Version.TryParse(versionText, out _)) continue;
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    var urlText = asset.GetProperty("browser_download_url").GetString();
                    var size = asset.GetProperty("size").GetInt64();
                    if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                        !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || size <= 0) continue;
                    releases.Add(new UpdateRelease(tag, versionText, release.GetProperty("name").GetString() ?? tag,
                        release.GetProperty("body").GetString() ?? "", name, url, size, moduleId));
                }
            }
            if (document.RootElement.GetArrayLength() < 100) break;
        }
        return releases.OrderByDescending(item => Version.Parse(item.Version)).ToArray();
    }

    public static async Task DownloadAsync(UpdateRelease release, string destination, long maximumBytes,
        CancellationToken cancellationToken = default, IProgress<UpdateDownloadProgress>? progress = null)
    {
        using var response = await Client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidDataException("La descarga supera el tamaño permitido.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += count;
            if (total > maximumBytes) throw new InvalidDataException("La descarga supera el tamaño permitido.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            progress?.Report(new UpdateDownloadProgress(total, release.Size));
        }
        if (total != release.Size) throw new InvalidDataException("La descarga quedó incompleta.");
    }
}
