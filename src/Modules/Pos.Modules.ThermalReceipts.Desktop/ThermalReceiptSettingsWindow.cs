using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using Pos.Desktop.Modules.Abstractions;
using Pos.Desktop.Presentation;
using Pos.Shared.Contracts;

namespace Pos.Modules.ThermalReceipts.Desktop;

internal sealed class ThermalReceiptSettingsWindow : Window
{
    private readonly PosDesktopModuleContext _context;
    private readonly bool _canConfigure;
    private readonly ComboBox _paper = new() { ItemsSource = new[] { 58, 80 }, SelectedItem = 80, Width = 85 };
    private readonly ComboBox _alignment = new() { ItemsSource = new[] { "Left", "Center", "Right" }, SelectedItem = "Center", Width = 110 };
    private readonly ComboBox _font = new() { ItemsSource = new[] { 80, 90, 100, 110, 125, 150 }, SelectedItem = 100, Width = 85 };
    private readonly ComboBox _printer = new() { MinWidth = 280, Height = 32 };
    private readonly ComboBox _copies = new() { ItemsSource = new[] { 1, 2, 3 }, SelectedItem = 1, Width = 70 };
    private readonly TextBox _header = new() { AcceptsReturn = true, Height = 58, Text = "Punto de venta" };
    private readonly TextBox _footer = new() { AcceptsReturn = true, Height = 58 };
    private readonly Dictionary<string, CheckBox> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> _labels = new(StringComparer.Ordinal);
    private readonly ListBox _itemColumns = new() { Height = 142, MinWidth = 230 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(4) };
    private ThermalReceiptTemplate _template = ThermalReceiptTemplateDefaults.Default;

