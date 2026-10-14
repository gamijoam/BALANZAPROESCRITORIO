# Diagnóstico de actualización: Servidor POS + caja local

Fecha: 2026-10-11

## Entorno revisado

- Paquete descargado y extraído en `C:\Users\Gamijoam\Desktop\Nueva carpeta (2)\Nueva carpeta`.
- Windows 10 Pro, Windows PowerShell 5.1 y runtimes .NET 10 presentes.
- Instalación local en `%LOCALAPPDATA%\Programs\Punto de Venta`.
- Base PostgreSQL `pos_dev`, rol de aplicación `pos_app`.

## Fallos reproducidos

1. El registro de `servidor-local.log` reportaba SQLSTATE `28P01`: PostgreSQL rechazaba la contraseña guardada de `pos_app`. El servicio no podía aplicar migraciones ni llegar al login.
2. `Actualizar POS.cmd` llegaba a `Backup-LocalDatabase.ps1` y fallaba en la comprobación SHA-256 con `Get-FileHash` no reconocido. La actualización se detenía en el respaldo, antes de parar el servicio o reemplazar la instalación.

## Cambios y comandos ejecutados

- Se ejecutó `scripts\Configure-LocalDatabase.ps1` del paquete. El script validó la cuenta administrativa de PostgreSQL y que `pos_dev` perteneciera a `pos_app`; al fallar la credencial de aplicación guardada, generó una nueva contraseña aleatoria para `pos_app` y guardó la conexión cifrada con DPAPI para el usuario actual de Windows.
- Se verificó la conexión de aplicación con `psql ... -c 'SELECT 1;'`; respondió correctamente.
- Se intentó `Actualizar POS.cmd`. El respaldo se detuvo por `Get-FileHash`; no se reemplazaron archivos.
- Se ejecutó `Install-LocalPos.ps1` desde Windows PowerShell 5.1 con `Microsoft.PowerShell.Utility` importado explícitamente. El respaldo se creó y verificó; se aplicó la migración `20261011224906_PairedTerminalCredentials`; la actualización instaló los archivos nuevos y abrió el asistente.
- Se añadieron `Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop` a `Backup-LocalDatabase.ps1` y `Restore-LocalDatabase.ps1` tanto en el paquete descargado como en la instalación. El respaldo manual posterior se verificó correctamente.

Respaldos verificados durante esta intervención:

- `Documentos\PuntoDeVenta\Respaldos\pos-pos_dev-20261011-194416.backup`
- `Documentos\PuntoDeVenta\Respaldos\pos-pos_dev-20261011-194735.backup`

No se incluyeron contraseñas, PIN ni cadenas de conexión en este informe. Los cambios a los scripts se hicieron en el paquete descargado y en la instalación local; no se modificó el código fuente del repositorio para aplicar el workaround.

## Correcciones recomendadas para el instalador fuente

1. Importar explícitamente `Microsoft.PowerShell.Utility` en los scripts que usan `Get-FileHash` (al menos respaldo y restauración), y comprobar errores de importación antes de continuar.
2. Probar `Actualizar POS.cmd` y los scripts de respaldo/restauración con Windows PowerShell 5.1, `-NoProfile` y `-ExecutionPolicy Bypass`, que es el host que invoca el `.cmd` en esta PC.
3. Agregar una verificación previa que confirme que la conexión `pos_app` funciona antes de iniciar `pg_dump`; mostrar el SQLSTATE o el mensaje útil sin revelar la contraseña.
4. Mantener la secuencia segura del actualizador: respaldo comprobado primero; solo después detener el servicio, migrar y sustituir archivos. Un error del respaldo debe dejar intacta la instalación.
5. Añadir un criterio de aceptación para probar el paquete extraído en una ruta con espacios y paréntesis, como `Nueva carpeta (2)`, ya que ese es el formato de la carpeta usada aquí.

## Estado al cerrar el diagnóstico

- La base acepta conexiones de `pos_app` y la migración del paquete está aplicada.
- El usuario `demo` existe con rol `Administrator` y está activo.
- La aplicación quedó abierta en **Configurar punto de venta**.
- Aún no existe el perfil DPAPI `deployment.dpapi`; por eso el modo Servidor no quedó configurado y no hay un servicio HTTPS activo en el puerto 5155.
- No se generó todavía el certificado PFX/CER ni se creó la regla de firewall.
