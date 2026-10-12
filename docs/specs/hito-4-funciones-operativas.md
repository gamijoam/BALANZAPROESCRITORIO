# Hito 4 — Funciones operativas

## Alcance implementado

El cliente WPF y la API implementan los flujos de inventario, caja, catálogo/precios, tasa de cambio, clientes, cuentas por cobrar, reportes y administración básica de operadores. PostgreSQL conserva los registros operativos y sus auditorías. Las rutas están enumeradas en [el contrato API v1](contrato-api-v1.md).

### Criterios de aceptación

- **Inventario:** al crear un producto se registra el stock inicial; un ajuste exige cantidad y motivo. Cada cambio crea un movimiento inmutable con actor y fecha. La actualización de stock, movimiento y auditoría se confirma de forma atómica.
- **Caja:** el turno se abre siempre con VES como moneda principal; la interfaz no pregunta cuál elegir. El saldo inicial puede registrarse por separado en VES y USD. Los pagos y movimientos conservan la moneda en que se reciben, de modo que los cobros en USD se reflejan y concilian en USD y los de VES en VES. Entrada/salida de efectivo exige monto y motivo. No se permite retirar más efectivo que el saldo esperado. El cierre requiere el conteo físico y conserva saldo esperado, contado y diferencia para ambas monedas. Ventas, relevo y cierre serializan el acceso al turno.
- **Catálogo y precios:** se pueden crear y activar/desactivar productos. Un cambio de precio exige motivo y conserva historial con valores anterior/nuevo y auditoría.
- **Precio especial en venta:** al seleccionar un artículo del carrito se puede cambiar su cantidad o aplicar un precio especial desde el mouse. El precio especial es solo para esa venta; exige motivo y autorización administrativa vinculada a la confirmación. El servicio guarda el precio aplicado en la línea de venta y el precio/motivo en auditoría, sin cambiar el precio de catálogo.
- **Tasa y precios mixtos:** se pueden consultar y registrar tasas USD/VES con fecha, vigencia y fuente. Productos USD y VES pueden coexistir en el carrito; el cliente muestra cada precio convertido a la moneda de caja y el servidor lo vuelve a calcular. Si la tasa cambia durante el cobro, el servidor rechaza la confirmación y el cliente actualiza los importes para revisión. Cada línea guarda el precio/moneda original y la tasa usada.
- **Pagos mixtos y redondeo:** una venta puede pagarse en VES y USD aunque el turno se haya abierto en una de esas monedas. Se guardan los importes realmente recibidos por moneda y la tasa aplicada. Se acepta únicamente la diferencia de hasta medio centavo de la moneda convertida, redondeado en la moneda de venta; el ajuste queda guardado en la venta, auditoría y reportes. El arqueo cuenta el efectivo recibido en su moneda real.
- **Clientes y fiado:** un crédito requiere cliente; se valida el límite y se registra el cargo asociado a la venta en la misma transacción. Los abonos actualizan el ledger y el saldo se obtiene de sus movimientos.
- **Reportes y auditoría:** incluye resumen de ventas, consulta detallada con filtros aplicados antes de paginar, ventas agrupadas por producto con unidades/importes devueltos y netos, arqueo de caja por moneda con cobros/reintegros por método y movimientos del turno, valorización de inventario, exportación CSV y consulta de eventos auditados con permisos.
- **Seguridad:** la API aplica permisos de cajero, supervisor y administrador. Desactivar un operador revoca sesiones y no se permite desactivar/degradar al último administrador activo.
- **Cambio de operador:** con caja abierta y carrito vacío, el operador puede liberar el turno y volver al login sin cerrarlo. El turno conserva quién lo abrió; el nuevo operador debe iniciar sesión y reclamarlo. El servidor permite un solo operador activo por turno y terminal, y cada venta, movimiento, relevo y cierre conserva su actor en el registro operativo/auditoría. Un operador sin el turno activo no puede cobrar, hacer movimientos ni cerrarlo, y no recibe saldos esperados.
- **Estado de turno:** si existe una caja abierta en la terminal, el POS la muestra como abierta y distingue quién la abrió y quién la tiene activa. Si el turno pertenece a otra sesión activa, se explica quién lo usa y se bloquea el cobro con un mensaje claro; no se muestra un error genérico de apertura ni se invita a crear otra caja.
- **Cierre de sesión:** sin caja abierta, el operador puede cerrar su sesión y volver al login. Con caja activa, la acción se presenta como cambio de operador y requiere confirmación. El relevo se bloquea si hay artículos en el carrito, una venta pendiente o una operación ocupada; nunca cierra la caja ni descarta la venta por cuenta propia.
- **Pruebas automatizadas:** relevo libera y reclama el mismo turno; dos operadores no pueden usarlo simultáneamente; una venta queda atribuida al operador activo; cierre y movimientos exigen que el operador tenga el turno activo; inicio con turno ocupado muestra su estado y no permite cobrar.

