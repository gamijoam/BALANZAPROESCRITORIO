# Reintento de configuración del servicio POS en Windows

## Estado confirmado

Este paquete contiene el arreglo para no fallar con `Move-Item` cuando ya existe `service-config.dpapi`: el script usa `File.Replace` con un valor nulo real para el archivo de respaldo. Las pruebas automatizadas de reemplazo y de tipos CIM pasaron (2/2), y un reemplazo de archivos temporales ordinarios pasó en Windows PowerShell 5.1.

En el equipo del usuario, al repetir la operación sobre la configuración protegida real de `C:\ProgramData\BalanzaPro\PosServer`, PowerShell devolvió `UnauthorizedAccessException` (“Acceso denegado a la ruta de acceso”) dentro de `File.Replace`. Por tanto, la actualización de una configuración DPAPI ya existente **no está confirmada como resuelta**; la prueba local anterior no reprodujo los permisos de ese archivo.

## Uso para instalación limpia

La configuración protegida anterior del servicio y sus certificados se retirarán antes de usar este paquete. Cuando no existe `service-config.dpapi`, el script toma la ruta de instalación inicial y crea el archivo nuevo. Este paquete no se ha usado todavía para confirmar el alta del servicio en esta instalación limpia.

La base PostgreSQL y los respaldos de ventas se conservan. Después de instalar, si aparece un error al crear el servicio o al volver a cambiar su configuración, guarda el mensaje completo para que el programador corrija el instalador y sus permisos.