    public ThermalReceiptSettingsWindow(PosDesktopModuleContext context)
    {
        _context = context;
        _canConfigure = context.GrantedPermissions.Contains("ThermalReceipts.Configure");
        Title = "Configurar ticket térmico"; Width = 1000; Height = 780; MinWidth = 850; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
        Content = BuildLayout();
        Loaded += async (_, _) => await LoadTemplateAsync();
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        var heading = new Border { Background = Brush("#102D50"), CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 12), Margin = new Thickness(0, 0, 0, 12) };
        var headingContent = new StackPanel();
        headingContent.Children.Add(new TextBlock { Text = "Ticket térmico", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        headingContent.Children.Add(new TextBlock { Text = "Diseño compartido entre terminales; cada caja usa su impresora local. Comprobante comercial no fiscal.",
            Foreground = Brush("#D7E3F2"), Margin = new Thickness(0, 4, 0, 0) });
        heading.Child = headingContent;
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Vista previa", async (_, _) => await PreviewAsync()));
        actions.Children.Add(ActionButton("Imprimir prueba", async (_, _) => await PrintTestAsync()));
        actions.Children.Add(ActionButton(_canConfigure ? "Guardar diseño e impresora" : "Guardar impresora",
            async (_, _) => await SaveAsync(), true));
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildEditor() });
        return root;
    }

    private UIElement BuildEditor()
    {
        var stack = new StackPanel();
        var printerGroup = Group("Impresora de esta terminal");
        printerGroup.Children.Add(Row(("Impresora instalada", _printer), ("Copias", _copies)));
        printerGroup.Children.Add(new TextBlock { Text = "La impresora y las copias se guardan solo en esta computadora.", Foreground = Brush("#52677F"), Margin = new Thickness(3) });
        stack.Children.Add(printerGroup);

        var layoutGroup = Group("Diseño compartido del comprobante");
        layoutGroup.Children.Add(Row(("Ancho (mm)", _paper), ("Alineación", _alignment), ("Tamaño de letra (%)", _font)));
        layoutGroup.Children.Add(Row(("Encabezado (una línea por renglón)", _header)));
        layoutGroup.Children.Add(Row(("Pie del ticket", _footer)));
        var sectionPanel = new WrapPanel();
        foreach (var (id, label) in new[] { ("saleInfo", "Datos de venta"), ("customer", "Cliente"), ("items", "Artículos"),
                     ("total", "Total"), ("rounding", "Ajuste"), ("payments", "Pagos") })
        {
            var check = new CheckBox { Content = label, Margin = new Thickness(7), IsChecked = _template.Sections.Contains(id) };
            _sections[id] = check; sectionPanel.Children.Add(check);
        }
        layoutGroup.Children.Add(SectionLabel("Secciones visibles")); layoutGroup.Children.Add(sectionPanel);
        var columnLayout = new DockPanel { Margin = new Thickness(3, 6, 3, 3) };
        var moveButtons = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        moveButtons.Children.Add(ActionButton("Subir", (_, _) => MoveColumn(-1)));
        moveButtons.Children.Add(ActionButton("Bajar", (_, _) => MoveColumn(1)));
        DockPanel.SetDock(moveButtons, Dock.Right); columnLayout.Children.Add(moveButtons); columnLayout.Children.Add(_itemColumns);
        var columnGroup = Group("Campos de cada artículo (marca y ordena)");
        columnGroup.Children.Add(columnLayout);
        layoutGroup.Children.Add(columnGroup);

        var labelPanel = new UniformGrid { Columns = 2 };
        foreach (var (id, label) in new[] { ("receiptTitle", "Título"), ("sale", "Venta"), ("date", "Fecha"),
                     ("customer", "Cliente"), ("sku", "Código SKU"), ("quantity", "Cantidad"), ("unitPrice", "Precio"),
                     ("lineTotal", "Importe"), ("total", "Total"), ("payments", "Pagos"), ("nonFiscal", "Aviso no fiscal") })
        {
            var box = new TextBox { Height = 30, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
            _labels[id] = box; labelPanel.Children.Add(Row((label, box)));
        }
        layoutGroup.Children.Add(SectionLabel("Textos editables")); layoutGroup.Children.Add(labelPanel);
        layoutGroup.Visibility = _canConfigure ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(layoutGroup);
        LoadLocalPrinterSettings();
        return stack;
    }

    private async Task LoadTemplateAsync()
    {
        try
        {
            var snapshot = await _context.ApiClient.GetModuleAsync<ThermalReceiptTemplateSnapshot>(_context.ModuleId, "template");
            _template = snapshot?.Template ?? ThermalReceiptTemplateDefaults.Default;
            _paper.SelectedItem = _template.PaperWidthMm; _alignment.SelectedItem = _template.Alignment; _font.SelectedItem = _template.FontSizePercent;
            _header.Text = string.Join(Environment.NewLine, _template.HeaderLines); _footer.Text = string.Join(Environment.NewLine, _template.FooterLines);
            foreach (var (id, check) in _sections) check.IsChecked = _template.Sections.Contains(id);
            foreach (var (id, box) in _labels) box.Text = _template.Labels.GetValueOrDefault(id, string.Empty);
            SetItemColumns(_template.ItemColumns);
            _status.Text = "Diseño cargado desde el servidor.";
        }
        catch (Exception exception) { _status.Text = $"No se pudo cargar el diseño compartido: {PosUserMessage.ForException(exception)}"; }
    }

    private ThermalReceiptTemplate BuildTemplate() => _template with
    {
        PaperWidthMm = (int)(_paper.SelectedItem ?? 80),
        Alignment = (string)(_alignment.SelectedItem ?? "Center"),
        FontSizePercent = (int)(_font.SelectedItem ?? 100),
        HeaderLines = Lines(_header.Text), FooterLines = Lines(_footer.Text),
        Sections = _sections.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray(),
        ItemColumns = _itemColumns.Items.OfType<CheckBox>().Where(box => box.IsChecked == true)
            .Select(box => (string)box.Tag).ToArray(),
        Labels = ThermalReceiptTemplateDefaults.Default.Labels.ToDictionary(pair => pair.Key,
            pair => _labels.TryGetValue(pair.Key, out var box) && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : pair.Value,
            StringComparer.Ordinal)
    };

    private async Task SaveAsync()
    {
        var template = BuildTemplate();
        try
        {
            if (_canConfigure)
                await _context.ApiClient.PutModuleAsync(_context.ModuleId, "template", new SaveThermalReceiptTemplateRequest(template));
            if (_printer.SelectedItem is string printerName)
                new ThermalReceiptPrinterSettings(printerName, (int)(_copies.SelectedItem ?? 1)).Save();
            else
                _status.Text = "Diseño guardado. Selecciona una impresora para imprimir en esta terminal.";
            _template = template;
            if (_printer.SelectedItem is string)
                _status.Text = _canConfigure ? "Diseño compartido y configuración local guardados." : "Impresora y copias guardadas en esta terminal.";
        }
        catch (Exception exception) { _status.Text = $"No se pudo guardar: {PosUserMessage.ForException(exception)}"; }
    }

    private Task PreviewAsync()
    {
        var window = new Window { Title = "Vista previa del ticket", Width = 460, Height = 680, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F2F5F9") };
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Border { Background = Brush("#102D50"), CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10) };
        header.Child = new TextBlock { Text = "Vista previa del ticket", Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.Bold };
        Grid.SetRow(header, 0); layout.Children.Add(header);
        var preview = new Border { Background = Brushes.White, BorderBrush = Brush("#D5DFEB"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10) };
        preview.Child = new FlowDocumentScrollViewer
        {
            Document = ThermalReceiptDocumentBuilder.Build(SampleReceipt(), BuildTemplate()),
            IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(preview, 1); layout.Children.Add(preview); window.Content = layout;
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private Task PrintTestAsync()
    {
        if (_printer.SelectedItem is not string printerName) { _status.Text = "Selecciona primero una impresora instalada."; return Task.CompletedTask; }
        try
        {
            using var server = new LocalPrintServer();
            var queue = server.GetPrintQueue(printerName);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            var document = ThermalReceiptDocumentBuilder.Build(SampleReceipt(), BuildTemplate());
            var ticket = queue.DefaultPrintTicket.Clone();
            ticket.PageMediaSize = new PageMediaSize(document.PageWidth, document.PageHeight);
            ticket.CopyCount = (int)(_copies.SelectedItem ?? 1);
            var validTicket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
            if (validTicket.CopyCount != (int)(_copies.SelectedItem ?? 1))
            {
                _status.Text = "El controlador de Windows no aceptó la cantidad de copias seleccionada.";
                return Task.CompletedTask;
            }
            writer.Write(((IDocumentPaginatorSource)document).DocumentPaginator, validTicket);
            new ThermalReceiptPrinterSettings(printerName, (int)(_copies.SelectedItem ?? 1)).Save();
            _status.Text = "Ticket de prueba enviado a la cola de Windows.";
        }
        catch (Exception exception) { _status.Text = $"Falló la prueba de impresión: {PosUserMessage.ForException(exception)}"; }
        return Task.CompletedTask;
    }

    private SaleReceipt SampleReceipt() => new(Guid.NewGuid(), DateTime.UtcNow, "VES", 1250m,
        [new ReceiptLine("DEMO-001", "Producto de prueba", 2m, 625m, 1250m, 625m, "VES", null)],
        [new SalePaymentInput("Efectivo", 1250m, "VES")]) { CustomerName = "Cliente de prueba" };

    private void LoadLocalPrinterSettings()
    {
        try
        {
            _printer.ItemsSource = ThermalReceiptPrinterSettings.InstalledPrinters();
            var settings = ThermalReceiptPrinterSettings.Load();
            _printer.SelectedItem = settings.PrinterName; _copies.SelectedItem = settings.Copies;
        }
        catch (Exception exception) { _status.Text = $"No se pudieron consultar impresoras Windows: {PosUserMessage.ForException(exception)}"; }
    }

    private void SetItemColumns(IReadOnlyList<string> selected)
    {
        var all = new[] { ("description", "Descripción"), ("quantity", "Cantidad"), ("unitPrice", "Precio unitario"),
            ("lineTotal", "Importe"), ("sku", "Código SKU") };
        var ordered = selected.Concat(all.Select(item => item.Item1)).Distinct(StringComparer.Ordinal).ToArray();
        _itemColumns.Items.Clear();
        foreach (var id in ordered)
        {
            var item = all.FirstOrDefault(candidate => candidate.Item1 == id);
            if (item.Item1 is null) continue;
            var required = id is "description" or "quantity" or "lineTotal";
            var check = new CheckBox { Content = item.Item2, Tag = id, IsChecked = selected.Contains(id),
                IsEnabled = !required, Margin = new Thickness(4) };
            System.Windows.Automation.AutomationProperties.SetName(check, item.Item2);
            _itemColumns.Items.Add(check);
        }
    }

    private void MoveColumn(int offset)
    {
        var index = _itemColumns.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _itemColumns.Items.Count) return;
        var item = _itemColumns.Items[index];
        _itemColumns.Items.RemoveAt(index);
        _itemColumns.Items.Insert(target, item);
        _itemColumns.SelectedIndex = target;
    }

    private static string[] Lines(string text) => text.Replace("\r", string.Empty).Split('\n',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static StackPanel Group(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brush("#132D4F"), Margin = new Thickness(0, 8, 0, 6) });
        return panel;
    }
    private static TextBlock SectionLabel(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 8, 3, 2) };
    private static UIElement Row(params (string Label, UIElement Control)[] fields)
    {
        var grid = new UniformGrid { Columns = fields.Length * 2, Margin = new Thickness(0, 2, 0, 5) };
        foreach (var field in fields)
        {
            grid.Children.Add(new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) });
            grid.Children.Add(field.Control);
        }
        return grid;
    }
    private static Button ActionButton(string text, RoutedEventHandler click, bool primary = false)
    {
        var button = new Button { Content = text, Height = 42, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(5),
            Background = primary ? Brush("#0867D5") : Brush("#FFFFFF"), Foreground = primary ? Brushes.White : Brush("#132D4F"),
            BorderBrush = primary ? Brush("#0867D5") : Brush("#D5DFEB") };
        button.Click += click; return button;
    }
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