## Roles iniciales

| Rol | Capacidades principales |
|---|---|
| Cajero | Confirmar ventas y cerrar caja; consulta de productos y flujos básicos de caja |
| Supervisor | Capacidades de operación, inventario, catálogo/precios, clientes/fiado, tasa y reportes |
| Administrador | Todos los permisos, incluida administración de operadores y reportes sensibles |

Los permisos se consultan en el servidor para cada solicitud. Cajeros pueden consultar tasas, pero no cambiarlas. Cambiar un rol tiene efecto en sesiones activas; desactivar un operador revoca las suyas. Los permisos concretos se mantienen en `RolePermissionPolicy` y la [matriz de permisos](matriz-permisos.md). Cada operador puede heredar, permitir o bloquear permisos de su rol; también se puede exigir un PIN de administrador para una acción concreta.

### Permisos configurables y autorización puntual

- **Criterios de aceptación:** desde Administración > Operadores y permisos, un administrador puede elegir un operador y configurar cada permiso como heredado, permitido, bloqueado o con PIN administrativo. Los cambios se guardan y auditan en el servidor; el rol Administrador no se puede editar ni bloquear.
- **Criterios de aceptación:** si una operación requiere PIN, el servicio valida credenciales de un administrador activo y autoriza solo la solicitud exacta (operador, terminal, permiso, ruta, contenido y operación). La autorización vence a los 60 segundos, se consume una sola vez y no sirve para otra acción ni para cambiar los datos enviados.
- **Criterios de aceptación:** una autorización inválida, vencida o reutilizada no ejecuta la operación. Una aprobación válida permite que el cliente reenvíe una sola vez la misma solicitud original.
- **Pruebas automatizadas:** resolución de permisos configurables, uso único y vinculación de autorización a operador/terminal/permiso/ruta/contenido, y reintento de una operación HTTP autorizada.

### Correcciones de permisos y selección del carrito

- Un operador Cajero que hereda los permisos del rol puede confirmar ventas y ventas a crédito; una anulación explícita `Deny` debe ser la causa para bloquear esas acciones. `RequireAdminPin` solicita autorización sin terminar el POS.
- Las tablas enlazadas a un elemento seleccionado usan selección por fila (`FullRow`) cuando el código trabaja con `SelectedItem`; seleccionar un artículo del carrito o abrir la matriz de permisos no debe provocar una excepción WPF.
- **Pruebas automatizadas:** el rol Cajero hereda `ConfirmSale` y `CreateCreditSale`; las vistas del carrito y de permisos no combinan `SelectedItem` con `SelectionUnit="Cell"`.

La autorización puntual no reemplaza el rol ni entrega una sesión de administrador. Los administradores conservan todos sus permisos y sus permisos no son configurables para evitar un bloqueo accidental.

## Reglas y límites

- Los movimientos financieros y de inventario no se editan ni eliminan como mecanismo de corrección; se registran contramovimientos.
- La valorización de inventario usa precio de venta vigente; no representa costo, utilidad ni margen.
- Los pagos mixtos se mantienen por moneda de recepción; el cambio de moneda no sustituye ni modifica el importe que se recibió físicamente.
- El fiado registra saldo por cobrar; no se han implementado vencimientos, intereses, cuotas ni reportes de antigüedad.
- La valorización de inventario y los reportes de ventas no calculan costo ni margen; tampoco hay consolidación multicaja avanzada ni reportes de antigüedad de créditos.
- El comprobante sigue siendo de prueba, sin cálculos, numeración ni certificación fiscal.

## Verificación

- Pruebas de dominio: 55 aprobadas. Pruebas de aplicación: 4 aprobadas. Pruebas de integración contra PostgreSQL: 8 aprobadas, incluyendo conversión en ambos sentidos, detección de tasa cambiada, snapshots/auditoría y atomicidad del crédito.
- `scripts/Test-LocalCheckout.ps1` ejecutó un smoke HTTP de API contra PostgreSQL local: RBAC, libro de inventario, arqueo, catálogo/precios, tasa, cliente, fiado/abono, reporte, auditoría e idempotencia; el script elimina los datos temporales al finalizar.
- `dotnet build Pos.sln -c Release`: correcto, cero advertencias y cero errores. La compilación valida los XAML de WPF; la inspección visual manual no se realizó en este entorno.
