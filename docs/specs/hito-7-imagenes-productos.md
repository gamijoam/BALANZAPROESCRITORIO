# Hito 7 — Flujo de imágenes de productos

## Historia

Como administrador de inventario, quiero agregar, ver, reemplazar y quitar la foto de un producto, y recibir un resultado claro cuando se guarda, para poder mantener el catálogo visual actualizado.

## Criterios de aceptación

- Crear producto permite seleccionar una imagen JPG, PNG o WebP de hasta 5 MB y comunica por separado si se creó el producto y si se guardó su foto.
- Inventario indica qué productos tienen foto y muestra la imagen del producto seleccionado.
- Inventario permite agregar o reemplazar la foto y refresca la vista después de guardar.
- Inventario permite quitar la foto; la API requiere permiso de administración de catálogo, limpia la referencia y registra auditoría.
- Si el archivo no existe, no es una imagen admitida, excede el límite o el servidor rechaza la operación, la pantalla comunica el error y deja opción de reintento.
- El paquete de vista previa contiene cliente y servidor de la misma versión; no se instala ni ejecuta ninguna migración contra la base local durante esta entrega.
