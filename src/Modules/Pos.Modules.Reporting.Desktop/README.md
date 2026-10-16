# Reportes avanzados

Este módulo agrega un visor de reportes WPF al POS anfitrión. Usa los endpoints HTTP del servicio para ventas, caja e inventario, y acepta fuentes de reporte declaradas por otros módulos. El escritorio no se conecta directamente a PostgreSQL.

## Cambios de versión

- **1.0.1:** evita que la selección inicial de moneda filtre la tabla antes de que WPF termine de construir la pantalla. No requiere migraciones ni una actualización del POS anfitrión.
- **1.0.2:** elimina el panel lateral, da el ancho completo a la tabla y traslada las columnas visibles a un botón emergente junto a las vistas personales. Abre el visor maximizado. No requiere migraciones ni actualización del POS anfitrión.
- **1.1.0:** agrega saldos y movimientos paginados de cuentas por cobrar, y cobros/reintegros agrupados por caja, método y moneda. Usa `ViewReports`, requiere Desktop API 1.4 y no agrega migraciones.
- **1.2.0:** agrega una vista paginada tipo hoja que respeta filtros, filas, orden y columnas visibles, con papel Carta/Oficio/A4 y orientación horizontal/vertical; permite imprimirla o guardarla como PDF mediante Microsoft Print to PDF en Windows. También incorpora el reporte de precios y márgenes actuales por nivel, calculado con el costo vigente cuando está registrado. No requiere migraciones ni actualización del POS anfitrión.

## Publicar el paquete firmado

Después de fusionar los cambios a `main`, el firmador centralizado genera el ZIP desde GitHub Actions:

1. Abre **Actions → Publicar módulo firmado → Run workflow**.
2. Selecciona `main` y `advanced-reports`.
3. Descarga el ZIP firmado del artifact `signed-module-output`.

El runner de firma del VPS guarda la clave privada fuera de GitHub y del equipo Windows. No se requiere copiar esa clave al repositorio.

Antes de instalar, actualiza el POS anfitrión a Desktop API 1.4. No hay migración nueva; las vistas personales siguen necesitando que esté aplicada `OperatorReportViews`. El visor y las tres nuevas fuentes requieren `Reports.View` (mapeado al permiso de operador `ViewReports`); el catálogo de inventario requiere `Reports.Inventory` (mapeado a `ViewInventory`). Las fuentes del módulo Almacenes siguen requiriendo `Warehouse.View` o `Warehouse.Transfer`.

Los cambios visibles al operador y los pasos de actualización están en [`docs/manuales/REPORTES-AVANZADOS-1.2.0.md`](../../../docs/manuales/REPORTES-AVANZADOS-1.2.0.md).

## Cómo agregar una fuente de reporte

Un módulo de escritorio puede llamar PosDesktopModuleRegistry.RegisterReportSource durante IPosDesktopModule.Configure. Debe declarar un ID estable, título, columnas tipadas y el permiso del manifiesto. Su delegado consulta datos solo a través de PosDesktopModuleContext.ApiClient o ReportApiClient; nunca debe construir SQL ni acceder a la base de datos.

El visor muestra únicamente fuentes de módulos activos y permisos autorizados. IDs duplicados se rechazan en la terminal. Una fuente puede indicar si utiliza un período de fechas.

## Vistas personales

Las vistas guardadas almacenan únicamente visibilidad/orden de columnas y filtros. El servidor las asocia al operador autenticado; los nombres y configuraciones se comparten entre sus terminales. No guardes consultas, SQL o datos de resultados en la definición.

## Verificaciones

- El registro y la proyección de columnas tienen pruebas en tests/Pos.Application.Tests/AdvancedReportModuleContractTests.cs.
- El modelo/migración de vistas personales tiene prueba en tests/Pos.Integration.Tests/TerminalClientVersionReportingTests.cs.
- El exportador XLSX escribe Open XML directamente y no necesita que Excel esté instalado.
