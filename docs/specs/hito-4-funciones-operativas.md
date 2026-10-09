# Hito 4 — Funciones operativas

## Alcance implementado

El cliente WPF y la API implementan los flujos de inventario, caja, catálogo/precios, tasa de cambio, clientes, cuentas por cobrar, reportes y administración básica de operadores. PostgreSQL conserva los registros operativos y sus auditorías. Las rutas están enumeradas en [el contrato API v1](contrato-api-v1.md).

### Criterios de aceptación

- **Inventario:** al crear un producto se registra el stock inicial; un ajuste exige cantidad y motivo. Cada cambio crea un movimiento inmutable con actor y fecha. La actualización de stock, movimiento y auditoría se confirma de forma atómica.
- **Caja:** solo se abre en USD o VES. Esa moneda fija los totales de venta, pagos y fiados del turno. Entrada/salida de efectivo exige monto y motivo. No se permite retirar más efectivo que el saldo esperado. El cierre requiere el conteo físico y conserva saldo esperado, contado y diferencia. Ventas y cierre serializan el acceso al turno.
- **Catálogo y precios:** se pueden crear y activar/desactivar productos. Un cambio de precio exige motivo y conserva historial con valores anterior/nuevo y auditoría.
- **Tasa y precios mixtos:** se pueden consultar y registrar tasas USD/VES con fecha, vigencia y fuente. Productos USD y VES pueden coexistir en el carrito; el cliente muestra cada precio convertido a la moneda de caja y el servidor lo vuelve a calcular. Si la tasa cambia durante el cobro, el servidor rechaza la confirmación y el cliente actualiza los importes para revisión. Cada línea guarda el precio/moneda original y la tasa usada.
- **Clientes y fiado:** un crédito requiere cliente; se valida el límite y se registra el cargo asociado a la venta en la misma transacción. Los abonos actualizan el ledger y el saldo se obtiene de sus movimientos.
- **Reportes y auditoría:** están disponibles resumen de ventas, valorización de inventario y consulta de eventos auditados con permisos.
- **Seguridad:** la API aplica permisos de cajero, supervisor y administrador. Desactivar un operador revoca sesiones y no se permite desactivar/degradar al último administrador activo.

## Roles iniciales

| Rol | Capacidades principales |
|---|---|
| Cajero | Confirmar ventas y cerrar caja; consulta de productos y flujos básicos de caja |
| Supervisor | Capacidades de operación, inventario, catálogo/precios, clientes/fiado, tasa y reportes |
| Administrador | Todos los permisos, incluida administración de operadores y reportes sensibles |

Los permisos se consultan en el servidor para cada solicitud. Cajeros pueden consultar tasas, pero no cambiarlas. Cambiar un rol tiene efecto en sesiones activas; desactivar un operador revoca las suyas. Los permisos concretos se mantienen en `RolePermissionPolicy` y la [matriz de permisos](matriz-permisos.md).

## Reglas y límites

- Los movimientos financieros y de inventario no se editan ni eliminan como mecanismo de corrección; se registran contramovimientos.
- La valorización de inventario usa precio de venta vigente; no representa costo, utilidad ni margen.
- Los pagos y fiados se registran en la moneda de la caja; aceptar pagos en divisa distinta al turno y llevar saldos de caja separados por moneda queda para una fase posterior.
- El fiado registra saldo por cobrar; no se han implementado vencimientos, intereses, cuotas ni reportes de antigüedad.
- Los reportes son consultas básicas; no hay exportación ni consolidación multicaja avanzada.
- El comprobante sigue siendo de prueba, sin cálculos, numeración ni certificación fiscal.

## Verificación

- Pruebas de dominio: 55 aprobadas. Pruebas de aplicación: 4 aprobadas. Pruebas de integración contra PostgreSQL: 8 aprobadas, incluyendo conversión en ambos sentidos, detección de tasa cambiada, snapshots/auditoría y atomicidad del crédito.
- `scripts/Test-LocalCheckout.ps1` ejecutó un smoke HTTP de API contra PostgreSQL local: RBAC, libro de inventario, arqueo, catálogo/precios, tasa, cliente, fiado/abono, reporte, auditoría e idempotencia; el script elimina los datos temporales al finalizar.
- `dotnet build Pos.sln -c Release`: correcto, cero advertencias y cero errores. La compilación valida los XAML de WPF; la inspección visual manual no se realizó en este entorno.
