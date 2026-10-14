using System.Xml.Linq;
using System.Windows;
using Pos.Desktop;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

[Collection("WPF UI")]
public sealed class FullscreenBrandingAcceptanceTests
{
    [Fact]
    public void LoginWindow_IsBorderlessFullscreenAndDisplaysBrandAssets()
    {
        var document = LoadXaml("src/Pos.Desktop/LoginWindow.xaml");
        var root = document.Root!;

        Assert.Equal("None", (string?)root.Attribute("WindowStyle"));
        Assert.Equal("Maximized", (string?)root.Attribute("WindowState"));
        Assert.Contains(document.Descendants().Where(element => element.Name.LocalName == "Image"), element =>
            ((string?)element.Attribute("Source"))?.Contains("balanzapro-login-bg.png", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(document.Descendants().Where(element => element.Name.LocalName == "Image"), element =>
            ((string?)element.Attribute("Source"))?.Contains("balanzapro-logo.png", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void PosWindow_IsBorderlessFullscreenAndHasAnAccessibleExitButton()
    {
        var document = LoadXaml("src/Pos.Desktop/MainWindow.xaml");
        var root = document.Root!;

        Assert.Equal("None", (string?)root.Attribute("WindowStyle"));
        Assert.Equal("Maximized", (string?)root.Attribute("WindowState"));
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ExitButton" &&
            (string?)element.Attribute("AutomationProperties.Name") == "Salir del punto de venta");
    }

    [Fact]
    public void DesktopExecutable_DeclaresAWindowsApplicationIcon()
    {
        var project = XDocument.Load(RepoPath("src/Pos.Desktop/Pos.Desktop.csproj"));
        var icon = project.Descendants("ApplicationIcon").SingleOrDefault()?.Value;

        Assert.Equal("Assets/balanzapro.ico", icon?.Replace('\\', '/'));
        Assert.True(File.Exists(RepoPath("src/Pos.Desktop/Assets/balanzapro.ico")));
    }

    [Fact]
    public void WpfWindows_LoadCompiledBrandResourcesAndUseFullscreenStyles()
    {
        RunOnSta(() =>
        {
            using var api = new PosApiClient(new Uri("http://127.0.0.1:1/"));
            var viewModel = new PosViewModel(api);
            var login = new LoginWindow(viewModel, Guid.NewGuid());
            var main = new MainWindow(viewModel, api);
            try
            {
                Assert.Equal(WindowStyle.None, login.WindowStyle);
                Assert.Equal(WindowState.Maximized, login.WindowState);
                Assert.Equal(WindowStyle.None, main.WindowStyle);
                Assert.Equal(WindowState.Maximized, main.WindowState);
                Assert.NotNull(login.Icon);
                Assert.NotNull(main.Icon);
                Assert.NotNull(login.FindName("LoginButton"));
                Assert.NotNull(main.FindName("ExitButton"));
            }
            finally
            {
                login.Close();
                main.Close();
            }
        });
    }

    private static XDocument LoadXaml(string path) => XDocument.Load(RepoPath(path));

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("La prueba WPF falló en el hilo STA.", failure);
    }

    private static string RepoPath(string relativePath)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pos.sln")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
        return Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
