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
            var progress = new Progress<UpdateDownloadProgress>(value => ShowTransferProgress(
                selected is null ? "Copiando paquete local" : "Descargando actualización", value));
            UpdateProgressBar.Visibility = Visibility.Visible;
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 0;
            if (selected is null)
            {
                var localFile = new FileInfo(_localArchive!);
                if (localFile.Length > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("El ZIP seleccionado supera el tamaño permitido.");
                await CopyLocalArchiveAsync(_localArchive!, archivePath, localFile.Length, progress);
            }
            else await UpdateReleaseCatalog.DownloadAsync(selected, archivePath, 2L * 1024 * 1024 * 1024, progress: progress);
            UpdateProgressBar.IsIndeterminate = true;
            StatusText.Text = "Comprobando la firma y el contenido…";
            var keyPath = Path.Combine(_installationRoot, "trusted-publishers", "pos-publisher-vps-2026.pem");
            if (!File.Exists(keyPath)) throw new InvalidDataException("Esta instalación no contiene la clave pública de actualización. Instala primero un paquete reciente con Actualizar POS.cmd.");
            var key = File.ReadAllText(keyPath);
            var version = await Task.Run(() => PosUpdateArchiveVerifier.VerifyAndExtract(archivePath, key, extracted, selected?.Version));
            if (Version.Parse(version) <= _currentVersion)
                throw new InvalidDataException("El paquete seleccionado no es más reciente que la versión instalada.");
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 100;
            StatusText.Text = $"Versión {version} descargada y verificada. Preparando la instalación…";
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
            UpdateProgressBar.IsIndeterminate = false;
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

    private void ShowTransferProgress(string action, UpdateDownloadProgress progress)
    {
        if (progress.TotalBytes <= 0) return;
        var percentage = Math.Clamp(progress.BytesReceived * 100d / progress.TotalBytes, 0d, 100d);
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = percentage;
        StatusText.Text = $"{action}: {percentage:0}% ({FormatBytes(progress.BytesReceived)} de {FormatBytes(progress.TotalBytes)}).";
    }

    private static async Task CopyLocalArchiveAsync(string source, string destination, long expectedBytes,
        IProgress<UpdateDownloadProgress> progress, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += count;
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("El ZIP seleccionado supera el tamaño permitido.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            progress.Report(new UpdateDownloadProgress(total, expectedBytes));
        }
        if (total != expectedBytes) throw new InvalidDataException("La copia del ZIP local quedó incompleta.");
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024d / 1024d:0.0} GB" : $"{bytes / 1024d / 1024d:0.0} MB";

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelCloseWhileBusy(object? sender, CancelEventArgs e) => e.Cancel = _busy && PreparedPackageDirectory is null;
}
