namespace Pos.Application.Tests;

public sealed class LoginFlowTests
{
    [Fact]
    public void Login_form_prioritizes_credentials_and_keeps_service_configuration_available()
    {
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot is not null && !File.Exists(Path.Combine(sourceRoot.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            sourceRoot = sourceRoot.Parent;
        Assert.NotNull(sourceRoot);
        var xaml = File.ReadAllText(Path.Combine(sourceRoot!.FullName, "src", "Pos.Desktop", "LoginWindow.xaml"));

        Assert.DoesNotContain("x:Name=\"UsernameBox\" Text=\"demo\"", xaml);
        Assert.Contains("x:Name=\"ErrorLabel\"", xaml);
        Assert.Contains("x:Name=\"ErrorLabel\" Foreground=\"#B43B42\"", xaml);
        Assert.Contains("MinHeight=\"30\"", xaml);
        Assert.True(xaml.IndexOf("x:Name=\"UsernameBox\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"PinBox\"", StringComparison.Ordinal));
        Assert.True(xaml.IndexOf("x:Name=\"PinBox\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"ConnectionExpander\"", StringComparison.Ordinal));
        Assert.Contains("x:Name=\"ConnectionExpander\"", xaml);
        Assert.Contains("Header=\"Conexión al servicio\"", xaml);
        Assert.Contains("IsExpanded=\"False\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Usuario\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"PIN\"", xaml);
    }

    [Fact]
    public void Login_window_shows_progress_and_preserves_pin_when_connection_fails()
    {
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot is not null && !File.Exists(Path.Combine(sourceRoot.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            sourceRoot = sourceRoot.Parent;
        Assert.NotNull(sourceRoot);
        var loginCode = File.ReadAllText(Path.Combine(sourceRoot!.FullName, "src", "Pos.Desktop", "LoginWindow.xaml.cs"));

        Assert.Contains("Validando…", loginCode);
        Assert.Contains("catch (HttpRequestException)", loginCode);
        Assert.Contains("ConnectionExpander.IsExpanded = true", loginCode);
        Assert.Contains("BaseAddressBox.Focus()", loginCode);
        Assert.Contains("que el servidor esté iniciado", loginCode);
        Assert.Equal(1, loginCode.Split("PinBox.Clear()").Length - 1);
    }

    [Fact]
    public void Login_window_uses_the_application_service_address_and_recovers_after_a_bad_pin()
    {
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot is not null && !File.Exists(Path.Combine(sourceRoot.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            sourceRoot = sourceRoot.Parent;
        Assert.NotNull(sourceRoot);
        var code = File.ReadAllText(Path.Combine(sourceRoot!.FullName, "src", "Pos.Desktop", "LoginWindow.xaml.cs"));

        Assert.Contains("_viewModel.ApiBaseAddress", code);
        Assert.Contains("catch (PosApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)", code);
        Assert.Contains("if (_viewModel.IsAuthenticated)", code);
        Assert.Contains("El acceso fue aceptado", code);
        Assert.Contains("PinBox.Clear()", code);
        Assert.Contains("PinBox.Focus()", code);
    }

    [Fact]
    public void Login_window_only_authenticates_and_main_window_loads_session_data_afterward()
    {
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot is not null && !File.Exists(Path.Combine(sourceRoot.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            sourceRoot = sourceRoot.Parent;
        Assert.NotNull(sourceRoot);
        var loginCode = File.ReadAllText(Path.Combine(sourceRoot!.FullName, "src", "Pos.Desktop", "LoginWindow.xaml.cs"));
        var mainCode = File.ReadAllText(Path.Combine(sourceRoot.FullName, "src", "Pos.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("_viewModel.AuthenticateAsync(", loginCode);
        Assert.Contains("_viewModel.LoadInitialPosStateAsync()", mainCode);
    }

    [Fact]
    public void Main_window_exposes_explicit_operator_switch_and_explains_shared_open_cash()
    {
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot is not null && !File.Exists(Path.Combine(sourceRoot.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            sourceRoot = sourceRoot.Parent;
        Assert.NotNull(sourceRoot);
        var xaml = File.ReadAllText(Path.Combine(sourceRoot!.FullName, "src", "Pos.Desktop", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(sourceRoot.FullName, "src", "Pos.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("Content=\"{Binding OperatorSessionActionLabel}\"", xaml);
        Assert.Contains("Click=\"OperatorSessionAction_Click\"", xaml);
        Assert.Contains("await _viewModel.SwitchOperatorAsync()", code);
        Assert.Contains("_viewModel.CartLines.Count > 0", code);
        Assert.Contains("SignInAndLoadAsync()", code);

        var viewModel = File.ReadAllText(Path.Combine(sourceRoot.FullName, "src", "Pos.Desktop.Presentation", "PosViewModel.cs"));
        Assert.Contains("Caja abierta · {CashSession.Currency} · turno de {CashSession.OpenedByOperatorName}", viewModel);
    }

    [Fact]
    public void Current_cash_session_is_scoped_to_the_terminal_and_includes_its_opener()
    {
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot is not null && !File.Exists(Path.Combine(sourceRoot.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            sourceRoot = sourceRoot.Parent;
        Assert.NotNull(sourceRoot);
        var server = File.ReadAllText(Path.Combine(sourceRoot!.FullName, "src", "Pos.Server", "Program.cs"));
        var endpointStart = server.IndexOf("/api/v1/cash-sessions/current", StringComparison.Ordinal);
        Assert.True(endpointStart >= 0);
        var endpoint = server[endpointStart..Math.Min(server.Length, endpointStart + 5000)];

        Assert.Contains("x.TerminalId == access.TerminalId && x.Status == \"Open\"", endpoint);
        Assert.DoesNotContain("x.OperatorId == access.OperatorId", endpoint);
        Assert.Contains("openedByOperatorName", endpoint);
        Assert.Contains("/api/v1/cash-sessions/current/claim", server);
        Assert.Contains("/api/v1/cash-sessions/current/release", server);
    }
}
