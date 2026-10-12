# Hito 16: UX de traslados de almacén

## Objetivo

Permitir que el operador identifique el producto correcto antes de incorporarlo a un traslado y revisar los artículos, cantidades y diferencias de un traslado existente.

## Criterios de aceptación

- Buscar un producto por código o nombre presenta resultados seleccionables; la búsqueda nunca incorpora automáticamente el primer resultado.
- Al seleccionar un resultado, la vista muestra nombre, código y existencia en el almacén de origen.
- El operador agrega el producto de forma explícita. La cantidad debe ser positiva y admitir hasta tres decimales.
- El flujo de teclado permite recorrer resultados, confirmar la selección con Enter, editar la cantidad y agregarla con Enter.
- El estado y los errores de operación quedan visibles en cualquiera de las pestañas.
- El historial carga las líneas y las cantidades enviadas, recibidas y la variación al seleccionar un traslado.
- Las acciones Despachar y Recibir completo se habilitan según el estado del traslado seleccionado y continúan autorizándose en el servidor.
- La interfaz consulta el servicio HTTP; no conecta directamente a PostgreSQL.
- Las cantidades se muestran con la cultura del POS y sin agrupar enteros como miles (`90`, `1,25`, `0,5`).
- El compositor de traslado presenta búsqueda/selección y borrador en columnas paralelas; el alta de almacén usa una fila compacta y la tabla de existencias ocupa el espacio restante.
- El precio de existencias muestra la cantidad monetaria y la moneda de forma explícita.

## Implementación

La interfaz está en `src/Modules/Pos.Modules.Warehouses.Desktop/WarehouseWindow.xaml` y su code-behind. El endpoint `GET transfers/{id}` devuelve las instantáneas de artículo guardadas con el traslado. Los DTO de detalle viven en el ensamblado de escritorio del módulo para que esta actualización no exija cambiar el contrato del POS anfitrión. No se agrega migración: se conserva `warehouses:0001`.

La versión firmada para distribuir es `artifacts/modules/warehouses/1.3.1.zip`. Se genera con `scripts/Build-WarehouseModulePackage.ps1`; el script también exporta la clave pública versionada para verificar firma e integridad. Esta versión es solo de presentación y conserva `warehouses:0001`; no convierte ni altera los saldos guardados.

## Pruebas y límites de verificación

- `WarehouseTransferUxTests`: 10 pruebas de selección explícita, accesibilidad, vista compacta, cultura y formato de cantidades.
- `Pos.Application.Tests`: 202 pruebas.
- `Pos.Modules.Tests`: 53 pruebas.
- `Pos.Domain.Tests`: 92 pruebas.
- Compilación Release de las DLL de servidor, escritorio y publicador: correcta, cero advertencias y errores.
- El paquete firmado debe pasar `scripts/Test-SignedModulePackage.ps1` antes de entregarse.
- La prueba visual en WPF y la prueba de activación contra PostgreSQL requieren ejecutarse en una instalación de prueba. No usar la base activa de un comercio. La suite de integración local puede tomar su conexión desde User Secrets; solo debe ejecutarse con una base desechable y aislada.

La recepción parcial sigue disponible en el contrato del servidor, pero esta pantalla conserva la acción **Recibir completo**. Habilitar captura de cantidades recibidas por artículo requiere otra historia y criterios propios. La nueva distribución debe comprobarse en una sesión WPF real a 1366×768 y con DPI de 125 %; la suite XAML no sustituye esa revisión visual.
