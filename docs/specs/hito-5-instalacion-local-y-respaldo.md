# Hito 5 — Instalación local y respaldo

> Esta guía describe el modo independiente de una sola PC. Para una instalación con servidor y terminales cliente por HTTPS, consulta `hito-5-modos-instalacion-y-multicaja.md`, que amplía el alcance del hito.

## Alcance de esta primera entrega

Preparar una instalación de servidor y punto de venta en una sola PC Windows. El paquete publica ambos ejecutables y el POS inicia o reutiliza el servicio local automáticamente antes de mostrar el login. La conexión se cifra con DPAPI en el perfil de Windows; al actualizar desde User Secrets se importa una sola vez. El paquete permite crear el primer operador por el endpoint de configuración limitado a loopback. El respaldo se genera con `pg_dump` en formato PostgreSQL comprimido y se puede programar diariamente; la restauración se hace en una base nueva y exige verificar el checksum antes de tocar PostgreSQL.

Esta entrega no instala PostgreSQL automáticamente, no abre puertos de red, no sincroniza cajas sin conexión y no afirma compatibilidad fiscal ni de periféricos. PostgreSQL debe estar instalado y activo. El primer inicio solicita los datos de conexión si todavía no existen y el servicio prepara el esquema automáticamente.

## Criterios de aceptación

- **Dado** el paquete publicado y una base PostgreSQL configurada, **cuando** se instala en el perfil actual y se inicia el POS, **entonces** el servidor escucha solamente en `127.0.0.1:5155` y el cliente abre la interfaz sin requerir permisos administrativos.
- **Dado** que no hay un servidor POS activo, **cuando** se abre el ejecutable del escritorio, **entonces** el cliente inicia el servidor empaquetado, espera a que la base esté lista y solo después presenta el acceso.
- **Dado** que el servidor ya está listo, **cuando** se abre otra instancia del POS, **entonces** reutiliza el servicio y no lanza un segundo proceso.
- **Dado** que PostgreSQL está detenido o la conexión es incorrecta, **cuando** se abre el POS, **entonces** muestra una explicación de conectividad/configuración y no el mensaje de credenciales incorrectas.
- **Dado** que todavía no existe configuración local, **cuando** se abre el POS por primera vez, **entonces** solicita los datos de PostgreSQL una vez y los protege con DPAPI para el usuario actual.
- **Dado** un primer arranque, **cuando** se configura el primer operador, **entonces** el endpoint temporal de configuración solo se expone en Development y loopback; el arranque normal usa Production.
- **Dado** una base configurada, **cuando** se solicita un respaldo, **entonces** se crea un archivo custom de `pg_dump`, se valida con `pg_restore --list`, se guarda su SHA-256 y no se imprime la contraseña.
- **Dado** una instalación local, **cuando** se activa la tarea diaria, **entonces** Windows ejecuta el respaldo con el usuario actual sin guardar su contraseña en el Programador de tareas.
- **Dado** un respaldo válido, **cuando** se restaura, **entonces** el script verifica el SHA-256 y crea una base de destino nueva; una base existente nunca se elimina ni se sobrescribe. Activar la restaurada requiere el modificador `-Activate`, confirmación escrita exacta y servicio detenido.
- **Dado** un respaldo dañado o credenciales inválidas, **cuando** falla el proceso, **entonces** no se declara éxito y se elimina únicamente la base nueva que ese mismo intento hubiera creado.
- **Dado** una actualización, **cuando** el respaldo falla por conectividad o autenticación, **entonces** se muestra el detalle seguro de PostgreSQL y el servidor POS no se detiene.

## Decisiones

