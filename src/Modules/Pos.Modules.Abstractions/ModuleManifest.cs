namespace Pos.Modules.Abstractions;

/// <summary>Metadata declared by an official POS module package.</summary>
public sealed record ModuleManifest(
    string Id,
    Version Version,
    ModuleHostApiRange HostApi,
    ModuleClientApiRange? ClientApi,
    string? EntryAssembly,
    string? EntryType,
    string? DesktopEntryAssembly,
    string? DesktopEntryType,
    ModuleDesktopApiRange? DesktopApi,
    IReadOnlyList<ModuleDependency> Dependencies,
    IReadOnlyList<string> Permissions,
    string? Entitlement,
    IReadOnlyList<string> Migrations);

public sealed record ModuleHostApiRange(int Min, int Max);

/// <summary>Inclusive minimum and exclusive maximum client protocol version.</summary>
public sealed record ModuleClientApiRange(Version Min, Version MaxExclusive)
{
    public bool Accepts(Version version) => version >= Min && version < MaxExclusive;
}

/// <summary>Inclusive minimum and exclusive maximum desktop extension API version.</summary>
public sealed record ModuleDesktopApiRange(Version Min, Version MaxExclusive)
{
    public bool Accepts(Version version) => version >= Min && version < MaxExclusive;
}

/// <summary>Inclusive minimum and exclusive maximum module version range.</summary>
public sealed record ModuleDependency(string Id, Version MinVersion, Version MaxVersionExclusive)
{
    public bool Accepts(Version version) => version >= MinVersion && version < MaxVersionExclusive;
}

public sealed class ModuleManifestException(string message) : Exception(message);
