# Hito 35 — Mínimo de inventario y antigüedad de cartera

## Estado

**Aprobado para implementación por el usuario el 16 de octubre de 2026**, al pedir aplicar las mejoras recomendadas para inventario y cuentas por cobrar.

## Historias

- Como responsable de inventario, quiero definir un mínimo por producto y consultar cuáles llegaron a ese nivel para organizar la reposición.
- Como responsable de cobranza, quiero ver los saldos por vencer y vencidos según su antigüedad para priorizar el seguimiento.
- Como operador, quiero indicar el vencimiento de una venta a crédito antes de confirmarla, usando 30 días como propuesta inicial.

## Criterios de aceptación

- El mínimo se configura al crear o editar un producto, acepta hasta tres decimales y empieza en cero para productos existentes. Cero desactiva la alerta.
- Inventario incluye el filtro **Bajo mínimo**, muestra el conteo actual y Reportes avanzados agrega **Productos bajo mínimo** con existencia, mínimo y unidades para llegar al mínimo. El reporte incluye productos activos.
- El formulario de cobro muestra el vencimiento solo si hay un pago a crédito. Propone 30 días, permite cambiar la fecha y conserva la misma fecha al reintentar una venta pendiente.
- El cargo de crédito guarda el vencimiento como fecha calendario. La cuenta del cliente muestra esa fecha.
- Reportes avanzados agrega **Antigüedad de cuentas por cobrar** con saldos por vencer, 1–30, 31–60, 61–90 y más de 90 días, además de saldos sin vencimiento.
- La antigüedad se calcula a la fecha local del POS. Los abonos se aplican por FIFO, desde el cargo más antiguo, antes de sumar los saldos pendientes por tramo.
- Los cargos existentes no reciben fechas estimadas: siguen vigentes con `DueDate` nulo y aparecen como **Sin vencimiento**.
- El servidor conserva la autoridad sobre productos, cargos e inventario. El escritorio y el módulo consultan únicamente las APIs autorizadas.
- La migración `Hito35StockMinimumAndReceivableDueDate` agrega las dos columnas sin alterar existencias ni saldos anteriores. El umbral llega con valor cero y la fecha llega nula.
- Reportes avanzados se publica como versión 1.7.0 y requiere Desktop API 1.6.

## Pruebas candidatas

- Crea y edita mínimos en cero, con decimales y con valores inválidos.
- Incluye en el reporte productos activos con existencia igual o inferior al mínimo; excluye mínimos cero.
- Confirma una venta a crédito con fecha editada y conserva la fecha y clave de idempotencia al reintentar.
- Calcula por FIFO pagos parciales sobre cargos con fechas distintas y muestra cada saldo pendiente en el tramo correcto.
- Conserva cargos históricos en "Sin vencimiento" y no inventa fechas.
- Rechaza el reporte a operadores sin permiso `Reports.View`.

## Restricciones

- Este hito no crea órdenes de compra automáticas ni cambia el stock al llegar al mínimo.
- No cambia el saldo del cliente ni registra asignaciones de abonos nuevas en el libro contable; FIFO define cómo se presenta la deuda pendiente en el reporte.
- Los montos de cartera permanecen normalizados a USD; las cantidades de inventario y los importes continúan usando `decimal`.
