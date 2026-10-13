[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Get-ListeningProcessIds {
    param([Parameter(Mandatory)][int]$Port)
    @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
}

function Confirm-StopAction {
    param([Parameter(Mandatory)][string]$Prompt)
    (Read-Host $Prompt) -match '^(s|si|sí)$'
}

function Stop-DesktopProcesses {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'Pos.Desktop.exe'" -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        Write-Host 'No hay procesos Pos.Desktop.exe activos.' -ForegroundColor Green
        return $true
    }

    Write-Host "`nProcesos de escritorio POS (no son el servidor ni un puerto TCP):" -ForegroundColor Cyan
    $processes | Select-Object ProcessId, Name, ExecutablePath | Format-Table -AutoSize
    if (-not (Confirm-StopAction '¿Cerrar los procesos Pos.Desktop.exe mostrados? Escribe S para confirmar')) {
        Write-Host 'No se detuvo ningún proceso.' -ForegroundColor Yellow
        return $false
    }

    foreach ($process in $processes) {
        try {
            Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop
            Write-Host "Cerrado: $($process.Name), PID $($process.ProcessId)." -ForegroundColor Green
        } catch {
            Write-Host "No se pudo cerrar PID $($process.ProcessId): $($_.Exception.Message). Si el proceso pertenece a otro usuario, ejecuta esta herramienta como administrador." -ForegroundColor Red
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $remaining = @(Get-CimInstance Win32_Process -Filter "Name = 'Pos.Desktop.exe'" -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            Write-Host 'Verificado: no quedan procesos Pos.Desktop.exe.' -ForegroundColor Green
            return $true
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    $remaining | Select-Object ProcessId, Name, ExecutablePath | Format-Table -AutoSize
    Write-Host 'Pos.Desktop.exe sigue activo o se volvió a iniciar. Revisa permisos o el programa que lo relanza.' -ForegroundColor Red
    return $false
}

function Stop-PortProcesses {
    $port = 0
    while ($true) {
        $portInput = Read-Host 'Escribe el puerto TCP que quieres liberar (1-65535)'
        if ([int]::TryParse($portInput, [ref]$port) -and $port -ge 1 -and $port -le 65535) { break }
        Write-Host 'Puerto inválido. Escribe un número entre 1 y 65535.' -ForegroundColor Yellow
    }

    $listenerIds = @(Get-ListeningProcessIds -Port $port)
    if ($listenerIds.Count -eq 0) {
        Write-Host "No hay procesos escuchando en el puerto TCP ${port}." -ForegroundColor Green
        return $true
    }

    $owners = foreach ($processId in $listenerIds) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
        $service = Get-CimInstance Win32_Service -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue |
            Where-Object { $_.State -eq 'Running' } | Select-Object -First 1
        [pscustomobject]@{
            ProcessId = [int]$processId
            Name = if ($process) { $process.Name } else { '(proceso no identificado)' }
            ExecutablePath = if ($process) { $process.ExecutablePath } else { '' }
            ServiceName = if ($service) { $service.Name } else { '' }
            ServiceDisplayName = if ($service) { $service.DisplayName } else { '' }
        }
    }

    Write-Host "`nProceso(s) que ocupan el puerto TCP ${port}:" -ForegroundColor Cyan
    $owners | Format-Table ProcessId, Name, ServiceName, ExecutablePath -AutoSize
    Write-Host 'Esta opción detiene el servidor/proceso que escucha en el puerto; no cierra Pos.Desktop.exe.' -ForegroundColor DarkYellow
    if (-not (Confirm-StopAction '¿Detener el/los proceso(s) mostrado(s)? Escribe S para confirmar')) {
        Write-Host 'No se detuvo ningún proceso.' -ForegroundColor Yellow
        return $false
    }

    foreach ($owner in $owners) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($owner.ServiceName)) {
                Stop-Service -Name $owner.ServiceName -Force -ErrorAction Stop
                Write-Host "Detenido servicio: $($owner.ServiceDisplayName) [$($owner.ServiceName)]." -ForegroundColor Green
            } else {
                Stop-Process -Id $owner.ProcessId -Force -ErrorAction Stop
                Write-Host "Detenido: $($owner.Name), PID $($owner.ProcessId)." -ForegroundColor Green
            }
        } catch {
            Write-Host "No se pudo detener PID $($owner.ProcessId): $($_.Exception.Message). Si pertenece a otro usuario o a un servicio protegido, ejecuta esta herramienta como administrador." -ForegroundColor Red
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $remaining = @(Get-ListeningProcessIds -Port $port)
        if ($remaining.Count -eq 0) {
            Write-Host "Puerto TCP $port liberado y verificado." -ForegroundColor Green
            return $true
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    $remainingOwners = foreach ($processId in $remaining) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
        [pscustomobject]@{ ProcessId = [int]$processId; Name = $process.Name; ExecutablePath = $process.ExecutablePath }
    }
    Write-Host 'El puerto sigue ocupado; el proceso pudo reiniciarse automáticamente o no tienes permisos:' -ForegroundColor Red
    $remainingOwners | Format-Table ProcessId, Name, ExecutablePath -AutoSize
    return $false
}

Write-Host 'DETENER PROCESOS DEL POS' -ForegroundColor Cyan
Write-Host '1. Liberar un puerto TCP (servidor/proceso que escucha).'
Write-Host '2. Cerrar Pos.Desktop.exe (aplicación de escritorio, aunque no tenga ventana visible).'
Write-Host 'El actualizador del POS gestiona su propio servidor después de validar el respaldo.'
$choice = Read-Host 'Elige 1 o 2'
switch ($choice) {
    '1' { if (-not (Stop-PortProcesses)) { exit 1 } }
    '2' { if (-not (Stop-DesktopProcesses)) { exit 1 } }
    default { Write-Host 'Opción inválida. No se detuvo ningún proceso.' -ForegroundColor Yellow; exit 2 }
}
