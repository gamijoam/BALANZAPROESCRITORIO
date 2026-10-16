# Hito 33 — Vista de hoja e impresión de reportes

## Estado

**Aprobado para implementación por el usuario el 16 de octubre de 2026**, al solicitar iniciar el diseño de reportes tipo hoja dentro del módulo Reportes.

## Historia

Como operador con permiso para consultar un reporte, quiero previsualizarlo como una hoja paginada, con las columnas y filtros que seleccioné, para imprimirlo o guardarlo como PDF sin salir del POS.

## Criterios de aceptación

- La acción **Vista de hoja** está dentro del módulo Reportes avanzados.
- El visor presenta el título, los filtros aplicados, fecha de generación y el total de filas.
- La tabla impresa conserva las filas filtradas, el orden y las columnas visibles en el reporte.
- La hoja se pagina y puede consultarse dentro de una ventana WPF. El operador puede elegir Carta, Oficio o A4, en orientación horizontal o vertical; el valor inicial es Carta horizontal.
- El tamaño y la orientación elegidos se usan tanto en la vista previa como en la impresión; no se requiere Excel ni conectividad con el VPS.
- La acción de impresión usa el diálogo estándar de Windows. El operador puede seleccionar Microsoft Print to PDF para guardar el archivo.
- Si no hay columnas visibles, el visor informa cómo corregirlo en vez de producir una hoja vacía.
- La función solo cambia la presentación; no modifica ventas, inventario ni otros datos.
- La vista de hoja presenta los campos entregados por la fuente y no inventa cálculos. Los márgenes actuales se incorporan en el reporte específico del hito 34; la utilidad histórica de ventas requiere snapshots de costo y queda fuera de este hito.

## Pruebas candidatas

- La vista previa usa solo las filas filtradas y las columnas visibles en el orden actual.
- El encabezado muestra el período, moneda y búsqueda que se aplicaron.
- El documento pagina el contenido cuando supera una hoja y presenta encabezados de tabla antes de las filas.
- La ventana de vista previa se abre sin impresora configurada; imprimir cancelado no altera el reporte.
- Un reporte vacío presenta un mensaje en la hoja sin fallar.

## Restricciones

- El escritorio utiliza los datos ya entregados por las fuentes autorizadas; no consulta PostgreSQL directamente.
- Los reportes respetan los permisos existentes.
- La exportación PDF se realiza con el proveedor de impresión de Windows elegido por el usuario, incluido Microsoft Print to PDF si está instalado.
- La compilación WPF y la inspección visual deben confirmarse en Windows antes de distribuir el paquete firmado.

## Alcance de esta entrega

Se incorpora el visor paginado y el flujo de impresión a PDF en el módulo existente. La edición de columnas, filtros, orden y vistas personales ya existente se mantiene como preparación de la hoja. Las opciones de diseño avanzadas (grupos, subtotales, logotipo y formatos guardados) y los reportes de rentabilidad quedan como fases separadas.
