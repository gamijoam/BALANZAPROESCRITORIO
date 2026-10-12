using Microsoft.AspNetCore.Http;

namespace Pos.Modules.Abstractions;

/// <summary>Trusted server module entry point. Its routes are mounted only under the module's host-owned URL prefix.</summary>
public interface IPosServerModule
{
    string ModuleId { get; }
    IReadOnlyList<PosModuleEndpoint> GetEndpoints();
}

public sealed record PosModuleEndpoint
{
    public PosModuleEndpoint(string method, string route, RequestDelegate handler, string? requiredPermission = null)
    { Method = method; Route = route; Handler = handler; RequiredPermission = requiredPermission; }

    public PosModuleEndpoint(string method, string route, Func<HttpContext, Task<IResult>> handler, string? requiredPermission = null)
    { Method = method; Route = route; Handler = handler; RequiredPermission = requiredPermission; }

    public string Method { get; }
    public string Route { get; }
    public Delegate Handler { get; }
    public string? RequiredPermission { get; }

    public async Task InvokeAsync(HttpContext context)
    {
        if (Handler is RequestDelegate requestDelegate)
        {
            await requestDelegate(context);
            return;
        }
        if (Handler is Func<HttpContext, Task<IResult>> resultHandler)
        {
            var result = await resultHandler(context);
            await result.ExecuteAsync(context);
            return;
        }
        throw new InvalidOperationException("Tipo de controlador de módulo no compatible.");
    }
}
