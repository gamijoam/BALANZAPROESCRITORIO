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
    // Keep the original four-parameter constructors so modules compiled before
    // multiple endpoint permissions can still resolve their constructor calls.
    public PosModuleEndpoint(string method, string route, RequestDelegate handler, string? requiredPermission = null)
        : this(method, route, handler, requiredPermission, []) { }

    public PosModuleEndpoint(string method, string route, RequestDelegate handler, string? requiredPermission,
        params string[] additionalRequiredPermissions)
    { Method = method; Route = route; Handler = handler; RequiredPermissions = BuildPermissions(requiredPermission, additionalRequiredPermissions); }

    public PosModuleEndpoint(string method, string route, Func<HttpContext, Task<IResult>> handler, string? requiredPermission = null)
        : this(method, route, handler, requiredPermission, []) { }

    public PosModuleEndpoint(string method, string route, Func<HttpContext, Task<IResult>> handler, string? requiredPermission,
        params string[] additionalRequiredPermissions)
    { Method = method; Route = route; Handler = handler; RequiredPermissions = BuildPermissions(requiredPermission, additionalRequiredPermissions); }

    public string Method { get; }
    public string Route { get; }
    public Delegate Handler { get; }
    public IReadOnlyList<string> RequiredPermissions { get; }
    public string? RequiredPermission => RequiredPermissions.Count == 0 ? null : RequiredPermissions[0];

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

    private static IReadOnlyList<string> BuildPermissions(string? requiredPermission, IReadOnlyCollection<string>? additionalPermissions)
    {
        var permissions = (requiredPermission is null ? [] : new[] { requiredPermission })
            .Concat(additionalPermissions ?? [])
            .ToArray();
        if (permissions.Any(string.IsNullOrWhiteSpace) || permissions.Distinct(StringComparer.Ordinal).Count() != permissions.Length)
            throw new ArgumentException("Los permisos requeridos del endpoint deben ser válidos y no repetirse.");
        return permissions;
    }
}
