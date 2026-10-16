using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;
using Pos.Modules.Reporting.Desktop;
using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Xml.Linq;
using Pos.Application.Reporting;
using System.Runtime.ExceptionServices;

namespace Pos.Application.Tests;

[Collection("WPF UI")]
public sealed class AdvancedReportModuleContractTests
{
    [Fact]
    public void Report_center_uses_the_full_width_table_and_a_column_picker_popup()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Modules", "Pos.Modules.Reporting.Desktop", "ReportCenterWindow.xaml"));

        Assert.Contains("ReportGrid", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnPickerPopup", xaml, StringComparison.Ordinal);
        Assert.Contains("Vistas personales", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VISTAS PERSONALES</TextBlock>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"280\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_RejectsReportSourcesWithUndeclaredPermissions()
    {
        var registry = new PosDesktopModuleRegistry("inventory-reports", ["Inventory.View"]);

        var error = Assert.Throws<InvalidOperationException>(() => registry.RegisterReportSource(
            new PosDesktopReportSource("inventory.movements", "Movimientos de inventario", "Warehouse.View", [],
                (_, _, _) => Task.FromResult(PosDesktopReportResult.Empty))));

        Assert.Contains("debe estar declarado", error.Message);
    }

    [Fact]
    public void Registry_RejectsDuplicateReportIds()
    {
        var registry = new PosDesktopModuleRegistry("sales-reports", ["Reports.View"]);
        var source = CreateSalesSource();
        registry.RegisterReportSource(source);

        var error = Assert.Throws<InvalidOperationException>(() => registry.RegisterReportSource(source));

        Assert.Contains("ya fue registrado", error.Message);
    }

    [Fact]
    public void Registry_AllowsOnlyOneAdvancedReportCenter()
    {
        var registry = new PosDesktopModuleRegistry("advanced-reports", ["Reports.View"]);
        registry.RegisterReportCenter("reports.advanced", "Reportes", "Reports.View", _ => new System.Windows.Window());

        var error = Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterReportCenter("reports.second", "Otro reporte", "Reports.View", _ => new System.Windows.Window()));

        Assert.Contains("centro de reportes", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportColumnProjection_UsesOnlyVisibleColumnsInTheirConfiguredOrder()
    {
        var report = new PosDesktopReportSource("sales.summary", "Ventas", "ViewReports",
        [
            new PosDesktopReportColumn("date", "Fecha", PosDesktopReportValueType.DateTime),
            new PosDesktopReportColumn("currency", "Moneda", PosDesktopReportValueType.Text),
            new PosDesktopReportColumn("total", "Total", PosDesktopReportValueType.Decimal)
        ], (_, _, _) => Task.FromResult(PosDesktopReportResult.Empty));
        var view = new SavedReportViewDefinition("Diario", [
            new ReportColumnLayout("total", true, 0),
            new ReportColumnLayout("date", true, 1),
            new ReportColumnLayout("currency", false, 2)
        ], new Dictionary<string, string>());

        var columns = ReportColumnProjection.Apply(report.Columns, view.Columns);

        Assert.Equal(["total", "date"], columns.Select(column => column.Id));
    }

    [Fact]
    public void XlsxExporter_WritesVisibleColumnsAsRealNumericCellsWithoutExcelRuntime()
    {
        var columns = new[]
        {
            new PosDesktopReportColumn("name", "Producto", PosDesktopReportValueType.Text),
            new PosDesktopReportColumn("amount", "Importe", PosDesktopReportValueType.Decimal)
        };
        IReadOnlyDictionary<string, object?>[] rows =
        [new Dictionary<string, object?> { ["name"] = "HARINA PAN", ["amount"] = 12.5m }];

        var bytes = XlsxReportWriter.Write(columns, rows);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(sheetEntry);
        using var stream = sheetEntry.Open();
        var sheet = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal("HARINA PAN", sheet.Descendants(ns + "t").Last().Value);
        Assert.Equal("12.5", sheet.Descendants(ns + "c").Single(cell => (string?)cell.Attribute("r") == "B2").Element(ns + "v")?.Value);
    }

    [Fact]
    public void SavedViewPolicy_AcceptsOnlyPresentationConfigurationWithUniqueColumnsAndOrders()
    {
        var valid = new SavedReportViewDefinition("Por vendedor",
            [new("operador", true, 0), new("total", false, 1)], new Dictionary<string, string> { ["currency"] = "USD" });
        var duplicateColumn = new SavedReportViewDefinition("Duplicada",
            [new("total", true, 0), new("total", false, 1)], new Dictionary<string, string>());

        Assert.Null(ReportViewPolicy.Validate("reports.sales.daily", valid));
        Assert.NotNull(ReportViewPolicy.Validate("reports.sales.daily", duplicateColumn));
        Assert.False(ReportViewPolicy.IsValidReportId("../../reports"));
    }

    [Fact]
    public void ReportsTool_RoutesToAuthorizedModuleCenterAndKeepsBasicFallback()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Pos.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("section == \"Reportes\"", source);
        Assert.Contains("reportCenter.CreateWindow(reportModule.Context)", source);
        Assert.Contains("new ManagementWindow(_viewModel, section", source);
    }

    [Fact]
    public void AdvancedReportsModule_DeclaresReceivableAndCashMethodReportSources()
    {
        var registry = new PosDesktopModuleRegistry("advanced-reports", ["Reports.View", "Reports.Inventory"]);
        new ReportingDesktopModule().Configure(registry);

        Assert.NotNull(registry.ReportCenter);
        Assert.Contains(registry.ReportSources, source => source.Id == "reports.sales.detail");
        Assert.Contains(registry.ReportSources, source => source.Id == "reports.inventory.movements");
        Assert.Contains(registry.ReportSources, source => source.Id == "reports.receivables.balances" && !source.SupportsDateRange);
        Assert.Contains(registry.ReportSources, source => source.Id == "reports.receivables.movements" && source.SupportsDateRange);
        Assert.Contains(registry.ReportSources, source => source.Id == "reports.cash.methods" && source.SupportsDateRange);
        Assert.All(registry.ReportSources.Where(source => source.Id.StartsWith("reports.sales.", StringComparison.Ordinal)),
            source => Assert.Equal("Reports.View", source.RequiredPermission));
        Assert.All(registry.ReportSources.Where(source => source.Id.StartsWith("reports.receivables.", StringComparison.Ordinal) || source.Id == "reports.cash.methods"),
            source => Assert.Equal("Reports.View", source.RequiredPermission));
        Assert.Contains(registry.ReportSources, source => source.Id == "reports.inventory.products" && source.RequiredPermission == "Reports.Inventory");
    }

    [Fact]
    public async Task Cash_methods_source_flattens_collection_and_refund_totals_by_session()
    {
        using var handler = new CashMethodsRequestHandler();
        using var api = new PosApiClient(new Uri("http://localhost:5155"), new HttpClient(handler));
        await api.LoginAsync("demo", "4826", handler.TerminalId);
        var context = new PosDesktopModuleContext(api, handler.TerminalId, new HashSet<string>(["Reports.View"]),
            "advanced-reports", (_, _) => Task.FromResult<IReadOnlyList<ProductInfo>>([])) { ReportApiClient = api };
        var registry = new PosDesktopModuleRegistry("advanced-reports", ["Reports.View", "Reports.Inventory"]);
        new ReportingDesktopModule().Configure(registry);
        var source = Assert.Single(registry.ReportSources, item => item.Id == "reports.cash.methods");

        var result = await source.LoadAsync(context,
            new PosDesktopReportQuery(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, null, new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, row => Equals(row["operacion"], "Cobro") && Equals(row["metodo"], "Transferencia") &&
            Equals(row["moneda"], "USD") && Equals(row["importe"], 25m));
        Assert.Contains(result.Rows, row => Equals(row["operacion"], "Reintegro") && Equals(row["metodo"], "Efectivo") &&
            Equals(row["moneda"], "VES") && Equals(row["importe"], 125m));
    }

    [Fact]
    public void AdvancedReportsManifest_UsesPermissionsMappedToExistingOperatorPermissions()
    {
        var root = FindRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "src", "Modules", "Pos.Modules.Reporting.Desktop", "module.manifest.json"));
        var server = File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Program.cs"));

        Assert.Contains("\"Reports.View\"", manifest);
        Assert.Contains("\"Reports.Inventory\"", manifest);
        Assert.Contains("\"Reports.View\" => Permission.ViewReports", server);
        Assert.Contains("\"Reports.Inventory\" => Permission.ViewInventory", server);
        Assert.Contains("\"version\": \"1.2.0\"", manifest);
        Assert.Contains("\"min\": \"1.5.0\"", manifest);
    }

    [Fact]
    public void Receivable_report_endpoints_require_view_reports_and_validate_the_reporting_period()
    {
        var root = FindRepositoryRoot();
        var endpoints = File.ReadAllText(Path.Combine(root, "src", "Pos.Server", "Hito4Endpoints.cs"));
        var balances = EndpointBlock(endpoints, "app.MapGet(\"/api/v1/reports/receivables/balances\"");
        var movements = EndpointBlock(endpoints, "app.MapGet(\"/api/v1/reports/receivables/movements\"");

        Assert.Contains("Permission.ViewReports", balances, StringComparison.Ordinal);
        Assert.Contains("Permission.ViewReports", movements, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromDays(92)", movements, StringComparison.Ordinal);
        Assert.Contains("toUtc <= fromUtc", movements, StringComparison.Ordinal);
        Assert.Contains("toUtc", movements, StringComparison.Ordinal);
        Assert.Contains("pageSize", balances, StringComparison.Ordinal);
        Assert.Contains("pageSize", movements, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportCenterWindow_CanOpenAndInitializeItsWpfView()
    {
        Exception? failure = null;
        var api = new PosApiClient(new Uri("https://localhost:5155"));
        var context = new PosDesktopModuleContext(api, Guid.NewGuid(), new HashSet<string>(["Reports.View"]),
            "advanced-reports", (_, _) => Task.FromResult<IReadOnlyList<ProductInfo>>([]))
        {
            ReportApiClient = api,
            GetReportSources = () => []
        };
        var thread = new Thread(() =>
        {
            try
            {
                var application = new System.Windows.Application();
                var window = new ReportCenterWindow(context);
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(window.Close);
                window.ShowDialog();
                application.Shutdown();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }

    private static PosDesktopReportSource CreateSalesSource() => new(
        "sales.summary", "Ventas", "Reports.View", [], (_, _, _) => Task.FromResult(PosDesktopReportResult.Empty));

    private static string EndpointBlock(string source, string route)
    {
        var start = source.IndexOf(route, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No se encontró la ruta {route}.");
        var end = source.IndexOf("app.MapGet(\"/api/v1/", start + route.Length, StringComparison.Ordinal);
        if (end < 0) end = source.IndexOf("app.MapPost(\"/api/v1/", start + route.Length, StringComparison.Ordinal);
        return source[start..(end < 0 ? source.Length : end)];
    }

    private sealed class CashMethodsRequestHandler : HttpMessageHandler
    {
        public Guid TerminalId { get; } = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        operatorId = Guid.NewGuid(), terminalId = TerminalId,
                        expiresAtUtc = DateTime.UtcNow.AddHours(1), accessToken = "test-token"
                    })
                });

            Assert.Equal("/api/v1/reports/cash-sessions", request.RequestUri.AbsolutePath);
            var opened = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
            var closed = opened.AddHours(8);
            var row = new CashSessionReportRow(Guid.NewGuid(), "Closed", "VES", opened, closed, "cajera", 0m, 0m,
                100m, 100m, 0m, 1000m, 1000m, 0m)
            {
                Payments =
                [
                    new CashMethodSummary("Cobro", "Transferencia", "USD", 25m),
                    new CashMethodSummary("Reintegro", "Efectivo", "VES", 125m)
                ]
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CashSessionReportInfo(1, 250, 1, [row]))
            });
        }
    }
}
