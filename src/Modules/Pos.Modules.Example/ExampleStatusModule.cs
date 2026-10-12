using Microsoft.AspNetCore.Http;
using Pos.Modules.Abstractions;

namespace Pos.Modules.Example;

/// <summary>Small executable sample used to validate the official module activation pipeline.</summary>
public sealed class ExampleStatusModule : IPosServerModule
{
    public string ModuleId => "example-status";

    public IReadOnlyList<PosModuleEndpoint> GetEndpoints() =>
    [
        new("GET", "status", WriteStatusAsync)
    ];

    private static Task WriteStatusAsync(HttpContext context) =>
        context.Response.WriteAsJsonAsync(new
        {
            moduleId = "example-status",
            version = "1.0.0",
            status = "active"
        }, context.RequestAborted);
}
