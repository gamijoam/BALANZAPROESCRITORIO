# Hito 6 — Visor de producto y atajos

## Historia

Como cajero, quiero ver la foto del producto agregado en el panel izquierdo y consultar los atajos en la barra inferior, para mantener la pantalla de venta despejada y reconocer visualmente el artículo actual.

## Criterios de aceptación

- El panel izquierdo conserva cliente y operador, elimina el bloque «Lector / búsqueda» y dedica el espacio restante al visor de producto.
- Al agregar un producto, el visor muestra su nombre y, si existe, su foto.
- Al retirar del carrito el producto mostrado, el visor pasa al producto que permanece más recientemente en el carrito; si el carrito queda vacío, vuelve al estado sin producto.
- La barra inferior muestra F1 Cliente, F2 Buscar/escanear, F4 Cobrar, F8 Editar cantidad, flechas arriba/abajo para recorrer artículos, Supr para quitar la fila y F12 Caja. El aviso «Comprobante de prueba no fiscal» deja de ocupar esa barra.
- F8 lleva directamente a la cantidad del artículo seleccionado o, si no hay selección, al primero del carrito. La cantidad se edita en la tabla; Enter confirma, Escape cancela y las flechas cambian de artículo conservando el foco en cantidad.
- Solo la cantidad es editable en línea. Código, descripción, precio unitario y total son de solo lectura; la acción con mouse para quitar una fila permanece disponible.
- Editar una cantidad válida actualiza de inmediato el total y el número de artículos. Se rechazan cantidades menores o iguales a cero y más de tres decimales.
- Supr quita el artículo seleccionado cuando el foco está en el carrito y no está activa la edición de la celda. F8 nunca elimina artículos.
- Los cambios se entregan en la vista previa del proyecto, sin instalar ni aplicar migraciones en la PC.
