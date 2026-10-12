# Contrato API v1 — estado implementado

El host es ASP.NET Core y consume la conexión PostgreSQL configurada fuera del repositorio. JSON usa `camelCase`; UUID identifica operadores, terminales, turnos, productos y ventas. Los montos viajan como decimal JSON con moneda.

## Endpoints actuales

| Método y ruta | Acceso | Resultado |
|---|---|---|
| `GET /health/live` | Público | Proceso activo |
| `GET /health/ready` | Público | Comprueba conexión a PostgreSQL; 503 si no está disponible |
| `POST /api/v1/setup/first-operator` | Solo loopback en Development; solo mientras no hay operadores | Crea el primer usuario con PIN |
| `POST /api/v1/auth/login` | Público | Devuelve bearer token de sesión de 12 horas |
| `GET /api/v1/auth/session` | Bearer | Operador, terminal y vencimiento de la sesión |
| `POST /api/v1/auth/logout` | Bearer | Revoca la sesión actual |
| `GET /api/v1/cash-sessions/current` | Bearer | Devuelve el turno abierto de la terminal, quién lo abrió y quién lo tiene activo; oculta saldos esperados al operador que no lo tiene asignado |
| `POST /api/v1/cash-sessions` | Bearer | Abre turno de caja para la terminal de la sesión |
| `POST /api/v1/cash-sessions/current/claim` | Bearer con permiso de venta | Reclama un turno liberado de esta terminal; responde conflicto si otro operador lo tiene activo |
| `POST /api/v1/cash-sessions/current/release` | Bearer | Libera el turno activo para que otro operador autenticado pueda reclamarlo |
| `POST /api/v1/cash-sessions/{cashSessionId}/close` | Bearer con permiso de caja | Cierra el turno asignado al operador activo de esta terminal |
| `POST /api/v1/cash-sessions/{cashSessionId}/movements` | Bearer con permiso | Registra entrada o salida justificada de efectivo |
| `GET /api/v1/products?query={texto}` | Bearer | Busca productos activos por SKU o nombre (hasta 30 resultados). `quantity` es el saldo del almacén asignado a la terminal autenticada (principal si no tiene asignación); cuando el módulo Almacenes no está instalado, conserva la existencia global. |
| `GET /api/v1/inventory?query={texto}&status={active\|inactive\|all}&currency={VES\|USD}&stock={available\|out-of-stock}&hasImage={true\|false}` | Bearer con permiso de inventario | Busca por SKU/nombre y combina filtros; estado predeterminado: activo |
| `POST /api/v1/inventory/{productId}/adjustments` | Bearer con permiso | Ajusta stock con motivo y movimiento inmutable |
| `GET /api/v1/inventory/{productId}/movements` | Bearer con permiso | Consulta historial de movimientos de inventario |
| `POST /api/v1/products` | Bearer con permiso | Crea producto con stock inicial trazable |
| `PATCH /api/v1/products/{productId}` | Bearer con permiso de catálogo | Edita SKU y nombre; SKU duplicado devuelve 409; cambio auditado |
| `POST /api/v1/products/{productId}/price-changes` | Bearer con permiso | Cambia precio con motivo e historial |
| `GET /api/v1/products/{productId}/price-history` | Bearer con permiso | Consulta cambios de precio |
| `PATCH /api/v1/products/{productId}/active` | Bearer con permiso de catálogo | Activa/desactiva producto sin eliminar historial; cambio auditado |
| `GET/POST /api/v1/exchange-rates` | Bearer con permiso | Consulta e ingresa tasas con fecha y fuente |
| `GET /api/v1/exchange-rates/current` | Bearer con permiso | Consulta tasa vigente |
| `GET/POST /api/v1/customers` | Bearer con permiso | Busca por nombre/documento/teléfono; las cuentas, límites y saldos son USD e incluyen equivalente VES a tasa actual |
| `GET /api/v1/customers/{customerId}/receivables` | Bearer con permiso | Consulta saldo y movimientos por cobrar |
| `POST /api/v1/customers/{customerId}/payments` | Bearer con permiso | Registra abono a cuenta por cobrar |
| `GET /api/v1/reports/sales` | Bearer con permiso | Resumen de ventas por período |
| `GET /api/v1/reports/sales/detail` | Bearer con permiso | Historial paginado; filtra por moneda, método de pago, usuario de operador y estado antes de contar y paginar |
| `GET /api/v1/reports/cash-sessions?fromUtc={utc}&toUtc={utc}&status={Todas\|Abiertas\|Cerradas}` | Bearer con permiso | Arqueos por moneda y detalle de cobros/reintegros por método y moneda, más movimientos de caja por turno |
| `GET /api/v1/reports/products/sales?fromUtc={utc}&toUtc={utc}&currency={Todas\|VES\|USD}` | Bearer con permiso | Unidades vendidas/devueltas y montos bruto/neto por producto y moneda para ventas del período; las devoluciones vinculadas se descuentan del resumen |
| `GET /api/v1/reports/inventory` | Bearer con permiso | Valorización a precio de venta |
| `GET /api/v1/audit` | Bearer con permiso sensible | Consulta eventos de auditoría |
| `GET/POST /api/v1/operators` | Administrador | Lista y crea operadores con rol |
| `PATCH /api/v1/operators/{operatorId}/role` | Administrador | Cambia rol con auditoría |
| `PATCH /api/v1/operators/{operatorId}/active` | Administrador | Activa/desactiva y revoca sesiones |
| `GET /api/v1/operators/{operatorId}/permissions` | Bearer con permiso `ManageUsers` | Consulta permisos heredados y excepciones del operador |
| `PUT /api/v1/operators/{operatorId}/permissions` | Bearer con permiso `ManageUsers` | Reemplaza la matriz completa; modos `Inherit`, `Allow`, `RequireAdminPin` y `Deny`; cambio auditado |
| `POST /api/v1/auth/authorize` | Bearer de cajero + credenciales de administrador | Emite una aprobación de 60 segundos ligada a permiso, operador, terminal, ruta, contenido y operación; solo se consume una vez |
| `POST /api/v1/sales` | Bearer, turno abierto y `Idempotency-Key` | Confirma venta, pagos, auditoría y movimiento de stock; una línea puede incluir `unitPriceOverride` y `priceOverrideReason` para un precio especial de una sola venta |
| `POST /api/v1/sales/{saleId}/returns` | Bearer, permiso `ReturnSale`, turno abierto e `Idempotency-Key` | Devuelve cantidades parciales o totales; conserva venta original, registra reintegros por medio/moneda original, caja, crédito, inventario y auditoría en una transacción |

