# Hito 34 — Reporte de precios y márgenes actuales

## Estado

**Aprobado para implementación por el usuario el 16 de octubre de 2026**, al pedir continuar los cambios del módulo de Reportes.

## Historia

Como administrador o supervisor con permiso de inventario, quiero comparar los tres niveles de precio vigentes con el costo unitario registrado, para revisar la ganancia y el margen bruto estimados de cada producto.

## Criterios de aceptación

- El reporte aparece dentro de **Reportes avanzados** y requiere el permiso `Reports.Inventory`, mapeado al permiso actual `ViewInventory`.
- Cada producto genera una fila para Precio 1, Precio 2 y Precio 3, incluso cuando el nivel no está configurado.
- Cada fila incluye código, producto, moneda, nivel de precio, estado del cálculo, costo unitario, precio, ganancia bruta por unidad y margen bruto porcentual.
- La ganancia por unidad es `precio - costo`. El margen bruto porcentual es `(precio - costo) / precio × 100` cuando precio y costo existen y el precio es mayor que cero.
- Un nivel sin precio configurado se indica como tal. Un costo no registrado deja la ganancia y el margen sin calcular; no se sustituye por cero. Un precio cero muestra ganancia unitaria si existe costo, pero no porcentaje de margen.
- El reporte usa el costo vigente del catálogo y no se etiqueta como utilidad histórica de ventas.
- La vista de hoja y las exportaciones mantienen los valores numéricos, filtros, columnas visibles y orden seleccionado.
- Los datos vienen del endpoint autorizado de inventario; el escritorio no accede directamente a PostgreSQL.

## Pruebas candidatas

- Calcula ganancia y margen para los tres niveles con costo y precio configurados.
- Omite ganancia y margen cuando falta costo y señala que el cálculo no está disponible.
- Identifica niveles de precio sin configurar y precio cero sin división por cero.
- Rechaza la fuente a operadores sin permiso `Reports.Inventory`.
- Exporta valores decimales numéricos y conserva el filtro por moneda.

## Restricciones

- No calcula utilidad histórica de ventas. Esa utilidad debe usar snapshots de costo por línea de venta.
- No modifica precios ni costos y no agrega migraciones.
- Los importes y porcentajes se calculan con `decimal`.
