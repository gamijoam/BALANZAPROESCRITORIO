# Finance 1.0.0

Estado: paquete fuente listo; publicación firmada pendiente de correr en `main` con el runner privado de firma.

## Qué incluye

- Registro de proveedores y activación/desactivación sin borrar su historial.
- Borradores de facturas y notas de débito con líneas, descuentos, impuestos, moneda, tasa, vencimiento y flete del documento.
- Contabilización idempotente de la obligación y recepción parcial o total de artículos.
- Actualización del inventario y del costo promedio desde el servicio del POS. El escritorio solo usa la API HTTP.
- Abonos parciales en USD o VES, con principal en la moneda de la factura, tasa aplicada y diferencia cambiaria.
- Reversos auditables para pagos, recepciones y facturas.
- Resumen preliminar de cuentas por pagar por moneda.

## Reglas aprobadas

- El costo promedio se guarda en la moneda base del producto.
- El costo de inventario usa el precio neto de los artículos. No capitaliza flete ni impuestos.
- El sistema conserva moneda y tasa de la factura. Un pago en otra moneda convierte el principal con la tasa vigente del POS y registra aparte la diferencia cambiaria.
- Las recepciones parciales actualizan solo las cantidades recibidas.
- Una recepción se puede reversar únicamente mientras no haya movimientos posteriores en los artículos afectados y las existencias coincidan con la fotografía tomada al recibir. Si ya hubo ventas, ajustes u otros movimientos, el servidor la rechaza para proteger el inventario.
- Los reversos agregan asientos compensatorios; no editan ni borran las operaciones originales.
- El módulo no constituye un sistema de contabilidad general ni certifica documentos fiscales.

## Requisitos

- Actualizar primero el POS core a una versión que incluya la política de costos por compra y la migración core `Hito29ReversiblePurchaseReceipts`.
- El servidor POS debe ejecutar PostgreSQL y aceptar módulos firmados con la clave pública `pos-publisher-vps-2026`.
- Para recibir en almacenes, el módulo `warehouses` debe estar instalado y la terminal debe tener una asignación válida. Sin esa extensión, la recepción conserva el inventario global del POS.

## Permisos

| Permiso | Uso |
|---|---|
| `Supplier.View` | Consultar proveedores y entrar al directorio. |
| `Supplier.Manage` | Crear, activar y desactivar proveedores. |
| `Purchase.View` | Consultar facturas, recepciones y saldos. |
| `Purchase.Manage` | Crear/editar borradores, contabilizar, recibir y reversar compras. |
| `SupplierPayment.Manage` | Registrar y reversar abonos; protegido por PIN de administrador según la política del POS. |
| `Finance.Reports` | Consultar el resumen preliminar de cuentas por pagar. |

## Instalar cuando el ZIP firmado esté publicado

1. En el POS servidor, abre **Administración → Módulos**.
2. Selecciona el ZIP firmado `finance-1.0.0.zip` y valida la firma, compatibilidad y migración.
3. Instala el módulo y reinicia el servidor desde el POS.
4. Comprueba que `finance` aparece activo. El módulo crea su esquema aislado; conserva sus datos si se desactiva.

No instales un ZIP sin firma ni copies una clave privada a la computadora del comercio. El flujo oficial está descrito en [publicar módulos](../../../docs/security/publicar-modulos-desde-cualquier-equipo.md).
