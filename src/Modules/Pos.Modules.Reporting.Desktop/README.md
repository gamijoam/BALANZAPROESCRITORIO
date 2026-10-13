# Reportes avanzados

Este módulo agrega un visor de reportes WPF al POS anfitrión. Usa los endpoints HTTP del servicio para ventas, caja e inventario, y acepta fuentes de reporte declaradas por otros módulos. El escritorio no se conecta directamente a PostgreSQL.

## Cambios de versión

- **1.0.1:** evita que la selección inicial de moneda filtre la tabla antes de que WPF termine de construir la pantalla. No requiere migraciones ni una actualización del POS anfitrión.
- **1.0.2:** elimina el panel lateral, da el ancho completo a la tabla y traslada las columnas visibles a un botón emergente junto a las vistas personales. Abre el visor maximizado. No requiere migraciones ni actualización del POS anfitrión.

## Construir el paquete firmado

Desde PowerShell en la raíz del repositorio:

    .\scripts\Build-ReportModulePackage.ps1 -CertificateThumbprint "<huella-certificado>"

El certificado debe estar instalado en Cert:\CurrentUser\My y contar con su clave privada. El ZIP firmado y su clave pública salen en artifacts\modules\advanced-reports\ (el ZIP contiene la versión configurada; la versión actual del módulo es 1.0.2).

Antes de instalar, aplica la migración del anfitrión OperatorReportViews y actualiza el POS a Desktop API 1.2. El visor requiere el permiso `Reports.View` (mapeado al permiso de operador `ViewReports`); el catálogo de inventario requiere `Reports.Inventory` (mapeado a `ViewInventory`). Las fuentes del módulo Almacenes siguen requiriendo `Warehouse.View` o `Warehouse.Transfer`.

## Cómo agregar una fuente de reporte

Un módulo de escritorio puede llamar PosDesktopModuleRegistry.RegisterReportSource durante IPosDesktopModule.Configure. Debe declarar un ID estable, título, columnas tipadas y el permiso del manifiesto. Su delegado consulta datos solo a través de PosDesktopModuleContext.ApiClient o ReportApiClient; nunca debe construir SQL ni acceder a la base de datos.

El visor muestra únicamente fuentes de módulos activos y permisos autorizados. IDs duplicados se rechazan en la terminal. Una fuente puede indicar si utiliza un período de fechas.

## Vistas personales

Las vistas guardadas almacenan únicamente visibilidad/orden de columnas y filtros. El servidor las asocia al operador autenticado; los nombres y configuraciones se comparten entre sus terminales. No guardes consultas, SQL o datos de resultados en la definición.

## Verificaciones

- El registro y la proyección de columnas tienen pruebas en tests/Pos.Application.Tests/AdvancedReportModuleContractTests.cs.
- El modelo/migración de vistas personales tiene prueba en tests/Pos.Integration.Tests/TerminalClientVersionReportingTests.cs.
- El exportador XLSX escribe Open XML directamente y no necesita que Excel esté instalado.
