using System.Text.Json;
using System.Runtime.ExceptionServices;
using Pos.Desktop;

namespace Pos.Application.Tests;

public sealed class PaymentRuntimeDependencyTests
{
    [Fact]
    public void Payment_window_opens_without_a_missing_domain_assembly()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new PaymentWindow(100m, "VES", usdToVes: 36.50m);
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Desktop_runtime_manifest_includes_domain_for_payment_summary()
    {
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !Directory.Exists(Path.Combine(repoRoot.FullName, "src", "Pos.Desktop")))
            repoRoot = repoRoot.Parent;
        Assert.NotNull(repoRoot);

        var manifestPath = new[] { "Debug", "Release" }
            .Select(configuration => Path.Combine(repoRoot!.FullName,
                "src", "Pos.Desktop", "bin", configuration, "net10.0-windows", "Pos.Desktop.deps.json"))
            .FirstOrDefault(File.Exists);

        Assert.NotNull(manifestPath);

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var libraries = manifest.RootElement.GetProperty("libraries");

        Assert.Contains(libraries.EnumerateObject(), library => library.Name.StartsWith("Pos.Domain/", StringComparison.Ordinal));
    }
}
