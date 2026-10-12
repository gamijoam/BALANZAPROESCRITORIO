# Módulo de ejemplo ejecutable

Este módulo de muestra implementa `IPosServerModule` y publica `GET status`. La respuesta incluye el id, versión y estado del módulo. El servidor monta esa ruta como `/api/v1/modules/example-status/status` y aplica la autorización de módulos del anfitrión. La migración declarativa `example-status:0001` crea una tabla aislada del módulo.

## Preparar un paquete firmado

1. Compila el proyecto para `net10.0` en Release.
2. Copia `Pos.Modules.Example.dll` y el archivo de migración a una carpeta de preparación vacía, conservando `migrations/example-status-0001.json`.
3. Crea allí `files.json` con el arreglo `["Pos.Modules.Example.dll", "migrations/example-status-0001.json"]`.
4. Copia `module.manifest.json` a una ruta fuera de la carpeta de preparación.
5. Publica con `Pos.Modules.Publisher build`, usando ese manifiesto, `files.json`, la carpeta de preparación, un `key-id` aprobado y el certificado oficial instalado en el almacén de certificados de Windows.
6. Verifica que el servidor tenga la clave pública correspondiente en su anillo de confianza antes de instalar el ZIP.

La clave privada nunca se copia al ZIP ni al repositorio. La firma efímera usada en las pruebas automatizadas no es una clave oficial y no sirve para instalar en un POS real.

## Prueba integral con PostgreSQL

`tests/Pos.Integration.Tests/ModulePostgresActivationTests.cs` crea una base temporal de nombre aleatorio, aplica las migraciones base, ejecuta el flujo real de instalación y respaldo, inicia el servidor para comprobar la activación y elimina la base al terminar. Requiere definir `POS_MODULE_VALIDATION_ADMIN_CONNECTION` con una conexión administrativa de prueba que tenga `CREATEDB` y `POS_MODULE_VALIDATION_PG_BIN` con la carpeta que contiene `pg_dump` y `pg_restore`. El test no debe apuntar a una instancia de producción.

Este módulo solo valida carga y activación. No cambia inventario, ventas, permisos de usuarios ni datos del comercio.
