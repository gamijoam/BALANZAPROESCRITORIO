# Módulo Almacenes y traslados

Módulo oficial opcional para organizar las existencias existentes por ubicación y registrar traslados.

## Qué incluye 1.2.0

- Almacén principal creado automáticamente y almacenes adicionales activos/inactivos.
- Saldo por producto y almacén; los saldos existentes se copian al principal al inicializar el módulo.
- Asignación de una terminal a un almacén; las ventas descuentan ese saldo en la misma transacción PostgreSQL que actualiza el POS.
- Traslados borrador → despachado → recibido. El despacho reserva las unidades del origen, el recibo las acredita al destino; una diferencia de recepción queda guardada como variación y se descuenta del saldo global.
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

Ver [guía para futuros desarrolladores](../../../docs/manuales/crear-modulos-pos.md) para compilar, firmar, instalar, actualizar y probar el módulo.
