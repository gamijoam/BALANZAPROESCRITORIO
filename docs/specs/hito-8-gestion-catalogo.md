# Hito 8 — Gestión práctica del catálogo

## Objetivo

Permitir que personal autorizado encuentre y mantenga productos sin alterar ventas ni movimientos históricos. La API sigue siendo autoridad de permisos, validación, unicidad y auditoría.

## Historias y criterios de aceptación

### Buscar y filtrar inventario

- Dado un usuario con permiso de inventario, cuando consulta Inventario, entonces puede buscar por coincidencia de SKU o nombre y filtrar por estado, moneda, disponibilidad de existencia y presencia de foto.
- El permiso de consulta de inventario es independiente de `AdjustStock`; autoriza consultas de existencias e historial de movimientos, y no autoriza ajustes.
- Dado un producto inactivo, cuando se filtra por inactivos o todos, entonces se puede consultar y seleccionar para reactivarlo.
- Dado un usuario sin permiso, cuando solicita inventario, entonces el servicio rechaza la petición.
- Los filtros se combinan y las consultas inválidas o demasiado largas se rechazan con un error claro.

### Editar SKU y nombre

- Dado un producto existente, cuando un usuario con permiso guarda un SKU y nombre válidos, entonces se actualiza el catálogo y se registra actor, fecha y valores modificados en auditoría.
- Si el SKU ya pertenece a otro producto, se rechaza el cambio sin modificar el producto ni su historial.
- Si el SKU o nombre incumple límites, se rechaza el cambio sin mutaciones.
- Cambiar SKU/nombre no reescribe líneas históricas de venta ni movimientos.

### Activar y desactivar

- Dado un producto activo, cuando el usuario confirma desactivarlo, entonces deja de ofrecerse en búsqueda y venta, pero permanece visible en inventario filtrando inactivos.
- Dado un producto inactivo, cuando se reactiva con permiso, entonces vuelve a aparecer en búsqueda y venta.
- Cada transición registra actor, estado anterior/nuevo y fecha en auditoría; no se eliminan datos relacionados.

## Alcance visual

- Barra superior: búsqueda, estado, moneda, existencia y foto; controles para aplicar/limpiar filtros.
- Tabla con SKU, nombre, existencia, precio, moneda, estado y foto.
- Panel de producto mantiene la vista de foto; agrupa edición, estado, precio, movimientos e inventario.
- Mensajes de estado, errores de validación y confirmación de desactivación en español.

## Riesgos y fuera de alcance

- No editar precios mediante el formulario general: se conserva el flujo existente con motivo e historial.
- No borrar productos; desactivar conserva referencias y trazabilidad.
- Categorías, unidades, códigos de barras alternos, costos, niveles mínimos y paginación quedan fuera de este hito.
- No se agrega una migración si SKU, nombre y estado actuales cubren la historia.

## Pruebas TDD

Antes de implementar, las pruebas del cliente API verifican consulta con filtros, edición y activación/desactivación autenticadas, además del tratamiento de errores. Se complementarán con integración PostgreSQL para filtros combinados, unicidad y auditoría.
