# Hito 5 — Instalación local y respaldo

## Alcance de esta primera entrega

Preparar una instalación de servidor y punto de venta en una sola PC Windows. El paquete publica ambos ejecutables, deja accesos directos para iniciar el servidor local y el POS, y permite crear el primer operador por el endpoint de configuración limitado a loopback. El respaldo se genera con `pg_dump` en formato PostgreSQL comprimido y se puede programar diariamente; la restauración se hace en una base nueva y exige verificar el checksum antes de tocar PostgreSQL.

Esta entrega no instala PostgreSQL automáticamente, no abre puertos de red, no sincroniza cajas sin conexión y no afirma compatibilidad fiscal ni de periféricos. PostgreSQL debe estar instalado y la base configurada con `Configure-LocalDatabase.ps1`.

## Criterios de aceptación

- **Dado** el paquete publicado y una base PostgreSQL configurada, **cuando** se instala en el perfil actual y se inicia el POS, **entonces** el servidor escucha solamente en `127.0.0.1:5155` y el cliente abre la interfaz sin requerir permisos administrativos.
- **Dado** un primer arranque, **cuando** se configura el primer operador, **entonces** el endpoint temporal de configuración solo se expone en Development y loopback; el arranque normal usa Production.
- **Dado** una base configurada, **cuando** se solicita un respaldo, **entonces** se crea un archivo custom de `pg_dump`, se valida con `pg_restore --list`, se guarda su SHA-256 y no se imprime la contraseña.
- **Dado** una instalación local, **cuando** se activa la tarea diaria, **entonces** Windows ejecuta el respaldo con el usuario actual sin guardar su contraseña en el Programador de tareas.
- **Dado** un respaldo válido, **cuando** se restaura, **entonces** el script verifica el SHA-256 y crea una base de destino nueva; una base existente nunca se elimina ni se sobrescribe. Activar la restaurada requiere el modificador `-Activate`, confirmación escrita exacta y servicio detenido.
- **Dado** un respaldo dañado o credenciales inválidas, **cuando** falla el proceso, **entonces** no se declara éxito y se elimina únicamente la base nueva que ese mismo intento hubiera creado.

## Decisiones

- El paquete es framework-dependent: usa en la PC los runtimes .NET 10 `Microsoft.NETCore.App`, `Microsoft.AspNetCore.App` y `Microsoft.WindowsDesktop.App`. PostgreSQL continúa siendo externo y no se incluye su instalador.
- La cadena de conexión permanece en .NET User Secrets del usuario de Windows que ejecuta el lanzador; no se copia al paquete ni al repositorio.
- Los respaldos no se cifran con una contraseña propia de la aplicación. Deben guardarse en una ubicación protegida por Windows/BitLocker y copiarse periódicamente a otro dispositivo.
- Restaurar a una base distinta permite ensayar recuperación sin destruir la base en uso. El cambio de la aplicación a la base restaurada queda como operación manual y explícita.
- El modificador `-Activate` cambia solamente el nombre de base en User Secrets después de validar el historial restaurado; exige que el servicio esté cerrado y que el operador escriba una confirmación exacta.
- El lanzador inicia el servicio como proceso oculto enlazado exclusivamente a `127.0.0.1`; no abre acceso de red.

## Fuera de alcance

Instalación/desinstalación de PostgreSQL, restauración destructiva en sitio, copia remota/cloud, cifrado propio, rotación automática de respaldos, servicio Windows formal, multicaja LAN y emparejamiento de periféricos.

## Instalación y uso

1. En la PC con PostgreSQL instalado y activo, extrae `local-pos-win-x64.zip`.
2. Desde PowerShell con el usuario de Windows que operará el POS, ejecuta `scripts\Configure-LocalDatabase.ps1`. Introduce la contraseña administrativa de PostgreSQL cuando la solicite.
3. Ejecuta `scripts\Install-LocalPos.ps1 -PackageDirectory .` para instalar en el perfil actual y preparar las migraciones. Agrega `-ScheduleDailyBackup` para programar respaldos a las 20:00 y conservar los últimos 14.
4. Ejecuta una vez `"$env:LOCALAPPDATA\Programs\Punto de Venta\scripts\Initialize-LocalPosOperator.ps1"` para crear el usuario inicial.
5. Abre el acceso directo **Punto de venta**.

El respaldo manual se ejecuta con `Backup-LocalDatabase.ps1`. La tarea programada corre con el usuario actual y necesita que su sesión de Windows esté iniciada. Los respaldos quedan en Documentos, no se cifran por la aplicación y se deben copiar a otro dispositivo.

La restauración siempre exige una base de destino que no exista. Sin `-Activate`, restaura y valida dejando intacta la conexión actual. Para cambiar el POS a la base recuperada, cierra el servicio y usa `-Activate`; el script solicita escribir `ACTIVAR <nombre>`. La base anterior no se borra.
