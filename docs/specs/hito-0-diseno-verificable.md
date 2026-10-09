# Hito 0 Diseño verificable

## Objetivo y límite

Convertir la especificación en límites, contratos y criterios verificables antes de implementar funciones. El Hito 0 no contiene lógica de ventas ni reglas de negocio implementadas.

**Estado:** completado y aprobado por el propietario el 8 de octubre de 2026. El Hito 1 ya se inició con esa aprobación.

## Mapa de módulos

| Módulo | Responsabilidad inicial | Dependencias a través de contratos |
|---|---|---|
| Catalog | Productos, códigos, categorías, unidades | Pricing, Inventory |
| Pricing | Precios, monedas, tasas e impuestos configurables | Sales |
| Inventory | Existencias, movimientos, ajustes y mínimos | Catalog, Sales |
| Sales | Carrito confirmado, venta, reversos e idempotencia | Catalog, Pricing, Inventory, Cash, Identity |
| Cash | Turnos, arqueos y movimientos de caja | Sales, Identity |
| Customers | Clientes, fiados y abonos | Sales, Identity |
| Identity | Usuarios, roles, permisos, sesión y autorización puntual | Todos los casos de uso protegidos |
| Reporting | Consultas esenciales y exportación | Módulos mediante consultas/contratos aprobados |
| Licensing | Derechos de funciones opcionales | Server, módulos protegidos |
| Operations | Terminales, dispositivos, diagnóstico y respaldos | Host/Infrastructure |

El orden es un mapa lógico; no obliga a crear un proyecto por cada módulo antes de que exista una historia que lo necesite. El MVP no depende de licencias premium para vender, revisar stock o cerrar caja.

## Historias y criterios iniciales

### Venta e idempotencia

- **Dado** un cajero autorizado, una caja abierta y stock disponible, **cuando** confirma una venta con una clave de idempotencia nueva, **entonces** el servicio registra una sola venta, pagos, movimientos de stock y auditoría en una transacción.
- **Dado** el mismo comando y la misma clave, **cuando** el cliente reintenta tras un timeout, **entonces** obtiene el identificador original sin duplicar cobro ni descontar stock otra vez.
- **Dada** una clave ya usada, **cuando** llega un contenido distinto, **entonces** el servicio responde conflicto y no modifica saldos.

### Concurrencia de existencias

- **Dada** una unidad disponible y dos cajas, **cuando** intentan confirmar ventas concurrentes, **entonces** solo una confirma si stock negativo está desactivado; la otra recibe un error de disponibilidad y no registra parcialmente la venta.

### Dinero y tasa

- **Dado** un pago mixto y una regla de redondeo configurada, **cuando** se confirma el cobro, **entonces** el total pagado y el vuelto cuadran exactamente en `decimal` y el documento conserva moneda, escala, tasa, impuestos y snapshots aplicados.
- **Dada** una venta confirmada con una tasa, **cuando** se registra una tasa nueva, **entonces** la venta histórica conserva la tasa original.

### Permisos y autorización

- **Dado** un cajero sin permiso para cambiar un precio, **cuando** intenta hacerlo sin aprobación válida, **entonces** el servicio rechaza la acción y audita el intento según política.
- **Dada** una aprobación de supervisor ligada a una operación específica, **cuando** se intenta reutilizar para otra operación, **entonces** el servicio la rechaza.

### Impresión, devoluciones y operación

- **Dada** una venta ya confirmada, **cuando** falla la impresora, **entonces** la venta permanece registrada y una reimpresión no crea otra venta.
- **Dada** una devolución autorizada, **cuando** se confirma, **entonces** queda ligada a la venta original y genera movimientos de reverso auditables.
- **Dado** un comercio operando localmente, **cuando** se interrumpe internet pero servicio y PostgreSQL siguen disponibles, **entonces** las operaciones locales continúan.
- **Dada** una caja cliente sin conexión al servidor, **cuando** intenta confirmar una venta, **entonces** no presenta la venta como confirmada ni inventa un modo offline.

## Estructura inicial de solución

La solución `Pos.sln` contiene proyectos anfitriones Desktop y Server, contratos compartidos y BuildingBlocks. También se preparan proyectos de pruebas para dominio, aplicación, integración, arquitectura y E2E. Los módulos se incorporarán en proyectos separados a medida que se aprueben historias y sus límites concretos; este Hito 0 no crea implementaciones de dominio.

## Plan de calidad y CI

La integración continua en Windows debe restaurar, compilar, ejecutar pruebas existentes y guardar resultados de test. Las pruebas de persistencia/concurrencia usarán PostgreSQL real mediante Testcontainers cuando se apruebe el Hito 2. Las mutaciones Stryker.NET se ejecutarán después del primer green de las historias, de forma focalizada por módulo. La definición de pipeline se encuentra en `.github/workflows/ci.yml`.

## Gate de salida

El propietario autorizó iniciar el Hito 1. Las decisiones de moneda/impuestos, tasa, fiados, dispositivos y operación siguen abiertas y están registradas para validarse antes de fijar persistencia y despliegue.
