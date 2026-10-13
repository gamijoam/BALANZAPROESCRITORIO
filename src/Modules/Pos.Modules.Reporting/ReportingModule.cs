using Pos.Modules.Abstractions;

namespace Pos.Modules.Reporting;

/// <summary>
/// The reporting module's current queries are part of the POS host API so that
/// permissions and data access stay centralized. This entry point lets the
/// signed package participate in the existing module activation lifecycle.
/// </summary>
public sealed class ReportingModule : IPosServerModule
{
    public string ModuleId => "advanced-reports";
    public IReadOnlyList<PosModuleEndpoint> GetEndpoints() => [];
}