El alta inicial acepta `{"username":"cajero","pin":"1234"}`; el PIN debe tener de 4 a 12 dígitos. El login acepta `username`, `pin` y `terminalId` UUID. El token solo se devuelve al iniciar sesión; en PostgreSQL se conserva su hash.

Un precio especial requiere importe mayor que cero, hasta cuatro decimales y motivo de 3 a 250 caracteres. Usa el permiso `OverrideSalePrice`; por defecto, todo operador que no sea administrador necesita el PIN de un administrador. La aprobación se vincula al contenido exacto de la venta. El importe aplicado queda en la línea histórica y el precio/motivo se guardan en el evento `sale.confirmed`; el precio del catálogo no cambia.

Abrir caja: `{"openingBalance":0,"currency":"VES"}`; solo se acepta `VES` o `USD`. Cerrar caja requiere `{"countedClosingBalance":123.45}`; el servidor registra saldo esperado, contado y diferencia para cada moneda. La venta recibe `cashSessionId` y `checkout`, con líneas `{productId, quantity}`, pagos `{method, amount, currency}` y `exchangeRateId` cuando alguna línea, pago o crédito requiere conversión. La moneda de la caja determina la moneda del comprobante; se pueden combinar pagos en VES y USD. El pago conserva su importe y moneda recibidos, además de la tasa aplicada; el cierre cuenta ese efectivo en su moneda real. Los importes extranjeros se convierten por grupo de moneda y se redondean a centavos en la moneda de venta. Se acepta una diferencia máxima igual a medio centavo de la moneda extranjera convertida a la moneda de venta; la diferencia se guarda como ajuste visible y auditable. Diferencias superiores se rechazan. Para una venta a crédito se envía `customerId` y un pago con método `credit`; el monto del pago debe estar en moneda de la venta, pero el saldo de la cuenta siempre se registra en USD. Una venta en VES se convierte con la tasa vigente confirmada en esa venta y valida el límite en USD. Los abonos parciales se registran en `POST /api/v1/customers/{customerId}/payments`; aceptan USD o VES y convierten el importe VES a USD con la tasa vigente al registrar el abono. Cada movimiento guarda monto y moneda recibidos, importe USD aplicado y tasa usada. La consulta de clientes y cuentas devuelve el saldo en USD y su equivalente VES a la tasa vigente.

## Venta e idempotencia

- `201` confirma una venta nueva; `200` devuelve el identificador original al repetir la misma clave y contenido.
- `409` indica clave ya usada con contenido diferente o stock insuficiente.
- `401` indica token ausente o inválido; `422` indica una regla de negocio no satisfecha.
- El identificador del operador y terminal se toma del token. El cliente no puede elegirlos en la venta.
- `Idempotency-Key` tiene entre 8 y 128 caracteres. El índice único es `(TerminalId, Key)`.
- Venta, líneas, pagos, reserva idempotente, auditoría y actualización condicional del stock se confirman en una transacción PostgreSQL.

## Roles, movimientos y límites actuales

El servicio aplica permisos de cajero, supervisor y administrador. Los cajeros pueden consultar la tasa vigente; solo supervisores y administradores pueden cambiarla. Las salidas de efectivo no pueden superar el efectivo esperado; el arqueo y los movimientos de inventario/caja conservan actor, motivo y auditoría. Los cambios de precio guardan historial y las ventas a crédito validan el límite disponible. El reporte de inventario usa precio de venta, no costo ni margen. Cada línea convertida conserva el precio y moneda originales, el factor de conversión y la tasa usada; el servidor rechaza la venta si la tasa cambió desde que se calculó el carrito.

El alta inicial se oculta fuera de Development. Siguen fuera de alcance emparejamiento de terminales, TLS/certificados, devoluciones, cálculos e impresión fiscal y autorización temporal ligada a una operación.
