[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][int]$DesktopProcessId,
    [switch]$Worker,
    [string]$ProgressFile
)

$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$updatesRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'BalanzaPro\Updates')).TrimEnd('\') + '\'
if (-not $package.StartsWith($updatesRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El paquete temporal está fuera de la carpeta de actualizaciones del POS.'
}
$installer = Join-Path $package 'scripts\Install-LocalPos.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'El paquete descargado no contiene el instalador del POS.'
}

function Write-UpdateProgress {
    param([Parameter(Mandatory)][string]$Message, [string]$State = 'installing', [string]$ErrorMessage)
    if ([string]::IsNullOrWhiteSpace($ProgressFile)) { return }
    $payload = [ordered]@{ state = $State; message = $Message; error = $ErrorMessage; updatedAtUtc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json -Compress
    $temporary = "$ProgressFile.tmp"
    [IO.File]::WriteAllText($temporary, $payload, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $ProgressFile -Force
}

if (-not $Worker) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $progressDirectory = Join-Path $env:LOCALAPPDATA 'BalanzaPro\Updates'
    New-Item -ItemType Directory -Path $progressDirectory -Force | Out-Null
    $ProgressFile = Join-Path $progressDirectory ("install-{0}.json" -f [Guid]::NewGuid().ToString('N'))
    $scriptPath = [IO.Path]::GetFullPath($PSCommandPath)
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -PackageDirectory "{1}" -DesktopProcessId {2} -Worker -ProgressFile "{3}"' -f `
        $scriptPath, $package, $DesktopProcessId, $ProgressFile
    $workerProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -PassThru -WindowStyle Hidden

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Actualizando BalanzaPro'
    $form.StartPosition = 'CenterScreen'
    $form.ClientSize = [System.Drawing.Size]::new(560, 190)
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ControlBox = $false
    $form.BackColor = [System.Drawing.Color]::FromArgb(242, 246, 251)

    $title = New-Object System.Windows.Forms.Label
    $title.Text = 'Instalando actualización del POS'
    $title.Font = [System.Drawing.Font]::new('Segoe UI', [single]15, [System.Drawing.FontStyle]::Bold)
    $title.ForeColor = [System.Drawing.Color]::FromArgb(16, 45, 80)
    $title.Location = [System.Drawing.Point]::new(24, 20)
    $title.Size = [System.Drawing.Size]::new(510, 32)
    $form.Controls.Add($title)

    $message = New-Object System.Windows.Forms.Label
    $message.Text = 'Preparando la instalación…'
    $message.Font = [System.Drawing.Font]::new('Segoe UI', [single]10)
    $message.ForeColor = [System.Drawing.Color]::FromArgb(52, 75, 103)
    $message.Location = [System.Drawing.Point]::new(26, 62)
    $message.Size = [System.Drawing.Size]::new(510, 28)
    $form.Controls.Add($message)

    $progress = New-Object System.Windows.Forms.ProgressBar
    $progress.Location = [System.Drawing.Point]::new(26, 100)
    $progress.Size = [System.Drawing.Size]::new(508, 22)
    $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
    $progress.MarqueeAnimationSpeed = 25
    $form.Controls.Add($progress)

    $closeButton = New-Object System.Windows.Forms.Button
    $closeButton.Text = 'Cerrar'
    $closeButton.Location = [System.Drawing.Point]::new(434, 142)
    $closeButton.Size = [System.Drawing.Size]::new(100, 30)
    $closeButton.Enabled = $false
    $closeButton.Add_Click({ $form.Close() })
    $form.Controls.Add($closeButton)

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 350
    $timer.Add_Tick({
        try {
            if (Test-Path -LiteralPath $ProgressFile -PathType Leaf) {
                $snapshot = Get-Content -LiteralPath $ProgressFile -Raw | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace([string]$snapshot.message)) { $message.Text = [string]$snapshot.message }
                if ($snapshot.state -eq 'completed') {
                    $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Continuous
                    $progress.Value = 100
                    $message.ForeColor = [System.Drawing.Color]::FromArgb(20, 120, 68)
                    $workerProcess.Refresh()
                    if ($workerProcess.HasExited) { $timer.Stop(); $closeButton.Enabled = $true }
                } elseif ($snapshot.state -eq 'failed') {
                    $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Blocks
                    $progress.Value = 0
                    $message.ForeColor = [System.Drawing.Color]::FromArgb(180, 35, 35)
                    $message.Text = 'No se pudo completar la actualización. ' + [string]$snapshot.error
                    $workerProcess.Refresh()
                    if ($workerProcess.HasExited) { $timer.Stop(); $closeButton.Enabled = $true }
                }
            }
            $workerProcess.Refresh()
            if ($workerProcess.HasExited -and $closeButton.Enabled -eq $false) {
                $timer.Stop()
                $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Blocks
                $progress.Value = 0
                $message.ForeColor = [System.Drawing.Color]::FromArgb(180, 35, 35)
                $message.Text = "El instalador terminó inesperadamente (código $($workerProcess.ExitCode))."
                $closeButton.Enabled = $true
            }
        } catch {
            # Keep the window responsive if a progress snapshot is being replaced.
        }
    })
    $form.Add_Shown({ $timer.Start() })
    $form.Add_FormClosed({
        $timer.Stop()
        $timer.Dispose()
        if (Test-Path -LiteralPath $ProgressFile) { Remove-Item -LiteralPath $ProgressFile -Force -ErrorAction SilentlyContinue }
    })
    [void]$form.ShowDialog()
    exit $workerProcess.ExitCode
}

try {
    Write-UpdateProgress 'Esperando el cierre del punto de venta…'
    $desktopProcess = Get-Process -Id $DesktopProcessId -ErrorAction SilentlyContinue
    if ($null -ne $desktopProcess) {
        $desktopProcess.WaitForExit(120000) | Out-Null
        if (-not $desktopProcess.HasExited) {
            throw 'El POS sigue abierto. Cierra las ventanas del programa y vuelve a intentar la actualización.'
        }
    }
    $env:BALANZAPRO_POS_UPDATE_PROGRESS_FILE = $ProgressFile
    & $installer -PackageDirectory $package -ProgressFile $ProgressFile
    if (-not $?) { throw 'El instalador no completó la actualización.' }
    $downloadDirectory = Split-Path -Parent $package
    if ([IO.Path]::GetFileName($package) -ieq 'package' -and
        [IO.Path]::GetFileName($downloadDirectory) -match '^[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $downloadDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-UpdateProgress 'Actualización instalada correctamente. El punto de venta se está iniciando…' -State 'completed'
} catch {
    $detail = $_.Exception.Message
    try { Write-UpdateProgress "No se pudo completar la actualización." -State 'failed' -ErrorMessage $detail } catch { }
    [Console]::Error.WriteLine("No se pudo actualizar el POS: $detail")
    [Console]::Error.WriteLine("El paquete temporal sigue disponible en '$package' para revisar el problema.")
    exit 1
}
