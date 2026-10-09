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
| `GET /api/v1/cash-sessions/current` | Bearer | Devuelve el turno abierto de la terminal, o `null` |
| `POST /api/v1/cash-sessions` | Bearer | Abre turno de caja para la terminal de la sesión |
| `POST /api/v1/cash-sessions/{cashSessionId}/close` | Bearer | Cierra el turno propio abierto |
| `POST /api/v1/cash-sessions/{cashSessionId}/movements` | Bearer con permiso | Registra entrada o salida justificada de efectivo |
| `GET /api/v1/products?query={texto}` | Bearer | Busca productos activos por SKU o nombre (hasta 30 resultados) |
| `GET /api/v1/inventory` | Bearer con permiso | Consulta existencias |
| `POST /api/v1/inventory/{productId}/adjustments` | Bearer con permiso | Ajusta stock con motivo y movimiento inmutable |
| `GET /api/v1/inventory/{productId}/movements` | Bearer con permiso | Consulta historial de movimientos de inventario |
| `POST /api/v1/products` | Bearer con permiso | Crea producto con stock inicial trazable |
| `POST /api/v1/products/{productId}/price-changes` | Bearer con permiso | Cambia precio con motivo e historial |
| `GET /api/v1/products/{productId}/price-history` | Bearer con permiso | Consulta cambios de precio |
| `PATCH /api/v1/products/{productId}/active` | Bearer con permiso | Activa/desactiva producto |
| `GET/POST /api/v1/exchange-rates` | Bearer con permiso | Consulta e ingresa tasas con fecha y fuente |
| `GET /api/v1/exchange-rates/current` | Bearer con permiso | Consulta tasa vigente |
| `GET/POST /api/v1/customers` | Bearer con permiso | Busca/lista y registra clientes |
| `GET /api/v1/customers/{customerId}/receivables` | Bearer con permiso | Consulta saldo y movimientos por cobrar |
| `POST /api/v1/customers/{customerId}/payments` | Bearer con permiso | Registra abono a cuenta por cobrar |
| `GET /api/v1/reports/sales` | Bearer con permiso | Resumen de ventas por período |
| `GET /api/v1/reports/inventory` | Bearer con permiso | Valorización a precio de venta |
| `GET /api/v1/audit` | Bearer con permiso sensible | Consulta eventos de auditoría |
| `GET/POST /api/v1/operators` | Administrador | Lista y crea operadores con rol |
| `PATCH /api/v1/operators/{operatorId}/role` | Administrador | Cambia rol con auditoría |
| `PATCH /api/v1/operators/{operatorId}/active` | Administrador | Activa/desactiva y revoca sesiones |
| `POST /api/v1/sales` | Bearer, turno abierto y `Idempotency-Key` | Confirma venta, pagos, auditoría y movimiento de stock |

El alta inicial acepta `{"username":"cajero","pin":"1234"}`; el PIN debe tener de 4 a 12 dígitos. El login acepta `username`, `pin` y `terminalId` UUID. El token solo se devuelve al iniciar sesión; en PostgreSQL se conserva su hash.

Abrir caja: `{"openingBalance":0,"currency":"VES"}`; solo se acepta `VES` o `USD`. Cerrar caja requiere `{"countedClosingBalance":123.45}`; el servidor registra saldo esperado, contado y diferencia. La venta recibe `cashSessionId` y `checkout`, con líneas `{productId, quantity}`, pagos `{method, amount, currency}` y `exchangeRateId` cuando alguna línea requiere conversión. La moneda de la caja es la moneda del comprobante, los pagos y los fiados; productos en la otra moneda se convierten con la tasa USD/VES vigente. Para fiado se envía `customerId` y un pago con método `credit` o `fiado`; el servidor valida límite y registra la cuenta por cobrar en la misma transacción.

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
