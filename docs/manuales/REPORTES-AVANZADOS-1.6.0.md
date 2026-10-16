# Reportes avanzados 1.6.0

Esta versión incorpora valoración del inventario con el costo actual y conserva los reportes de cuentas por pagar y utilidad histórica de la versión 1.5.0.

## Valor del inventario al costo

El reporte multiplica las existencias actuales por el costo unitario guardado en el producto. Incluye productos activos e inactivos que todavía tienen unidades, mantiene los valores separados por moneda y muestra los productos sin costo sin incluirlos como si valieran cero.

El resumen presenta productos con existencia, unidades disponibles, productos sin costo y valor total por moneda. La valoración usa el costo actual del catálogo; no es una reconstrucción histórica ni reemplaza un balance contable. El cálculo no requiere conexión a Internet ni cambios de base de datos.

## Cuentas por pagar y utilidad histórica

Esta versión mantiene los reportes añadidos en 1.5.0: cuentas por pagar con vencimientos y utilidad bruta histórica por artículo. La guía anterior describe sus cálculos y condiciones de datos en [`REPORTES-AVANZADOS-1.5.0.md`](REPORTES-AVANZADOS-1.5.0.md).

## Actualizar

Después de fusionar a `main`, si aún no instalaste la actualización del POS que acompaña a Reportes 1.5.0, genera su ZIP desde **Actions → Empaquetar POS para Windows** e instálalo con **Actualizar POS**. Para el módulo firmado, ejecuta **Actions → Publicar módulo firmado → Run workflow**, selecciona `main` y `advanced-reports`, descarga el artifact `signed-module-output` e instala `advanced-reports` **1.6.0** desde **Administración → Módulos**.

El reporte de cuentas por pagar también necesita el módulo Finanzas **1.0.8**. El reporte de utilidad histórica necesita la actualización del POS anfitrión que expone los costos y devoluciones guardados en cada venta. No hay migraciones nuevas para la versión 1.6.0.
