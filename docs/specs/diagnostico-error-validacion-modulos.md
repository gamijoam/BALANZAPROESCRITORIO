# Diagnóstico de errores al validar módulos

## Criterios de aceptación

- Si la validación falla por una regla conocida del paquete, la pantalla muestra el detalle que devuelve el servidor.
- Si el servidor falla de forma inesperada, registra la excepción con un identificador de diagnóstico y responde con ese identificador, sin exponer secretos ni detalles internos al cliente.
- La excepción se conserva en un archivo local del servidor, incluso si la salida estándar del proceso no está conectada al POS.
- La pantalla muestra el identificador en lugar del mensaje genérico y orienta al administrador a consultar el registro del servidor.
- El cliente reconoce los campos de error usados por Problem Details y por los errores JSON del POS (`error`, `code`, `title`, `detail`, `message`, `diagnosticId`).
- La prueba de regresión demuestra que un error inesperado de validación ya no se reduce a “No se pudo completar la operación”.
- El configurador local repara con la credencial administrativa de PostgreSQL el permiso `SELECT` que necesita `pos_app` en `paired_terminals`, sin cambiar ni restablecer la contraseña del rol de aplicación cuando la conexión protegida existente sigue siendo válida.
- Después de la reparación, `pos_app` puede consultar `paired_terminals` y `/api/v1/modules/plan` puede completar la evaluación de compatibilidad de terminales.

## Prueba de aceptación

Simular `POST /api/v1/modules/plan` con una respuesta HTTP 500 que contiene `code=module_plan_failed` y `diagnosticId=abc123`. El cliente debe preservar el código e identificador, y la pantalla debe mostrar un mensaje de validación con `abc123`. En el servidor, una excepción inesperada debe quedar registrada junto al mismo identificador, sin guardar una cadena de conexión o contraseña.

Para el caso real de PostgreSQL, preparar una tabla `paired_terminals` propiedad de `postgres` sin permiso `SELECT` para `pos_app`. Ejecutar la reparación del configurador y comprobar que la tabla sigue existiendo, que la contraseña protegida de `pos_app` se conserva y que el usuario puede ejecutar una consulta de lectura. Luego validar un paquete de módulo y comprobar que el plan se devuelve sin error de permisos.
