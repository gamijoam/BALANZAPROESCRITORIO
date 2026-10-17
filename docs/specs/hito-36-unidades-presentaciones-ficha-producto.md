# Hito 36 — Unidades, presentaciones y ficha completa del producto

## Aprobación y alcance

Implementar el plan aprobado por el usuario para manejar unidades base y presentaciones de venta, abrir una ficha completa del producto desde Inventario y mostrar la presentación en la búsqueda y el carrito.

## Criterios de aceptación

1. Cada producto tiene una unidad base de inventario. Los productos existentes conservan sus cantidades y quedan con su unidad actual equivalente a factor 1.
2. El catálogo permite definir unidades reutilizables con nombre, abreviatura y si admiten cantidades fraccionarias.
3. La ficha del producto permite agregar, editar y desactivar presentaciones con etiqueta, unidad, factor hacia la unidad base, precios 1/2/3 opcionales y código de barras opcional.
4. El factor es específico de cada producto y presentación. No se asume que todas las cajas o pares contienen lo mismo.
5. Inventario, almacenes, ajustes, compras y mínimos siguen expresando cantidades en la unidad base del producto.
6. Al vender una presentación, el servidor valida su relación con el producto, calcula el consumo en unidad base, valida stock y registra la venta en una transacción.
7. Las ventas conservan una instantánea de etiqueta, factor y cantidad base consumida. Las devoluciones reponen el equivalente base de la cantidad devuelta, sin recalcular con la configuración actual del producto.
8. Buscar un producto muestra su unidad base y presentaciones disponibles. El carrito, recibo e historial identifican la presentación vendida.
9. Las ventas y cantidades históricas existentes no se reinterpretan. Los campos nuevos de sus líneas usan factor 1 y la unidad histórica genérica.
10. El doble clic sobre una fila de Inventario abre la ficha completa. También existe una acción visible para abrirla sin doble clic.
11. Los cambios de ficha y presentaciones requieren el permiso de catálogo y generan eventos de auditoría.
12. El flujo común de unidades no exige escribir abreviaturas ni llenar precios y códigos opcionales; esos datos quedan en opciones avanzadas.
13. Al elegir una presentación conocida, el programa propone su equivalencia habitual (Par = 2, Docena = 12). Para cajas y paquetes de contenido variable, pide solo cuántas unidades base contiene.
14. La búsqueda y el carrito muestran el nombre corto de la forma de venta, como “Par”; la equivalencia completa queda visible en la ficha del producto.

## Reglas de datos

- Unidad base: unidad en la que se guarda la existencia central y por almacén.
- Presentación: cantidad vendible expresada en una unidad y convertida por un factor específico del producto; por ejemplo, una caja puede consumir 12 unidades de un SKU y 6 de otro.
- Si no se define un precio para una presentación, se calcula desde el precio de la unidad base por el factor. El precio calculado queda sujeto a revisión y se puede reemplazar por un precio explícito.
- El catálogo inicial incluye las unidades de uso común. Crear una unidad personalizada requiere solo un nombre; la abreviatura se genera y la opción de cantidades fraccionarias queda avanzada.
- Cantidades vendidas deben ser enteras cuando la unidad seleccionada no permite fracciones. Si la unidad base tampoco permite fracciones, el factor y el consumo calculado deben producir cantidades base enteras; el consumo conserva precisión máxima de tres decimales.
- Los registros históricos de venta guardan la descripción, presentación, factor, cantidad vendida y cantidad base consumida en el momento de confirmar.

## Límites

No se convierte automáticamente stock histórico ni se recalculan ventas o devoluciones previas. No se introduce conversión dimensional física entre kg, litros o metros; las equivalencias son comerciales y se configuran por producto.
