using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pos.Desktop;
using Pos.Desktop.Presentation;

namespace Pos.Application.Tests;

[Collection("WPF UI")]
public sealed class MainWindowCartLayoutAcceptanceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Sale_layout_gives_the_cart_more_width_and_keeps_a_compact_product_preview()
    {
        var document = LoadView();
        var rootGrid = Assert.Single(document.Root!.Elements(Presentation + "Grid"));
        var columns = rootGrid.Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width"))
            .ToArray();

        Assert.Equal(new[] { "170", "*", "220" }, columns);

        var preview = Assert.Single(document.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute(Xaml + "Name") == "ProductPreviewPanel");
        var names = preview.DescendantsAndSelf()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => name is not null)
            .ToArray();
        Assert.Contains("ProductPreviewImage", names);
        Assert.Contains("ProductPreviewName", names);
        Assert.Contains("ProductPreviewSku", names);
        Assert.Contains("ProductPreviewPrice", names);
    }

    [Fact]
    public void Customer_and_operator_are_visible_in_a_context_strip_above_search()
    {
        var document = LoadView();
        var context = Assert.Single(document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == "SaleContextBar");
        var bindings = context.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.Contains(bindings, value => value.Contains("SelectedCustomer.Name", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("SelectedCustomer.DocumentNumber", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("OperatorName", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("CashStatus", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("FallbackValue=Venta general", StringComparison.Ordinal));
        Assert.Contains(context.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute("Click") == "Customer_Click");
        Assert.Contains(context.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute("Click") == "OperatorSessionAction_Click");

        var search = Assert.Single(document.Descendants(Presentation + "TextBox"),
            element => (string?)element.Attribute(Xaml + "Name") == "SearchBox");
        Assert.Equal("1", (string?)search.Ancestors(Presentation + "Grid").First().Attribute("Grid.Row"));
        var cart = Assert.Single(document.Descendants(Presentation + "DataGrid"),
            element => (string?)element.Attribute(Xaml + "Name") == "CartGrid");
        Assert.Equal("3", (string?)cart.Attribute("Grid.Row"));
    }

    [Fact]
    public void Cart_description_wraps_to_two_lines_and_exposes_the_full_name_as_tooltip()
    {
        var document = LoadView();
        var cart = Assert.Single(document.Descendants(Presentation + "DataGrid"),
            element => (string?)element.Attribute(Xaml + "Name") == "CartGrid");
        var descriptionColumn = Assert.Single(cart.Descendants(Presentation + "DataGridTextColumn"),
            element => (string?)element.Attribute("Header") == "Descripción");
        Assert.Contains("Product.Name", (string?)descriptionColumn.Attribute("Binding"));
        Assert.Equal("*", (string?)descriptionColumn.Attribute("Width"));
        var setters = descriptionColumn.Descendants(Presentation + "Setter")
            .ToDictionary(setter => setter.Attribute("Property")!.Value, setter => setter.Attribute("Value")!.Value);
        Assert.Equal("Wrap", setters["TextWrapping"]);
        Assert.Equal("36", setters["MaxHeight"]);
        Assert.Equal("18", setters["LineHeight"]);

        Assert.Equal("CartGrid_LoadingRow", (string?)cart.Attribute("LoadingRow"));
        var code = File.ReadAllText(PathFor("src", "Pos.Desktop", "MainWindow.xaml.cs"));
        Assert.Contains("e.Row.ToolTip = e.Row.Item is CartLineViewModel line ? line.Product.Name : null", code);
    }

    [Fact]
    public void Narrower_navigation_keeps_all_labels_and_the_charge_button_inside_its_column()
    {
        var document = LoadView();
        var navigation = Assert.Single(document.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute(Xaml + "Name") == "RightNavigationPanel");
        var actionIds = navigation.Descendants(Presentation + "Button")
            .Select(button => (string?)button.Attribute("AutomationProperties.AutomationId"))
            .Where(id => id?.StartsWith("Navigate", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(10, actionIds.Length);

        var charge = Assert.Single(navigation.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "ChargeSale");
        Assert.Equal("Stretch", (string?)charge.Attribute("HorizontalAlignment"));
        Assert.Equal("0", (string?)charge.Attribute("MinWidth"));
    }

    [Theory]
    [InlineData(1180, 720, true)]
    [InlineData(1600, 900, false)]
    public void Wpf_layout_fits_cart_and_actions_at_supported_window_sizes(double width, double height, bool nameShouldWrap)
    {
        RunOnSta(() =>
        {
            using var api = new PosApiClient(new Uri("http://127.0.0.1:1/"));
            var viewModel = new PosViewModel(api);
            var window = new MainWindow(viewModel, api, startSessionOnLoad: false)
            {
                WindowState = WindowState.Normal,
                Width = width,
                Height = height,
                Left = 0,
                Top = 0,
                Opacity = 0,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            try
            {
                Assert.True(viewModel.AddProduct(new ProductInfo(Guid.NewGuid(), "LARGO-001",
                    "ARROZ PREMIUM DE GRANO LARGO PRESENTACIÓN FAMILIAR DE CINCO KILOGRAMOS", 129.99m, "VES", TaxTreatment: "Taxable16")));
                window.Show();
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                window.UpdateLayout();
                var root = Assert.IsType<Grid>(window.Content);
                root.UpdateLayout();

                var preview = Assert.IsType<Border>(window.FindName("ProductPreviewPanel"));
                var navigation = Assert.IsType<Border>(window.FindName("RightNavigationPanel"));
                var cart = Assert.IsType<DataGrid>(window.FindName("CartGrid"));
                var search = Assert.IsType<TextBox>(window.FindName("SearchBox"));
                var descriptionColumn = cart.Columns.Single(column => Equals(column.Header, "Descripción"));
                var measuredColumns = string.Join(", ", cart.Columns.Select(column =>
                    $"{column.Header}:{column.ActualWidth:0}/{column.Width}"));
                var charge = navigation.DescendantsOfType<Button>()
                    .Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Cobrar venta. Atajo F4");

                Assert.True(preview.ActualWidth is >= 165 and <= 175,
                    $"Foto de {preview.ActualWidth:0}px; ventana {window.ActualWidth:0}x{window.ActualHeight:0}, raíz {root.ActualWidth:0}x{root.ActualHeight:0}, visible={window.IsVisible}, presentationSource={PresentationSource.FromVisual(window) is not null}.");
                Assert.InRange(navigation.ActualWidth, 215, 225);
                Assert.True(search.ActualWidth > 300);
                Assert.True(cart.ActualWidth > 500);
                Assert.True(descriptionColumn.ActualWidth >= 120,
                    $"La columna Descripción mide {descriptionColumn.ActualWidth:0}px (carrito {cart.ActualWidth:0}px).");
                Assert.True(charge.ActualWidth <= navigation.ActualWidth - 20);
                var descriptionText = cart.DescendantsOfType<TextBlock>()
                    .Single(text => text.Text.StartsWith("ARROZ PREMIUM", StringComparison.Ordinal));
                Assert.True(nameShouldWrap == (descriptionText.ActualHeight >= 30),
                    $"La descripción tiene {descriptionText.ActualWidth:0}x{descriptionText.ActualHeight:0}px en {width:0}x{height:0}. Columnas: {measuredColumns}");

                var renderDirectory = Environment.GetEnvironmentVariable("POS_LAYOUT_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(renderDirectory))
                {
                    Directory.CreateDirectory(renderDirectory);
                    var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(renderDirectory, $"pos-main-window-{(int)width}x{(int)height}.png"));
                    encoder.Save(file);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static XDocument LoadView() => XDocument.Load(PathFor("src", "Pos.Desktop", "MainWindow.xaml"));

    private static string PathFor(params string[] paths)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prompt_Maestro_POS_Escritorio_TDD.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. paths]);
    }

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
        if (failure is not null) throw new InvalidOperationException("La prueba de distribución WPF falló.", failure);
    }
}

internal static class MainWindowCartLayoutTestExtensions
{
    public static IEnumerable<T> DescendantsOfType<T>(this DependencyObject parent) where T : DependencyObject
    {
        if (parent is not System.Windows.Media.Visual) yield break;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in child.DescendantsOfType<T>()) yield return descendant;
        }
    }
}
