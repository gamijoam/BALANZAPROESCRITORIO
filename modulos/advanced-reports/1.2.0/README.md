# Reportes avanzados 1.2.0

Esta actualización agrega una vista paginada tipo hoja para imprimir o guardar los reportes como PDF, y un reporte de precios y márgenes actuales para los tres niveles de precio.

## Novedades

- **Vista de hoja:** respeta filtros, filas, orden y columnas visibles. Permite elegir Carta, Oficio o A4 y orientación horizontal o vertical. Para guardar como PDF, selecciona Microsoft Print to PDF en el diálogo de impresión de Windows.
- **Precios y márgenes actuales:** muestra por producto Precio 1, Precio 2 y Precio 3, costo unitario vigente, ganancia bruta por unidad y margen bruto porcentual. Si no hay costo registrado, no inventa un margen. Este reporte describe el catálogo actual, no la utilidad histórica de ventas.

No agrega migraciones ni requiere actualizar el POS anfitrión. El reporte de precios requiere el permiso `ViewInventory`; las demás fuentes conservan sus permisos actuales. Consulta [la guía de actualización](../../../docs/manuales/REPORTES-AVANZADOS-1.2.0.md) para más detalles.

## Publicar el paquete firmado

El ZIP firmado aún no está incluido en esta carpeta. Después de fusionar los cambios a `main`, se genera desde GitHub Actions:

1. Abre **Actions → Publicar módulo firmado → Run workflow**.
2. Selecciona la rama `main` y `advanced-reports`.
3. Descarga `advanced-reports-1.2.0.zip` del artifact `signed-module-output` y verifica su SHA-256 antes de instalar.

La clave privada permanece en el VPS firmador. No la instales en Windows ni la copies al repositorio. El artifact de GitHub se conserva 30 días.

La vista previa y el empaquetado deben revisarse en Windows antes de distribuir el ZIP firmado.
