# Hito 18 — Inventario por almacén

## Objetivo

Hacer que Inventario muestre cantidades y aplique filtros de disponibilidad según el almacén seleccionado en la extensión Almacenes. “Almacén: todos” conserva la cantidad global del POS.

## Criterios de aceptación

- La consulta del anfitrión conserva búsqueda, estado, moneda y foto, pero no elimina productos usando la cantidad global antes de aplicar extensiones.
- El módulo sustituye la cantidad del catálogo por la del almacén elegido y conserva productos con saldo cero.
- Después de las extensiones, el anfitrión aplica “Con existencia” o “Agotados” usando la cantidad que muestra la tabla.
- Con “Almacén: todos”, sin módulo o sin extensión autorizada, la cantidad y los filtros siguen usando el inventario global.
- La pantalla no recibe el almacén como dato de autoridad para ventas; F2 y la validación de venta continúan usando el almacén de la sesión autenticada en el servidor.
- No se agregan migraciones ni cambian saldos, endpoints HTTP o permisos del módulo.

## Pruebas TDD

- Una prueba de diseño confirma que la pantalla solicita el catálogo sin filtro global de stock, ejecuta extensiones y luego filtra la disponibilidad.
- Pruebas del filtro cubren global `10`, almacén A `0` y almacén B `4`: A aparece agotado con `0`, B disponible con `4`, y “todos” conserva `10`.
- Sin extensión, el filtro global conserva sus resultados.
- La firma del ZIP 1.3.2 y la compilación del POS anfitrión deben verificarse antes de distribución.

## Entrega

El cambio anfitrión requiere actualizar el POS en el servidor y las terminales. La corrección del filtro se distribuye en el módulo firmado `warehouses` 1.3.2; conserva la migración existente `warehouses:0001`.
