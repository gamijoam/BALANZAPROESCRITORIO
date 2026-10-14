# Historia: registrar saldo inicial de cuentas por cobrar

## Contexto y alcance aprobado

Hito 4 ya contempla clientes, fiados, saldos, abonos y auditoría. Esta historia permite incorporar el saldo pendiente de un sistema anterior como un cargo inicial del cliente, sin inventar ventas, pagos, cajas ni operadores. La aplicación WPF seguirá usando únicamente el contrato HTTP; el servidor validará permisos, integridad e idempotencia.

## Criterios de aceptación

1. **Registro exacto:** dado un cliente activo sin movimientos de cuenta, cuando un operador con `ManageReceivables` registra un saldo USD positivo con hasta cuatro decimales y un motivo, el servidor crea un cargo inicial inmutable, sin `SaleId`, conserva el importe exacto y registra actor, terminal, fecha UTC y evento de auditoría.
2. **Idempotencia:** si se repite la misma solicitud con la misma clave de idempotencia, se devuelve el movimiento existente sin crear un segundo cargo ni repetir la auditoría. La misma clave con datos distintos se rechaza.
3. **Una sola apertura:** un cliente con cualquier movimiento previo no puede recibir un saldo inicial nuevo; una cuenta ya inicializada no puede duplicarse con otra clave.
4. **Autorización:** el endpoint exige `ManageReceivables`; la validación del cliente nunca sustituye la validación del servidor.
5. **Interfaz:** la ventana de cuenta de crédito permite registrar el saldo inicial una sola vez cuando la cuenta todavía no tiene movimientos y muestra ese movimiento en el historial.
6. **Carga del legado:** cargar una sola apertura por cada cliente deudor ya importado, usando el total pendiente agrupado del archivo; no crear usuarios ni ventas históricas falsas.

## Datos que se conservarán

- Importe por cliente en USD con cuatro decimales.
- Motivo con fecha de corte y cantidad de ventas a crédito que componen el total.
- Clave de idempotencia derivada de la fuente y del cliente de origen, para que la carga pueda reintentarse.
- Actor y terminal existentes usados por el servicio; no se crean identidades.

## Pruebas primero

- `Opening_balance_is_exact_audited_and_idempotent`: prueba de integración PostgreSQL para importe, movimiento, auditoría, reintento y rechazo de una segunda apertura.
- `Receivable_opening_balance_uses_the_authenticated_api_contract`: prueba del cliente HTTP para ruta, autenticación y precisión decimal.
- `Receivables_view_exposes_opening_balance_action`: prueba del contrato visual de la ventana WPF.

## Persistencia y recuperación

La migración añade metadatos de apertura y clave idempotente, sin reescribir cargos históricos. Los cargos iniciales quedan en el mismo libro inmutable que los demás movimientos. El respaldo PostgreSQL previo a migrar permite restaurar el estado anterior si la instalación debe revertirse.
