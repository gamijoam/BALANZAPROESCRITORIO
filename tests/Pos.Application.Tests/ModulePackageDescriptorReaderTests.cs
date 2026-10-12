using System.IO.Compression;
using System.Text;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

public sealed class ModulePackageDescriptorReaderTests
{
    [Fact]
    public void Read_extracts_internal_id_and_version_and_resolves_the_warehouse_display_name()
    {
        var path = CreatePackage("warehouses", "1.0.0");
        try
        {
            var descriptor = ModulePackageDescriptorReader.Read(path);

            Assert.Equal("warehouses", descriptor.Id);
            Assert.Equal("1.0.0", descriptor.Version);
            Assert.Equal("Almacenes y traslados", descriptor.DisplayName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_rejects_a_zip_without_the_module_manifest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-no-manifest-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("readme.txt").Open())
            stream.Write(Encoding.UTF8.GetBytes("not a module"));

        try { Assert.Throws<InvalidDataException>(() => ModulePackageDescriptorReader.Read(path)); }
        finally { File.Delete(path); }
    }

    private static string CreatePackage(string id, string version)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-module-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry("module.manifest.json").Open();
        var manifest = $$"""{"id":"{{id}}","version":"{{version}}"}""";
        stream.Write(Encoding.UTF8.GetBytes(manifest));
        return path;
    }
}
