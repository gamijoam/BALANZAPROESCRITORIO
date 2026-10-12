using System.Security.Cryptography;
using Pos.Modules.Packaging;

namespace Pos.Modules.Tests;

public sealed class ModuleInstallationPlannerTests
{
    [Fact]
    public void Plan_ResolvesSignedBundleDependenciesBeforeRequestedModuleWithoutSideEffects()
    {
        using var signingKey = RSA.Create(2048);
        var trustedKeys = new Dictionary<string, string> { ["publisher"] = signingKey.ExportSubjectPublicKeyInfoPem() };
        using var dependency = Package(Manifest("catalog", "[]"), signingKey);
        using var requested = Package(Manifest("warehouses", """[{"id":"catalog","minVersion":"1.0.0","maxVersionExclusive":"2.0.0"}]"""), signingKey);

        var plan = ModuleInstallationPlanner.CreatePlan(
            [requested, dependency], "warehouses", 1, trustedKeys, new HashSet<string>(StringComparer.Ordinal));

        Assert.True(plan.IsValid);
        Assert.Equal(["catalog", "warehouses"], plan.InstallationOrder.Select(item => item.Id));
        Assert.All(plan.InstallationOrder, item => Assert.Equal("publisher", item.SignerKeyId));
    }

    [Fact]
    public void Plan_RejectsMissingDependenciesBeforeProducingAnInstallPlan()
    {
        using var signingKey = RSA.Create(2048);
        using var requested = Package(Manifest("warehouses", """[{"id":"catalog","minVersion":"1.0.0","maxVersionExclusive":"2.0.0"}]"""), signingKey);

        var exception = Assert.Throws<ModuleInstallationPlanningException>(() =>
            ModuleInstallationPlanner.CreatePlan(
                [requested], "warehouses", 1,
                new Dictionary<string, string> { ["publisher"] = signingKey.ExportSubjectPublicKeyInfoPem() },
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("dependency_invalid", exception.Code);
    }

    [Fact]
    public void Plan_RejectsAnEntitlementThatHasNoLicenseGrant()
    {
        using var signingKey = RSA.Create(2048);
        using var licensed = Package(Manifest("advanced", "[]", "warehouses.advanced"), signingKey);

        var exception = Assert.Throws<ModuleInstallationPlanningException>(() =>
            ModuleInstallationPlanner.CreatePlan(
                [licensed], "advanced", 1,
                new Dictionary<string, string> { ["publisher"] = signingKey.ExportSubjectPublicKeyInfoPem() },
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("entitlement_unavailable", exception.Code);
    }

    [Fact]
    public void Plan_RejectsPackagesSignedByUntrustedKeys()
    {
        using var signingKey = RSA.Create(2048);
        using var untrustedKey = RSA.Create(2048);
        using var package = Package(Manifest("sample", "[]"), signingKey);

        var exception = Assert.Throws<ModuleInstallationPlanningException>(() =>
            ModuleInstallationPlanner.CreatePlan(
                [package], "sample", 1,
                new Dictionary<string, string> { ["publisher"] = untrustedKey.ExportSubjectPublicKeyInfoPem() },
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("package_invalid", exception.Code);
    }

    [Fact]
    public void Plan_RejectsMoreThanThirtyTwoPackagesBeforeInspectingThem()
    {
        var packageStreams = Enumerable.Range(0, 33).Select(_ => Stream.Null).ToArray();

        var exception = Assert.Throws<ModuleInstallationPlanningException>(() =>
            ModuleInstallationPlanner.CreatePlan(
                packageStreams, "sample", 1,
                new Dictionary<string, string>(), new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("package_count_invalid", exception.Code);
    }

    private static MemoryStream Package(string manifest, RSA signingKey)
    {
        var bytes = ModulePackageBuilder.Build(manifest, new Dictionary<string, byte[]>(), "publisher", signingKey);
        return new MemoryStream(bytes);
    }

    private static string Manifest(string id, string dependencies, string? entitlement = null) => $$"""
        {
          "id": "{{id}}",
          "version": "1.0.0",
          "hostApi": { "min": 1, "max": 1 }, "clientApi": { "min": "1.0.0", "maxExclusive": "2.0.0" },
          "dependencies": {{dependencies}},
          "permissions": ["Sample.View"],
          "entitlement": {{(entitlement is null ? "null" : $"\"{entitlement}\"")}},
          "migrations": []
        }
        """;
}
