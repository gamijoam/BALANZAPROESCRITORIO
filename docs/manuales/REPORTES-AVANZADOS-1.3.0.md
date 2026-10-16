# Reportes avanzados 1.3.0

Esta versión añade resúmenes automáticos y un editor para personalizar las hojas impresas de los reportes.

## Resúmenes

Al consultar un reporte, las tarjetas de resumen aparecen sobre la tabla y se actualizan con la búsqueda y la moneda seleccionadas. Los importes se mantienen separados por moneda.

- **Saldos por cobrar:** clientes con deuda, deuda total en USD, equivalente total en VES y crédito disponible en USD.
- **Movimientos de cuentas por cobrar:** cantidad de movimientos y clientes, cargos y abonos expresados en USD.
- **Ventas:** cantidad de ventas y total por moneda. En el reporte detallado también se muestran líneas, unidades e importe bruto; el reporte por producto muestra unidades e importes netos después de devoluciones.
- **Caja:** turnos, turnos abiertos, diferencias de arqueo en USD y VES, y cobros/reintegros separados por operación y moneda.
- **Inventario:** productos y valor estimado de existencias por moneda. El valor se calcula con precio de venta y no representa costo ni utilidad. Los movimientos muestran cantidad de registros, productos afectados, entradas y salidas.
- **Precios y márgenes:** cantidad de precios evaluables y margen ponderado por nivel y moneda, solo cuando hay costo y precio válidos.
- **Almacenes:** productos y valor estimado por moneda; traslados por cantidad, líneas de producto y estado.

Los resúmenes reflejan las filas que coinciden con los filtros actuales. Los reportes de saldo muestran la deuda total vigente; no requieren seleccionar un período.

## Diseñar la hoja

1. Abre **Reportes**, selecciona una categoría y aplica los filtros.
2. Pulsa **Diseñar hoja**.
3. Edita el nombre del comercio, título y subtítulo; elige color, papel y orientación.
4. Activa o desactiva el resumen, los filtros y la fecha de generación.
5. En **Columnas de la hoja**, cambia títulos, visibilidad y ancho. Usa **Subir** y **Bajar** para cambiar el orden.
6. Si lo necesitas, elige una agrupación y las columnas numéricas a sumar. La hoja añade subtotales y total general.
7. Pulsa **Ver hoja para imprimir**. Para guardar el diseño, escribe un nombre y pulsa **Guardar plantilla**.

Las plantillas quedan guardadas con las vistas personales del operador en el servidor local del POS, disponibles sin Internet y en sus terminales. La vista de impresión aplica el encabezado, resumen, agrupaciones, totales, columnas y nota al pie. Puedes elegir **Microsoft Print to PDF** en la ventana de impresión de Windows.

## Precios y márgenes

El reporte **Precios y márgenes actuales** muestra una fila por producto y nivel (Precio 1, 2 y 3), con costo unitario, precio actual, ganancia bruta por unidad y margen bruto porcentual. El margen se calcula como `(precio - costo) / precio × 100`, con el costo vigente del producto. Los precios de catálogo no incluyen impuesto.

Si el precio de un nivel no está configurado, el reporte lo indica. Si falta el costo, deja la ganancia y el margen sin calcular; nunca toma el costo faltante como cero. Este reporte describe el catálogo actual y no la utilidad histórica de ventas.

## Actualización

Primero instala la actualización completa del POS anfitrión que ofrece Desktop API 1.5 y aplica la migración `ReportSheetTemplates`. Después instala el módulo firmado `advanced-reports-1.3.0.zip` desde **Administración → Módulos**.

Para generar el módulo firmado después de fusionar los cambios a `main`:

1. En GitHub, abre **Actions → Publicar módulo firmado → Run workflow**.
2. Selecciona la rama `main` y el módulo `advanced-reports`.
3. Descarga el ZIP firmado del artifact `signed-module-output` e instálalo desde **Administración → Módulos**.

La plantilla y el resumen se calculan localmente con los datos cargados en el POS. La impresión no requiere conexión a Internet. Microsoft Print to PDF debe estar habilitado en Windows para guardar el resultado como PDF.
