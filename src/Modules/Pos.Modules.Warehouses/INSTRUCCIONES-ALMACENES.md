# Probar Almacenes en el POS

## Requisito

Usa la compilación POS que acompaña esta entrega: el módulo requiere el host actualizado con la entrada **Almacenes**, el contrato HTTP de almacenes y los permisos granulares. El ZIP del módulo solo agrega la capacidad de servidor y su migración; no actualiza el programa base por sí solo.

## Instalar

1. Instala/actualiza el POS base con `pos-warehouses-preview.zip` y conserva la configuración PostgreSQL existente.
2. Agrega la clave pública del publicador del módulo al archivo de confianza indicado por el manual de entrega. La clave privada nunca se copia a la computadora del servidor.
3. Inicia el POS actualizado e inicia sesión como administrador.
4. Abre **Administración → Módulos** y elige `warehouses-1.0.0.zip`. El sistema lee el manifiesto y completa el campo automáticamente. El nombre visible es **Almacenes y traslados**; su ID interno es `warehouses`. `1.0.0.zip` es el nombre del archivo, no el ID. Si la pantalla no completa el ID al elegir un solo ZIP, estás ejecutando una versión anterior del POS.
5. Pulsa **Validar**. Revisa firma, compatibilidad, permisos, migraciones y versiones de terminal antes de instalar.
6. Pulsa **Instalar** y confirma que el respaldo termine correctamente. Cierra y vuelve a abrir el POS para reiniciar el servicio.
7. En **Administración → Módulos**, comprueba que el estado diga **Activo**.

## Flujo de prueba

1. Abre **Almacenes**. El almacén principal debe mostrar las cantidades que ya existían.
2. Crea un almacén con código `SUC-01` y nombre `Depósito de prueba`.
3. En **Traslados**, selecciona el principal como origen y el nuevo depósito como destino. Agrega un producto con existencia disponible y una cantidad pequeña.
4. Crea el traslado; debe quedar en borrador. Despáchalo y comprueba que las unidades salen del origen. Luego recíbelo en destino.
5. Selecciona el depósito y pulsa **Asignar esta terminal**. Una venta de ese producto debe rebajar la existencia del depósito; el total global del POS cambia en la misma transacción.
6. Haz una devolución con reingreso y comprueba que las unidades vuelvan a la ubicación de esa venta.

Haz la primera verificación con datos de prueba y una base recuperable. El botón **Recibir completo** confirma la cantidad enviada; el servicio también guarda las diferencias cuando se reporta una cantidad menor mediante su API.
