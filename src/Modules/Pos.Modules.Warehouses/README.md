# Módulo Almacenes y traslados

Módulo oficial opcional para organizar las existencias existentes por ubicación y registrar traslados.

## Cambios de la versión 1.3.3

- El historial de cada producto incorpora una pestaña **Traslados** cuando el módulo está activo y el operador tiene `Warehouse.View`.
- Registra una salida negativa al despachar y una entrada positiva con las unidades realmente recibidas; cada fila muestra ruta, referencia, fecha y operador. Los borradores no aparecen como movimientos.
- La consulta pagina los eventos del servicio; el POS de escritorio no accede a PostgreSQL.
- No requiere migración. Actualiza el POS anfitrión a Desktop API 1.1.0 y el ZIP firmado `warehouses` 1.3.3, luego reinicia servicio y terminales.

## Cambios de la versión 1.3.2

- El filtro de Inventario muestra la existencia del almacén seleccionado, incluso cuando es cero.
- Los filtros “Con existencia” y “Agotados” se aplican al saldo del almacén mostrado; “Almacén: todos” usa el saldo global.
- No modifica saldos ni agrega migraciones; conserva `warehouses:0001`.

## Funciones visuales de la versión 1.3.1

- Vista de traslado en dos paneles: búsqueda y selección a la izquierda; borrador y acción de creación a la derecha. Los almacenes de origen y destino quedan arriba.
- El resultado seleccionado, su existencia en origen, la cantidad y el botón Agregar permanecen visibles sin depender de desplazamiento vertical.
- Cantidades con el formato común del POS: `90`, `1,25`, `0,5`; ya no se muestran enteros como `90.000`, que se confunden con noventa mil.
- La tabla de existencias muestra precio con dos decimales y su moneda en una columna independiente.
- El alta de almacén ocupa una fila compacta y la tabla de existencias usa el resto del espacio.
- No modifica cantidades almacenadas ni agrega migraciones; conserva `warehouses:0001`.

## Funciones existentes desde 1.3.0

- Almacén principal creado automáticamente y almacenes adicionales activos/inactivos.
- Saldo por producto y almacén; los saldos existentes se copian al principal al inicializar el módulo.
- Asignación de una terminal a un almacén; las ventas descuentan ese saldo en la misma transacción PostgreSQL que actualiza el POS.
- Traslados borrador → despachado → recibido. El despacho reserva las unidades del origen, el recibo las acredita al destino; una diferencia de recepción queda guardada como variación y se descuenta del saldo global.
- Selección explícita del producto antes de agregarlo al traslado, con código, nombre y existencia visible en el almacén de origen; la búsqueda nunca agrega automáticamente el primer resultado.
- Historial de traslados con detalle de productos, cantidades enviadas/recibidas y variaciones, consultado a través del servicio.
- Devoluciones reponen el almacén donde se originó la venta. La desactivación del módulo conserva tablas y los puentes transaccionales para que ventas/devoluciones previas a una reactivación no pierdan consistencia.
- Permisos `Warehouse.View`, `Warehouse.Manage` y `Warehouse.Transfer`, validados por el servidor y ligados a permisos del POS.
- Eventos de auditoría para alta de almacén, asignación de terminal y ciclo de traslado.
- Ensamblado WPF firmado que registra el menú **Almacenes** y el filtro por ubicación en Inventario. El anfitrión lo descarga del servidor y verifica firma, hashes y `desktopApi` al iniciar sesión.

## Límites conocidos de esta entrega

- No incluye compras, proveedores, lotes, vencimientos, series, conteo cíclico ni múltiples sucursales con sincronización.
- El botón de asignación cambia la terminal donde se usa; hazlo al inicio de una operación y no cambies una terminal de almacén mientras haya ventas pendientes.
- Las diferencias de recepción se pueden registrar desde la API del módulo. La ventana WPF actual ofrece “Recibir completo”; el conteo parcial se habilitará en una revisión de UX aparte.
- El almacén principal no se puede desactivar. Un almacén no se puede desactivar si conserva unidades o traslados borrador/en tránsito.
- Venta y Cobro no son reemplazables por el módulo. Para una extensión nueva hace falta que el anfitrión publique primero un contrato compatible.

La versión 1.3.3 conserva la migración `warehouses:0001`; no requiere una migración nueva. Actualiza el POS anfitrión y el ZIP firmado del módulo. Reinicia el servicio y las terminales para cargar ambos cambios.

Ver [guía para futuros desarrolladores](../../../docs/manuales/crear-modulos-pos.md) para compilar, firmar, instalar, actualizar y probar el módulo.
