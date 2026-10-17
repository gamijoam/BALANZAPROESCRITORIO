using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Pos.Modules.Packaging;

namespace Pos.Desktop;

public partial class PosUpdateWindow : Window
{
    private readonly string _installationRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
    private readonly Version _currentVersion;
    private string? _localArchive;
    private bool _busy;
    public string? PreparedPackageDirectory { get; private set; }

    public PosUpdateWindow()
    {
        InitializeComponent();
        Closing += CancelCloseWhileBusy;
        var versionPath = Path.Combine(_installationRoot, "pos-version.json");
        var version = File.Exists(versionPath)
            ? JsonDocument.Parse(File.ReadAllText(versionPath)).RootElement.GetProperty("version").GetString()
            : null;
        _currentVersion = Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0, 0);
        CurrentVersionText.Text = $"Versión instalada: {(_currentVersion == new Version(0, 0, 0) ? "anterior (sin número)" : _currentVersion)}";
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var releases = await UpdateReleaseCatalog.GetAsync("pos-v", "local-pos-win-x64.zip");
            var available = releases.Where(item => Version.Parse(item.Version) > _currentVersion).ToArray();
            VersionsList.ItemsSource = available;
            _localArchive = null;
            StatusText.Text = available.Length == 0 ? "El POS ya está en la última versión publicada." :
                $"Hay {available.Length} versión(es) nueva(s). Selecciona una para leer los cambios.";
            VersionsList.SelectedIndex = available.Length > 0 ? 0 : -1;
        }
        catch (Exception ex) { StatusText.Text = $"No se pudieron consultar las versiones: {ex.Message}. Puedes usar un ZIP local."; }
        finally { SetBusy(false); }
    }

    private void Local_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar actualización oficial del POS", Filter = "Paquete POS (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _localArchive = dialog.FileName;
        VersionsList.SelectedIndex = -1;
        NotesText.Text = $"Archivo local: {Path.GetFileName(_localArchive)}\nSe comprobará la firma antes de cerrar el POS.";
        StatusText.Text = "Archivo seleccionado. Pulsa Preparar actualización.";
        InstallButton.IsEnabled = true;
    }

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionsList.SelectedItem is not UpdateRelease release)
        {
            InstallButton.IsEnabled = !_busy && _localArchive is not null;
            return;
        }
        _localArchive = null;
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes) ? "Esta versión no incluye una lista de cambios." : release.Notes;
        StatusText.Text = $"Versión {release.Version} · descarga aproximada {release.Size / 1024 / 1024} MB.";
        InstallButton.IsEnabled = !_busy;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var selected = VersionsList.SelectedItem as UpdateRelease;
        if (_localArchive is null && selected is null) return;
        SetBusy(true);
        var temporaryRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BalanzaPro", "Updates");
        var work = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(work, "update.zip");
        var extracted = Path.Combine(work, "package");
        try
        {
            Directory.CreateDirectory(work);
            StatusText.Text = selected is null ? "Leyendo archivo local…" : "Descargando versión oficial…";
            if (selected is null)
            {
                if (new FileInfo(_localArchive!).Length > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("El ZIP seleccionado supera el tamaño permitido.");
                await Task.Run(() => File.Copy(_localArchive!, archivePath));
            }
            else await UpdateReleaseCatalog.DownloadAsync(selected, archivePath, 2L * 1024 * 1024 * 1024);
            StatusText.Text = "Comprobando la firma y el contenido…";
            var keyPath = Path.Combine(_installationRoot, "trusted-publishers", "pos-publisher-vps-2026.pem");
            if (!File.Exists(keyPath)) throw new InvalidDataException("Esta instalación no contiene la clave pública de actualización. Instala primero un paquete reciente con Actualizar POS.cmd.");
            var key = File.ReadAllText(keyPath);
            var version = await Task.Run(() => PosUpdateArchiveVerifier.VerifyAndExtract(archivePath, key, extracted, selected?.Version));
            if (Version.Parse(version) <= _currentVersion)
                throw new InvalidDataException("El paquete seleccionado no es más reciente que la versión instalada.");
            File.Delete(archivePath);
            var answer = MessageBox.Show(this,
                $"La versión {version} está lista. El POS se cerrará, respaldará los datos en el servidor y se volverá a abrir al terminar. ¿Continuar?",
                "Instalar actualización", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            PreparedPackageDirectory = extracted;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"No se pudo preparar la actualización: {ex.Message}";
        }
        finally
        {
            if (PreparedPackageDirectory is null && Directory.Exists(work))
                try { Directory.Delete(work, true); } catch (Exception) { }
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        LocalButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && (_localArchive is not null || VersionsList.SelectedItem is UpdateRelease);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelCloseWhileBusy(object? sender, CancelEventArgs e) => e.Cancel = _busy && PreparedPackageDirectory is null;
}