- El paquete es framework-dependent: usa en la PC los runtimes .NET 10 `Microsoft.NETCore.App`, `Microsoft.AspNetCore.App` y `Microsoft.WindowsDesktop.App`. PostgreSQL continúa siendo externo y no se incluye su instalador.
- La cadena de conexión no se copia al paquete ni al repositorio. En la ejecución normal se guarda cifrada con DPAPI en `%LOCALAPPDATA%\PuntoDeVenta\Configuracion`; User Secrets solo se lee como migración de desarrollo y se convierte al almacenamiento cifrado.
- Los respaldos no se cifran con una contraseña propia de la aplicación. Deben guardarse en una ubicación protegida por Windows/BitLocker y copiarse periódicamente a otro dispositivo.
- Restaurar a una base distinta permite ensayar recuperación sin destruir la base en uso. El cambio de la aplicación a la base restaurada queda como operación manual y explícita.
- El modificador `-Activate` cambia solamente el nombre de base en User Secrets después de validar el historial restaurado; exige que el servicio esté cerrado y que el operador escriba una confirmación exacta.
- El escritorio inicia el servicio como proceso oculto enlazado exclusivamente a `127.0.0.1`, lo reutiliza si ya responde y conserva registros locales sin guardar ni imprimir la cadena de conexión. No abre acceso de red.

## Fuera de alcance

Instalación/desinstalación de PostgreSQL, restauración destructiva en sitio, copia remota/cloud, cifrado propio, rotación automática de respaldos, servicio Windows formal y emparejamiento de periféricos.

## Instalación y uso

1. En la PC con PostgreSQL instalado y activo, extrae `local-pos-win-x64.zip`.
2. Abre `Iniciar POS.vbs`. El inicio normal no muestra consola ni requiere ejecutar scripts manualmente.
3. Si es la primera ejecución y no hay una conexión heredada de User Secrets, completa una sola vez la ventana de conexión PostgreSQL. La configuración se guarda cifrada para el usuario actual.
4. El escritorio inicia o reutiliza el servidor local, espera a PostgreSQL y prepara las migraciones antes de mostrar el login. Si PostgreSQL no está disponible, inicia el servicio y elige **Sí** para volver a comprobarlo; no hace falta cerrar y abrir el POS.
5. Solo si la base todavía no tiene un operador inicial, usa el flujo de configuración del operador incluido en `scripts`. Para la instalación opcional en el perfil de Windows, usa `scripts\Install-LocalPos.ps1 -PackageDirectory .`; agrega `-ScheduleDailyBackup` para programar respaldos a las 20:00 y conservar los últimos 14.

## Actualización de una instalación existente

1. Extrae el paquete completo de la nueva versión en una carpeta temporal, distinta de la instalación.
2. Cierra todas las ventanas del POS. El actualizador se niega a continuar mientras exista una ventana abierta para no interrumpir una venta.
3. Ejecuta `Actualizar POS.cmd` desde la carpeta extraída y con la misma cuenta de Windows que usa el POS. La política de ejecución solo se omite para ese proceso de PowerShell; no cambia la política de Windows.
4. El actualizador valida que el escritorio esté cerrado, genera y verifica un respaldo de PostgreSQL mientras el servicio sigue disponible, detiene el servicio POS, prepara los archivos nuevos en una carpeta temporal y ejecuta las migraciones del servidor nuevo.
5. Si la migración termina correctamente, cambia la instalación a la carpeta nueva, conserva la versión anterior como `.previous`, actualiza los accesos directos y abre el POS nuevo. El servidor confirma la conexión y el esquema antes de mostrar el login.

Si la aplicación está abierta, el puerto lo ocupa otro programa o falla el respaldo/la migración, el actualizador se detiene y muestra el motivo. Si el respaldo falla, el servicio no se detiene. No termina procesos que no se identifiquen como `Pos.Server.exe` respondiendo con la identidad esperada del POS. La configuración cifrada de conexión y los respaldos viven fuera de la carpeta de instalación y no se reemplazan al actualizar. La actualización no cambia contraseñas PostgreSQL. Conserva el paquete anterior hasta comprobar el inicio de sesión y el flujo de ventas.

El respaldo manual se ejecuta con `Backup-LocalDatabase.ps1`. La tarea programada corre con el usuario actual y necesita que su sesión de Windows esté iniciada. Los respaldos quedan en Documentos, no se cifran por la aplicación y se deben copiar a otro dispositivo.

La restauración siempre exige una base de destino que no exista. Sin `-Activate`, restaura y valida dejando intacta la conexión actual. Para cambiar el POS a la base recuperada, cierra el servicio y usa `-Activate`; el script solicita escribir `ACTIVAR <nombre>`. La base anterior no se borra.
