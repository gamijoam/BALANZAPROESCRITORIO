# Hito 17 — Existencias de F2 por almacén de terminal

## Alcance aprobado

El servidor POS resuelve el almacén a partir de la sesión autenticada. La búsqueda de productos conserva el catálogo y el contrato HTTP existente; `quantity` comunica el saldo disponible para la terminal.

## Criterios de aceptación

- Sin las tablas de Almacenes, F2 devuelve la existencia global del POS como antes.
- Con las tablas instaladas, cada terminal recibe el saldo de su almacén asignado, aunque la interfaz del módulo esté desactivada.
- Una terminal sin asignación usa el almacén principal.
- Los productos sin una fila de saldo en el almacén correspondiente se devuelven con `quantity: 0`; siguen visibles y el POS impide agregarlos.
- El cliente no puede escoger el almacén que determina su saldo. La identidad de terminal viene de la sesión autenticada.
- La confirmación de venta conserva la validación autoritativa de existencias del servidor.
- El cambio no agrega migraciones ni modifica saldos, ventas o la base de datos activa.

## Pruebas

`ProductSearch_UsesAuthenticatedTerminalsWarehouseAndFallsBackToGlobalStockWithoutModule` cubre el fallback global antes de crear las tablas de Almacenes, dos almacenes con cantidades distintas, terminal sin asignación y saldo cero. Crea una base temporal con `POS_MODULE_VALIDATION_ADMIN_CONNECTION`; nunca debe apuntar a la base activa.

Las pruebas de escritorio ya verifican que el cliente conserva `quantity` y bloquea agregar un producto agotado.

## Despliegue

Actualizar y reiniciar el servidor POS principal. No se instala un ZIP nuevo de Almacenes y no es necesario actualizar el cliente de escritorio.
