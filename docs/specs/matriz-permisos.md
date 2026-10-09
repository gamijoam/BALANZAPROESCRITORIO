# Matriz inicial de permisos

Los roles son perfiles iniciales; la autorización efectiva se expresa por permiso y se valida en el servicio. Ningún PIN compartido o autorización temporal general está permitido.

| Acción | Cajero | Supervisor | Administrador | Condiciones/auditoría |
|---|---:|---:|---:|---|
| Buscar productos y consultar precios vigentes | Sí | Sí | Sí | Respetar acceso a costo/margen |
| Abrir y cerrar su turno de caja | Sí | Sí | Sí | Arqueo por medio de pago |
| Confirmar venta dentro de política | Sí | Sí | Sí | Caja abierta; idempotencia; auditoría |
| Descuento dentro del límite del rol | Configurable | Sí | Sí | Guardar actor y monto aplicado |
| Descuento fuera del límite | No | Autoriza puntualmente | Autoriza puntualmente | Aprobación ligada a venta/acción |
| Cambiar precio o costo | No | Según permiso explícito | Sí | Motivo y valores anterior/nuevo auditados |
| Devolución/anulación | No por defecto | Sí | Sí | Referencia a venta, motivo y contramovimiento |
| Ajuste manual de inventario | No | Según permiso explícito | Sí | Motivo, cantidad y actor; movimiento inmutable |
| Crear/modificar tasa | No | Según política | Sí | Origen, vigencia, responsable y valores auditados |
| Consultar tasa vigente para calcular precios | Sí | Sí | Sí | La venta conserva la tasa y el factor usados |
| Autorizar fiado/abono/cambio de límite | Según límite | Sí | Sí | Cliente, saldo y decisión registrados |
| Administrar usuarios/roles/permisos | No | No por defecto | Sí | Auditoría de cambios |
| Ver reportes sensibles/exportar | Propio/limitado | Según permiso | Sí | Ámbito y exportación auditados |
| Configurar respaldos/restaurar | No | No | Sí | Restauración requiere evento y comprobación |
| Administrar licencia/terminal/emparejamiento | No | No por defecto | Sí | Auditar terminal y cambios de derechos |

Antes de Hito 1 se debe validar si los supervisores podrán abrir/cerrar cajas ajenas, anular ventas ya cerradas y revisar reportes de todas las terminales. No existe un permiso implícito porque alguien tenga un PIN.
