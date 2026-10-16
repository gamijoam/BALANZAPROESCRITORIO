# Reportes avanzados 1.2.0

Esta versión agrega una vista paginada tipo hoja para revisar e imprimir los reportes configurados.

## Precios y márgenes actuales

El reporte **Precios y márgenes actuales** muestra una fila por producto y nivel (Precio 1, 2 y 3), con costo unitario, precio actual, ganancia bruta por unidad y margen bruto porcentual. El margen se calcula como `(precio - costo) / precio × 100`, con el costo vigente del producto. Los precios de catálogo no incluyen impuesto.

Si el precio de un nivel no está configurado, el reporte lo indica. Si falta el costo, deja la ganancia y el margen sin calcular; nunca toma el costo faltante como cero. Un precio igual a cero tampoco produce un porcentaje de margen.

Este reporte describe el catálogo actual. No representa la utilidad histórica de ventas, que requiere usar el costo guardado en cada venta.

## Vista de hoja

1. Abre **Reportes** y elige el tipo de reporte.
2. Aplica el período, moneda o búsqueda que necesites.
3. En **Columnas**, marca las que quieras incluir. Puedes arrastrar los encabezados para cambiar su orden.
4. Pulsa **Vista de hoja** para revisar cómo quedará la página.
5. Ajusta el papel (**Carta**, **Oficio** o **A4**) y la orientación (**Horizontal** o **Vertical**). La vista inicia en Carta horizontal.
6. Pulsa **Imprimir / guardar PDF**. Para crear un PDF, elige **Microsoft Print to PDF** en la ventana de impresión de Windows y selecciona dónde guardarlo.

La hoja usa las filas filtradas y las columnas visibles del reporte. La vista previa no modifica ventas, inventario ni otros datos. Se puede abrir sin Internet; el reporte debe haber cargado previamente sus datos.

La creación del PDF depende de que Microsoft Print to PDF esté disponible en Windows. Si no aparece en la lista de impresoras, la opción se puede habilitar desde las características opcionales de Windows.

## Actualización

Instala el paquete firmado `advanced-reports-1.2.0.zip` desde **Administración → Módulos**. No requiere migraciones ni actualización del POS anfitrión. El certificado de firma se conserva fuera del repositorio.

Para generar el paquete después de fusionar los cambios a `main`:

1. En GitHub, abre **Actions → Publicar módulo firmado → Run workflow**.
2. Selecciona la rama `main` y el módulo `advanced-reports`.
3. Espera a que termine la compilación y el firmador del VPS. El trabajo publica el artifact `signed-module-output` con el ZIP firmado, la clave pública y el SHA-256.
4. Descarga el ZIP desde ese artifact e instálalo en el POS desde **Administración → Módulos**.

El artifact firmado se conserva durante 30 días. No necesitas instalar ni copiar certificados privados en Windows; la clave privada permanece en el VPS.
