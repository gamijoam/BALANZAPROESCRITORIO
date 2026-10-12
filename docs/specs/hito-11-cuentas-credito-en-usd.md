# Hito 11 — Crédito denominado en USD

## Historias y criterios de aceptación

- Toda cuenta de cliente, su límite y su saldo se denominan en USD.
- Al confirmar una venta en VES a crédito, el servicio convierte el importe del crédito a USD con la tasa vigente usada en esa venta; en venta USD conserva el importe USD.
- El pago de crédito conserva moneda e importe cobrados y la cuenta registra el importe USD, la tasa y el importe de origen para trazabilidad.
- El saldo de la cuenta y su límite se muestran en USD; la pantalla también muestra el equivalente VES al cambio vigente, claramente como referencia.
- Los cargos asociados a una venta permiten consultar los productos, cantidades y valor USD calculado con la tasa histórica; el equivalente VES del saldo pendiente se calcula con la tasa vigente de cada consulta.
- El límite disponible se valida en USD y la creación de clientes no permite seleccionar otra moneda de cuenta.
- La cuenta permite registrar abonos en USD o VES; el abono VES se convierte a USD con la tasa vigente al registrar el pago y la pantalla conserva ambas cifras.
- Las cuentas y límites históricos en VES se convierten a USD durante la migración, conservando los importes originales de los movimientos y su tasa cuando existan.
- Las ventas a crédito históricas sin movimiento se recuperan solo cuando la venta conserva una tasa verificable; el movimiento recuperado queda auditado. Las ventas sin tasa verificable no generan deuda automáticamente.

## Pruebas

- Primero deben fallar pruebas de presentación (cuenta siempre USD y equivalencia VES) y la integración de venta en VES a un cliente USD.
- La integración debe cubrir conversión, límite, trazabilidad de la tasa, saldo y abono en VES/ USD.
