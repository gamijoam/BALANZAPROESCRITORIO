using Microsoft.AspNetCore.Http;
using Pos.Modules.Abstractions;

namespace Pos.Modules.Tests;

public sealed class PosModuleEndpointBackwardCompatibilityTests
{
    [Fact]
    public void Keeps_constructor_signatures_used_by_precompiled_modules()
    {
        Assert.NotNull(typeof(PosModuleEndpoint).GetConstructor(
            [typeof(string), typeof(string), typeof(RequestDelegate), typeof(string)]));
        Assert.NotNull(typeof(PosModuleEndpoint).GetConstructor(
            [typeof(string), typeof(string), typeof(Func<HttpContext, Task<IResult>>), typeof(string)]));
    }
}
