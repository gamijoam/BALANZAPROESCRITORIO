[CmdletBinding()]
param(
    [ValidatePattern('^(?:[01]\d|2[0-3]):[0-5]\d$')][string]$Time = '20:00',
    [ValidateRange(1, 3650)][int]$KeepLast = 14
)

$ErrorActionPreference = 'Stop'
$backupScript = Join-Path $PSScriptRoot 'Backup-LocalDatabase.ps1'
if (-not (Test-Path -LiteralPath $backupScript -PathType Leaf)) { throw 'No se encontró el script de respaldo local.' }

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $powershell -Argument "-NoProfile -File `"$backupScript`" -KeepLast $KeepLast"
$trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact($Time, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture))
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'Punto de venta - Respaldo diario' -Description 'Crea y verifica un respaldo PostgreSQL local del punto de venta.' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Write-Output "Respaldo diario programado para las $Time. Se ejecutará con el usuario de Windows actual cuando tenga una sesión iniciada; se conservarán los últimos $KeepLast respaldos."
