# Reportes avanzados

Este módulo agrega un visor de reportes WPF al POS anfitrión. Usa los endpoints HTTP del servicio para ventas, caja e inventario, y acepta fuentes de reporte declaradas por otros módulos. El escritorio no se conecta directamente a PostgreSQL.

## Cambios de versión

- **1.0.1:** evita que la selección inicial de moneda filtre la tabla antes de que WPF termine de construir la pantalla. No requiere migraciones ni una actualización del POS anfitrión.
- **1.0.2:** elimina el panel lateral, da el ancho completo a la tabla y traslada las columnas visibles a un botón emergente junto a las vistas personales. Abre el visor maximizado. No requiere migraciones ni actualización del POS anfitrión.
- **1.1.0:** agrega saldos y movimientos paginados de cuentas por cobrar, y cobros/reintegros agrupados por caja, método y moneda. Usa `ViewReports`, requiere Desktop API 1.4 y no agrega migraciones.
- **1.2.0:** agrega una vista paginada tipo hoja que respeta filtros, filas, orden y columnas visibles, con papel Carta/Oficio/A4 y orientación horizontal/vertical; permite imprimirla o guardarla como PDF mediante Microsoft Print to PDF en Windows. También incorpora el reporte de precios y márgenes actuales por nivel, calculado con el costo vigente cuando está registrado. No requiere migraciones ni actualización del POS anfitrión.
- **1.3.0:** incorpora un diseñador de hojas con encabezado, columnas configurables, agrupaciones, sumatorias, estilos del comercio y plantillas personales. Requiere Desktop API 1.5 y la migración `ReportSheetTemplates`; el guardado usa la base local del POS y funciona sin Internet.
- **1.4.0:** agrega resúmenes automáticos por categoría, calculados con los resultados filtrados y con opción para incluirlos en las hojas. Mantiene el requisito de Desktop API 1.5; no agrega migraciones.
- **1.5.0:** agrega el reporte de utilidad bruta histórica por artículo (con costo guardado en cada venta, devoluciones y reingresos) y el resumen actual de cuentas por pagar publicado por Finanzas. Señala líneas sin costo o sin conversión histórica. No agrega migraciones; requiere actualizar el POS anfitrión para que la API de ventas entregue los datos históricos.
- **1.6.0:** agrega la valoración de existencias al costo actual, incluyendo productos inactivos que aún tienen unidades. Separa los valores por moneda y marca productos sin costo. No requiere migraciones ni cambios al POS anfitrión.
- **1.7.0:** agrega productos bajo mínimo y antigüedad de cuentas por cobrar. El mínimo se configura por producto; las ventas a crédito nuevas permiten elegir vencimiento, inicialmente a 30 días. Las deudas anteriores sin fecha se muestran como "Sin vencimiento"; los abonos se aplican primero a la deuda más antigua. Requiere Desktop API 1.6 y la migración `Hito35StockMinimumAndReceivableDueDate` del POS anfitrión.

## Publicar el paquete firmado

Después de fusionar los cambios a `main`, el firmador centralizado genera el ZIP desde GitHub Actions:

1. Abre **Actions → Publicar módulo firmado → Run workflow**.
2. Selecciona la rama `main` y un módulo (`finance` o `advanced-reports`); ejecuta el workflow una vez por cada módulo.
3. Descarga el ZIP firmado del artifact `signed-module-output`.

El runner de firma del VPS guarda la clave privada fuera de GitHub y del equipo Windows. No se requiere copiar esa clave al repositorio.

Antes de instalar la versión 1.3.0 o superior, actualiza el POS anfitrión a Desktop API 1.5 para que aplique la migración `ReportSheetTemplates`. Para usar la versión 1.7.0, actualiza primero el POS anfitrión a Desktop API 1.6 para aplicar `Hito35StockMinimumAndReceivableDueDate`; el instalador conserva las existencias y las cuentas actuales, y no inventa vencimientos para cargos históricos. Para usar el reporte de utilidad histórica, actualiza también el POS anfitrión a la versión que expone costos y devoluciones en el reporte de ventas, e instala `finance` 1.0.8 para habilitar cuentas por pagar. Las plantillas se guardan junto a las vistas personales del operador. El visor y las fuentes de reportes requieren `Reports.View` (mapeado al permiso de operador `ViewReports`); el catálogo de inventario requiere `Reports.Inventory` (mapeado a `ViewInventory`). Las fuentes del módulo Finanzas requieren `Purchase.View`; las de Almacenes siguen requiriendo `Warehouse.View` o `Warehouse.Transfer`.

Genera también el paquete del POS con **Actions → Empaquetar POS para Windows** para que la API de ventas entregue costo y devoluciones. Los cambios visibles y pasos de actualización están en [`docs/manuales/REPORTES-AVANZADOS-1.7.0.md`](../../../docs/manuales/REPORTES-AVANZADOS-1.7.0.md).

## Cómo agregar una fuente de reporte

Un módulo de escritorio puede llamar PosDesktopModuleRegistry.RegisterReportSource durante IPosDesktopModule.Configure. Debe declarar un ID estable, título, columnas tipadas y el permiso del manifiesto. Su delegado consulta datos solo a través de PosDesktopModuleContext.ApiClient o ReportApiClient; nunca debe construir SQL ni acceder a la base de datos.

El visor muestra únicamente fuentes de módulos activos y permisos autorizados. IDs duplicados se rechazan en la terminal. Una fuente puede indicar si utiliza un período de fechas.

## Vistas personales

Las vistas guardadas almacenan filtros, columnas y el diseño opcional de la hoja. El servidor las asocia al operador autenticado; los nombres y configuraciones se comparten entre sus terminales. No guardes consultas, SQL o datos de resultados en la definición.

## Verificaciones

- El registro y la proyección de columnas tienen pruebas en tests/Pos.Application.Tests/AdvancedReportModuleContractTests.cs.
- El modelo/migración de vistas personales tiene prueba en tests/Pos.Integration.Tests/TerminalClientVersionReportingTests.cs.
- El exportador XLSX escribe Open XML directamente y no necesita que Excel esté instalado.
