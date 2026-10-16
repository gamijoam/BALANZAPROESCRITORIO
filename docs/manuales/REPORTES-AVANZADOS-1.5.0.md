# Reportes avanzados 1.5.0

Esta versión añade cuentas por pagar y utilidad bruta histórica por artículo. No agrega migraciones a la base de datos.

## Cuentas por pagar

El reporte muestra facturas y notas con saldo abierto, proveedor, fechas de emisión y vencimiento, saldo pendiente en la moneda del documento y su equivalente en USD. Incluye todos los documentos abiertos, sin limitar el resultado a un período de emisión.

El resumen incluye documentos pendientes, proveedores con saldo, saldo total equivalente en USD, saldo vencido y saldo que vence hoy o durante los próximos siete días. También presenta los saldos separados por moneda. La conversión a USD usa la tasa guardada en cada documento.

La fuente pertenece al módulo Finanzas y requiere que el operador tenga permiso para consultar compras (`Purchase.View`). Instala la versión 1.0.8 del módulo `finance` para incluirla en Reportes.

## Utilidad bruta histórica

El reporte **Utilidad bruta por artículo** usa el costo guardado al momento de cada venta; cambiar el costo actual de un producto no altera el historial. Incluye cantidad vendida, devuelta y reingresada, venta antes de devoluciones, importe devuelto, venta neta, costo histórico convertido a la moneda de la venta, utilidad bruta y margen.

El costo de las unidades reingresadas a inventario se resta del costo de venta. Si un artículo devuelto no se reingresó, su costo permanece en el cálculo. Las devoluciones reducen la venta neta. Los valores permanecen separados por moneda.

Cuando una venta no tiene un costo histórico, el reporte deja costo, utilidad y margen sin calcular para esa línea. También señala si falta la moneda o tasa histórica necesaria para convertirlo. El resumen cuenta esos casos y calcula utilidad y margen solo sobre líneas con costo e impuesto histórico calculables. Los impuestos registrados se excluyen de la venta neta. Si una venta antigua no guardó el impuesto, esa línea se marca y queda fuera de los totales de venta neta y utilidad porque no se puede separar el impuesto con precisión.

Este reporte necesita la actualización completa del POS anfitrión que expone al módulo los costos, tasas y datos de devoluciones guardados en cada venta. Las ventas antiguas sin esos datos siguen visibles y se identifican como incompletas; no se les asigna costo cero.

## Actualizar

Después de fusionar los cambios a `main`, actualiza en este orden:

1. Ejecuta **Actions → Empaquetar POS para Windows** y genera un ZIP nuevo. Instálalo con **Actualizar POS**. Esta versión también aumenta el texto de referencia USD en la tarjeta roja.
2. Ejecuta **Actions → Publicar módulo firmado → Run workflow** con `finance`; instala el ZIP `finance` 1.0.8 desde **Administración → Módulos**.
3. Ejecuta el mismo workflow con `advanced-reports`; instala el ZIP `advanced-reports` 1.5.0.

El instalador hace respaldo y solicita reinicio cuando instala un módulo. No se requiere una migración nueva de base de datos para estos reportes. El diseñador de hojas y sus plantillas requieren que el POS anfitrión tenga aplicada la migración `ReportSheetTemplates` desde la versión 1.3.0 de Reportes avanzados.

## Nota sobre los datos

La utilidad bruta depende de que el costo se haya guardado al vender. Si los productos no tenían costo registrado, el reporte puede mostrar venta neta pero no utilidad para esas líneas. Las ventas históricas previas a la captura del costo y de impuestos no se reconstruyen usando los valores actuales del catálogo.
