# Hito 10 — Clientes y créditos claros

## Historias y criterios de aceptación

- La pantalla utiliza “Clientes y créditos” y presenta etiquetas permanentes y legibles para los datos del cliente.
- Se puede buscar por nombre, documento o teléfono y al pulsar Enter se actualiza la lista.
- Se puede editar nombre, documento, teléfono y límite de crédito del cliente seleccionado; el saldo histórico no se altera. No se permite reducir el límite por debajo del saldo pendiente.
- Se puede eliminar un cliente de la lista activa con confirmación; el servicio conserva su historial, audita la acción y rechaza la operación si mantiene saldo pendiente.
- La lista muestra moneda, límite, saldo pendiente y crédito disponible con formato monetario es-VE.
- La pantalla de cuenta presenta “Venta a crédito” y “Abono” en español, referencia de venta legible, importe y saldo resultante.
- Cada cargo por venta muestra los productos vendidos, cantidades y valor histórico convertido a USD con la tasa de la venta; para ventas USD, muestra el importe USD original.
- El saldo de deuda permanece en USD. Su equivalente en VES se consulta con la tasa vigente y cambia al volver a consultar la cuenta después de actualizar la tasa; nunca reescribe cargos históricos.
- Cobrar una venta parcialmente a crédito requiere cliente seleccionado y crédito disponible suficiente; la cuenta siempre se denomina en USD y convierte importes VES con la tasa vigente de la venta.
- Registrar un abono permite pagos parciales en USD o VES, admite una referencia opcional de hasta 250 caracteres, convierte VES con la tasa vigente al pago y rechaza un importe convertido superior al saldo.
- Al cerrar la cuenta de crédito, la lista de clientes refresca saldo, disponible y equivalente en VES, manteniendo seleccionado al mismo cliente.
- La UI ofrece nombres claros para cliente general, asignación, consulta de cuenta y registro de abono; no presenta “fiado” al usuario.

## Reglas de negocio actuales preservadas

- El límite es máximo de saldo pendiente; no se implementan intereses, vencimientos ni cuotas en este hito.
- El backend mantiene sus entradas de cuenta inmutables y registra auditoría.
- La venta debe confirmarse para generar el cargo; cancelar el cobro no genera deuda.

## Pruebas

Primero se agregan pruebas de diseño para etiquetas, columnas de límite/saldo/disponible y presentación de movimientos en español. Luego se verifican búsqueda por teléfono, saldo, crédito disponible, generación de cargo al confirmar venta y abonos parciales mediante pruebas de integración.
