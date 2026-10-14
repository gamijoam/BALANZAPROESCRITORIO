# Hito 22 — Reportes de cartera y cobros por caja

## Criterios de aceptación

- El módulo Reportes avanzados agrega un reporte de saldos actuales por cliente con deuda positiva. Cada fila muestra saldo USD, equivalente VES a la tasa vigente, límite, crédito disponible y último movimiento.
- El reporte de movimientos lista cargos y abonos entre `fromUtc` inclusivo y `toUtc` exclusivo, con límite de 92 días, paginación máxima de 250, cliente, tipo, USD, importe/moneda originales, tasa aplicada, referencia, venta asociada y operador.
- Ambas consultas se autorizan con `ViewReports`, calculan los datos en el servicio y no leen PostgreSQL desde WPF. No dependen de la lista de clientes limitada a 100 ni hacen una consulta por cliente.
- Un tercer reporte muestra por turno/caja el detalle resumido de cobros y reintegros agrupados por operación, método y moneda. No se presenta como un detalle cronológico por pago.
- Las tres fuentes conservan búsqueda, selección/orden de columnas, vistas personales y exportación CSV/XLSX.
- El paquete del módulo incrementa a 1.1.0. El anfitrión incrementa Desktop API de 1.3.0 a 1.4.0 para declarar los contratos tipados de consulta. No se agrega migración.

## Pruebas TDD

- Probar clientes HTTP tipados, serialización, autorización de rutas, filtros y límites.
- Probar consulta PostgreSQL con más de 100 clientes, balances positivos, conversión vigente, movimientos en bordes UTC y filtro de moneda original.
- Probar el aplanado de cobros/reintegros por caja y método, y mantener reportes y paquetes existentes compatibles.
- Verificar exportación con las filas y columnas visibles y el manifiesto/versión del módulo.

## Límites

- El saldo VES es un equivalente a tasa actual; el saldo contable se mantiene en USD.
- No se calcula antigüedad vencida, interés, cuotas ni asignación de abonos a cargos.
- El desglose de caja se agrupa por sesión, no por fecha individual de cada pago.
