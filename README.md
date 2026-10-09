# POS de escritorio para comercios venezolanos

Proyecto de POS para Windows, diseñado alrededor de la pantalla de venta. La arquitectura aprobada inicialmente usa WPF como cliente de un servicio .NET y PostgreSQL como base de datos tanto en una instalación de una PC como en una instalación multicaja.

## Estado

El **Hito 0 está aprobado**; los hitos 1 a 3 entregaron el dominio inicial, persistencia PostgreSQL/API y pantalla de venta. El **Hito 4 incorpora inventario, caja y arqueo, productos y precios, tasa de cambio, clientes y fiado/abonos, reportes, auditoría y permisos por rol**. El primer tramo del **Hito 5** prepara una instalación local y respaldo/restauración PostgreSQL. El comprobante sigue siendo de prueba y no fiscal.

## Requisitos de desarrollo

- Windows x64 y .NET 10 SDK.
- PostgreSQL local para los hitos de persistencia e integración.
- Docker Desktop cuando se incorporen pruebas Testcontainers.
- Git.

Visual Studio es opcional: el proyecto puede compilarse desde la CLI de .NET. Docker no es un requisito de instalación para el comercio.

## Documentación

- [ADR-001 Arquitectura](docs/adr/ADR-001-arquitectura.md)
- [Alcance del Hito 0](docs/specs/hito-0-diseno-verificable.md)
- [Matriz de permisos](docs/specs/matriz-permisos.md)
- [Contrato inicial de API](docs/specs/contrato-api-v1.md)
- [Modelo conceptual de datos](docs/specs/modelo-datos-inicial.md)
- [Riesgos y decisiones pendientes](docs/specs/riesgos-y-decisiones.md)
- [Reglas del dominio del Hito 1](docs/specs/hito-1-reglas-dominio.md)
- [Persistencia PostgreSQL del Hito 2](docs/specs/hito-2-persistencia.md)
- [POS de escritorio del Hito 3](docs/specs/hito-3-pos-escritorio.md)
- [Funciones operativas del Hito 4](docs/specs/hito-4-funciones-operativas.md)
- [Instalación local y respaldo del Hito 5](docs/specs/hito-5-instalacion-local-y-respaldo.md)

## Compilar

```powershell
dotnet restore .\Pos.sln
dotnet build .\Pos.sln --no-restore
dotnet test .\Pos.sln --no-build
```

## Iniciar el servicio local

```powershell
dotnet run --project .\src\Pos.Server\Pos.Server.csproj
```

En otra consola, crea el primer operador (solo se permite en Development y desde localhost):

```powershell
.\scripts\Configure-InitialOperator.ps1
```

Este script solicita el PIN sin mostrarlo. El cliente puede iniciar sesión contra `http://localhost:5155/api/v1/auth/login`.

## Instalar en una sola PC

Con PostgreSQL instalado y los runtimes .NET 10 requeridos, publica el paquete para Windows x64:

```powershell
.\scripts\Publish-LocalPos.ps1
.\artifacts\local-pos\scripts\Configure-LocalDatabase.ps1
.\scripts\Install-LocalPos.ps1 -ScheduleDailyBackup
```

La configuración solicita la contraseña administrativa de PostgreSQL y guarda una contraseña de aplicación aleatoria en la configuración segura del usuario de Windows. La instalación se guarda en el perfil actual y requiere los runtimes .NET 10 de escritorio, ASP.NET Core y .NET; crea accesos directos para iniciar el servidor enlazado solo a `127.0.0.1` y el POS. Para crear el primer operador, ejecuta una vez:

```powershell
& "$env:LOCALAPPDATA\Programs\Punto de Venta\scripts\Initialize-LocalPosOperator.ps1"
```

El paquete no contiene PostgreSQL, datos del comercio ni secretos. La configuración inicial no abre puertos LAN.

## Respaldo y restauración

El respaldo manual crea un archivo comprimido de PostgreSQL en Documentos y verifica su integridad y SHA-256:

```powershell
& "$env:LOCALAPPDATA\Programs\Punto de Venta\scripts\Backup-LocalDatabase.ps1" -KeepLast 14
```

Con `-ScheduleDailyBackup`, la instalación configura respaldos diarios a las 20:00. Windows los ejecuta con el usuario que guarda la conexión segura, cuando ese usuario ha iniciado sesión. Los archivos no llevan cifrado propio: protégelos con BitLocker y conserva copias en otro dispositivo.

Para ensayar la restauración sin sobrescribir la base activa, usa un nombre de base nuevo. El script verifica el checksum y solicita la contraseña administrativa de PostgreSQL:

```powershell
& "$env:LOCALAPPDATA\Programs\Punto de Venta\scripts\Restore-LocalDatabase.ps1" `
  -BackupFile "$env:USERPROFILE\Documents\PuntoDeVenta\Respaldos\pos-pos_dev-AAAAmmdd-HHmmss.backup" `
  -TargetDatabaseName pos_recuperacion
```

La restauración deja intacta la base original y no cambia automáticamente la conexión activa del POS. Si ya se revisó la base recuperada y se quiere usarla, cierra el servicio, agrega `-Activate` y confirma escribiendo `ACTIVAR pos_recuperacion`.

Prueba de mutación del dominio, ejecutada desde el directorio de pruebas:

```powershell
Push-Location .\tests\Pos.Domain.Tests
dotnet tool restore
dotnet tool run dotnet-stryker
Pop-Location
```

Las pruebas de integración ejercitan PostgreSQL real. El script `scripts/Test-LocalCheckout.ps1` verifica el flujo API del Hito 4, incluyendo permisos, inventario, arqueo, precios/tasa, clientes/fiado, reportes y auditoría; limpia los datos temporales al terminar. Las reglas están en `docs/specs/hito-1-reglas-dominio.md` y `docs/specs/hito-4-funciones-operativas.md`.
