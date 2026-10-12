# Ajustes de usabilidad: productos, clientes y devoluciones

## Criterios de aceptación

- El campo Precio del formulario Nuevo producto comienza vacío, listo para escribir; el usuario no debe borrar un cero prefijado.
- Después de crear un cliente, la operación se completa sin mostrar una leyenda de éxito en la barra inferior. Los errores de validación y de servicio siguen visibles.
- Después de retirar un cliente de la lista activa, no queda una leyenda de éxito en la barra inferior. Los errores de validación y de servicio siguen visibles.
- El límite de crédito del formulario Nuevo cliente comienza vacío, listo para escribir.
- El reporte muestra en español los estados de venta y señala en el detalle de cada artículo la cantidad devuelta y si no tuvo devolución, fue parcial o completa.
- Las filas de artículos con devolución se distinguen visualmente de las demás.
- Al abrir caja, la moneda principal del turno es siempre VES, aplicada por la interfaz y el servicio; no se pide elegirla. El saldo inicial puede registrarse por separado en VES y USD, y los cobros en USD se siguen reflejando y conciliando en USD.

## Pruebas

- Pruebas de presentación para el estado inicial del campo, el mensaje de creación, las etiquetas en español y las marcas por línea.
- Compilación y pruebas de la solución.
