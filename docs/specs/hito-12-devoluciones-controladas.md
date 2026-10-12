# Historia — Devoluciones parciales y totales

## Alcance aprobado

Implementar devoluciones parciales o totales de una venta confirmada. Cada devolución queda vinculada a la venta original. El reintegro se distribuye usando los mismos medios y monedas que el pago original. Los pagos a crédito reducen la cuenta del cliente en USD; las devoluciones en efectivo se registran como salida de la caja abierta. Los reintegros bancarios o por punto de venta quedan registrados para que el comercio ejecute la operación en el proveedor correspondiente.

El usuario autorizado decide por cada artículo devuelto si vuelve a estar disponible en inventario. No se borran ni editan los registros de la venta, sus pagos o movimientos anteriores.

## Criterios de aceptación

- **Devolución parcial:** dado un artículo vendido, cuando un supervisor o administrador devuelve una cantidad positiva que no supera la cantidad pendiente, entonces se registra la cantidad y el importe al precio de la venta original.
- **Devolución total:** cuando las cantidades devueltas alcanzan todas las cantidades vendidas, la venta queda identificada como devuelta; sus documentos originales permanecen consultables.
- **Límite:** una cantidad que excede lo vendido menos devoluciones previas, un artículo ajeno a la venta o una línea repetida en la misma solicitud se rechaza sin cambios.
- **Reintegro:** el importe de cada devolución se distribuye entre medios y monedas originales, respetando lo que queda disponible de cada pago. La suma convertida debe coincidir con el importe devuelto; no se permite duplicar el reintegro.
- **Crédito:** la parte originalmente fiada genera un movimiento compensatorio en la cuenta del cliente, cuyo saldo continúa expresado en USD.
- **Caja:** la parte en efectivo genera una salida en la caja abierta del operador. La diferencia de caja refleja el reintegro; no se altera un turno cerrado.
- **Inventario:** solo las líneas marcadas para reingreso aumentan existencias y crean un movimiento positivo. Las otras líneas quedan registradas como no revendibles y no aumentan stock.
- **Permisos:** cajeros sin `ReturnSale` reciben 403; supervisores y administradores pueden devolver.
- **Atomicidad e idempotencia:** devolución, líneas, reintegros, ledger de crédito, efectivo, stock y auditoría se confirman en una sola transacción. Repetir la misma clave y contenido responde con la devolución existente; usar la misma clave con contenido distinto da conflicto.
- **Trazabilidad:** cada evento identifica venta, operador, terminal, caja, motivo, cantidades, importes, tasa/método aplicado y fecha.

## Pruebas

- Dominio: importe por cantidad parcial, acumulación de devoluciones, cantidades inválidas y redondeo monetario.
- Integración PostgreSQL: rollback ante fallo, permisos, conciliación de caja por moneda, stock, crédito USD, idempotencia y concurrencia de devoluciones sobre la misma venta.
- WPF: seleccionar una venta, elegir artículos/cantidades y reingreso, revisar desglose de reintegro y confirmar con motivo.

## Fuera de alcance

Procesar automáticamente reintegros en bancos, pago móvil o terminales; integración fiscal; devoluciones de ventas cuyo historial financiero no pueda reconciliarse; compras/proveedores.
