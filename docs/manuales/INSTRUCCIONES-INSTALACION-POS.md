# Punto de venta — instalación y conexión de cajas

Esta guía cubre instalación, conexión por red local, actualización y respaldo. Extrae el ZIP completo antes de ejecutar `Iniciar POS.cmd` o `Actualizar POS.cmd`.

## Requisitos

- Windows 10/11 de 64 bits. El paquete oficial es autocontenido e incluye el runtime .NET; no hay que descargar ni instalar .NET por separado.
- PostgreSQL instalado y activo únicamente en la computadora que será servidor. PostgreSQL no viene dentro del ZIP.
- Las PCs que compartirán ventas deben estar conectadas a la misma red local.
- Usa la misma cuenta de Windows con la que configuraste el POS: Windows protege la configuración y el certificado para ese usuario.

PostgreSQL no viene dentro del paquete. Una PC configurada como **independiente** tiene su propia base; sus ventas no se comparten con otras PCs.

## Instalar la caja principal

1. En la computadora que tiene PostgreSQL, abre `Iniciar POS.cmd`.
2. Selecciona **Servidor POS + caja local**. El POS detecta la IP local, genera su certificado HTTPS y prepara el puerto 5155 para redes privadas.
3. Acepta el permiso de Windows para habilitar el POS en la red privada. No lo habilites para redes públicas.
4. Completa los datos de PostgreSQL si los solicita y configura el primer operador.
5. Cierra el POS y ejecuta `Actualizar POS.cmd` una vez para instalarlo en su carpeta permanente y crear el acceso directo. La primera instalación crea un respaldo preventivo.
6. Desde el acceso directo, abre el POS para comprobar que la base y la caja funcionan; luego ciérralo.
7. Abre `Administrar servicio POS.cmd` desde la carpeta permanente que instaló el actualizador. Elige **Instalar / actualizar configuración y arrancar** y acepta la solicitud de administrador. Esto registra el servidor con inicio automático y protección contra fallos.
8. Abre el POS desde su acceso directo. Anota la IP que muestra el asistente; la usarás en las otras cajas. Reserva esa IP en el router para que no cambie.

Si no aparece una IP, conecta primero la computadora a la red y abre nuevamente el POS. La caja servidor debe permanecer encendida, conectada a la red y con PostgreSQL activo para que las demás puedan facturar. El servicio puede seguir atendiendo cajas aunque cierres la ventana del POS.

Si el servidor detecta que la IP guardada ya no pertenece a esta computadora, actualizará automáticamente su dirección y el certificado HTTPS. Anota la dirección nueva y vuelve a configurar la confianza del servidor en cada terminal cliente.

## Conectar otra computadora

1. Extrae el mismo ZIP en la computadora cliente y abre `Iniciar POS.cmd`. No instales el servicio ni PostgreSQL en una terminal cliente.
2. Selecciona **Terminal cliente**.
3. Escribe la IP del servidor, por ejemplo `192.168.1.20`, y pulsa **Continuar**. El puerto 5155 se agrega automáticamente. También puedes escribir `192.168.1.20:5155`.
4. La primera vez, revisa la IP en el aviso de confianza y confirma si corresponde a tu caja principal. La aplicación guarda la identidad de ese servidor y la comprueba automáticamente en adelante.
5. Inicia sesión con el usuario y PIN habitual del operador.

No hace falta instalar PostgreSQL, escribir la contraseña de `postgres`, copiar archivos de certificado ni emparejar la terminal con un PIN administrativo. Si la IP no es la de tu servidor o no reconoces el aviso de confianza, cancela y confirma la dirección con quien administra la caja principal.

## Qué contraseñas se usan

- La contraseña `postgres` es administrativa y solo se solicita para configurar/reparar PostgreSQL.
- `pos_app` es normalmente el usuario que utiliza el servicio para conectarse a la base. Su contraseña no es el PIN del operador.
- Cada cajero usa su propio usuario y PIN del POS. La terminal cliente envía esos datos al servidor por HTTPS.
- La conexión PostgreSQL y el certificado privado se guardan cifrados para la cuenta de Windows del servidor; no se copian a las cajas cliente ni se incluyen en el ZIP.

## Si cambia la IP o se reinstala el servidor

- Si el router asigna otra IP, reserva una dirección fija para el servidor. Al iniciar un cliente que ya no lo encuentra, elige **No** en el aviso para escribir la IP nueva.
- Si se vuelve a crear el certificado o se reinstala el perfil del servidor, los clientes avisan que cambió la identidad. Verifica que estás en la red privada correcta y elige **No** en el aviso para reconfigurar la conexión y confirmar la nueva identidad.
- No confirmes cambios inesperados de certificado. Pregunta al encargado del servidor si el cambio fue intencional.

## Actualizar el POS

1. Extrae el ZIP nuevo en una carpeta distinta de la instalación actual.
2. Cierra todas las ventanas del POS en la PC que vas a actualizar.
3. Ejecuta `Actualizar POS.cmd` con la misma cuenta de Windows que usa esa instalación.
4. En el servidor, el actualizador verifica PostgreSQL, crea y comprueba un respaldo, detiene el servicio POS antes de reemplazar archivos, aplica las migraciones de la versión nueva, actualiza la configuración protegida y vuelve a arrancar el servicio. Comprueba la ruta y el estado del ejecutable activo para no dejar la versión vieja escuchando en el puerto. En una terminal cliente solo actualiza los archivos locales.

Si falla el respaldo o la autenticación, corrige el problema antes de continuar: el actualizador no debe detener el servidor cuando el respaldo no pudo completarse. La actualización conserva el perfil cifrado y no cambia contraseñas PostgreSQL.

El servicio de Windows `BalanzaProPosServer` inicia automáticamente al arrancar Windows. Su configuración de PostgreSQL y certificado se protege con DPAPI de máquina dentro de `ProgramData`; el archivo permite lectura solo a SYSTEM y Administradores. Los módulos siguen usando las carpetas del usuario que configuró el POS y se concede acceso a SYSTEM para que el servicio pueda cargarlos. Si cambiaste la contraseña PostgreSQL, la IP o el certificado después de instalar el servicio, usa la opción **Instalar / actualizar configuración y arrancar** para volver a guardar la configuración protegida.

Abre `Administrar servicio POS.cmd` para ver el estado, PID y ruta activa, instalar o actualizar la configuración, detener, reiniciar o quitar el servicio. Quitar el servicio no borra la base ni los datos de módulos. El acceso directo de POS comprueba e inicia el servicio instalado antes de abrir la caja.

Si aún no instalaste el servicio, al abrir `Iniciar POS.cmd` el escritorio puede iniciar un servidor local temporal. Ese servidor no arranca con Windows y se cierra al salir del POS. Instala el servicio después de configurar la base para operación automática.

Ejecuta `Detener proceso por puerto.cmd` y elige **1** para liberar un puerto TCP o **2** para cerrar `Pos.Desktop.exe`, incluso si quedó ejecutándose sin ventana visible. La herramienta muestra los PID y las rutas, solicita confirmación y comprueba que la acción se completó. Si necesita permisos elevados, Windows los solicitará. Consulta `DETENER-PROCESOS-POS.md` para más detalles.

## Módulos oficiales

- La instalación de un paquete firmado se realiza desde **Administración → Módulos**. Después de instalarlo, usa **Reiniciar servidor y cargar módulos** en la computadora servidor; espera a que la pantalla indique que el servicio está listo. Esta acción actualiza la lista de módulos de la caja anfitriona.
- En una terminal cliente el botón de reinicio queda deshabilitado: para no tumbar el servicio desde una caja remota, hay que ejecutar la acción en la computadora servidor. Al volver a iniciar sesión en las terminales, estas descargarán las pantallas activas.
- Para detener un módulo activo, selecciónalo en **Módulos registrados** y elige **Desactivar módulo seleccionado**. El servidor espera las operaciones del módulo en curso, bloquea sus rutas y registra la acción; los archivos, tablas, migraciones e información del módulo se conservan. El POS base permanece disponible.
- Para reactivar un módulo desactivado, selecciónalo en **Módulos registrados** y elige **Reactivar módulo seleccionado**. El servidor conserva el ZIP y los datos, deja el módulo pendiente de activación y requiere reiniciarse. No vuelvas a cargar el ZIP: el servidor revisará firma y compatibilidad al arrancar, sin repetir las migraciones.
- La restauración de una copia crea primero una base de datos nueva y no sustituye la conexión activa automáticamente. Comprueba la base restaurada y usa el procedimiento de activación explícita del actualizador solo cuando confirmes el destino.

## Respaldo y restauración

- Respaldo manual: ejecuta `scripts\Backup-LocalDatabase.ps1` en la PC servidor. Los archivos se guardan en `Documentos\PuntoDeVenta\Respaldos` y se verifican con SHA-256.
- Respaldos diarios: ejecuta `scripts\Register-LocalBackupTask.ps1`. La tarea se ejecuta con el usuario actual de Windows, que debe poder leer la configuración protegida y conectarse a PostgreSQL.
- Restaurar: `scripts\Restore-LocalDatabase.ps1` crea una base nueva, verifica el checksum y deja intacta la original. Activar la base recuperada requiere `-Activate`, el POS cerrado y escribir la confirmación exacta que solicita el script.
- Conserva una copia en otro dispositivo protegido. Los archivos `.backup` no se cifran por la aplicación; protégelos con BitLocker u otro almacenamiento seguro.

## Solución de problemas

### No conecta a la IP

1. Confirma que cliente y servidor estén en la misma red local.
2. Revisa la IP anotada en el servidor y que PostgreSQL esté activo.
3. En el servidor, comprueba que la red de Windows esté marcada como **Privada** y acepta el permiso del Firewall para el puerto 5155.
4. Verifica que la IP no haya cambiado en el router.

### El servidor dice que PostgreSQL no está disponible

Confirma que el servicio de PostgreSQL esté activo. En el formulario de base de datos, host local suele ser `127.0.0.1`, puerto `5432`, base `pos_dev` y usuario `pos_app`. La contraseña debe pertenecer al usuario indicado. No escribas el PIN del POS ni uses automáticamente la clave administrativa `postgres` en el campo de `pos_app`.

Si la conexión protegida quedó inválida, en la PC servidor ejecuta `scripts\Configure-LocalDatabase.ps1`. Solicita la contraseña administrativa de PostgreSQL; valida y conserva la contraseña de aplicación si todavía funciona. No ejecutes este script en las terminales cliente.

### La aplicación informa que cambió la identidad del servidor

Puede suceder si el certificado se regeneró o el servidor se reinstaló. Confirma el cambio con el encargado de la caja principal. En el aviso de conexión, selecciona **No**, escribe la IP del servidor y confirma la nueva identidad. No continúes si nadie autorizó el cambio.

### Error de inicio de sesión

Si el servicio está disponible y el POS indica que el usuario o PIN es incorrecto, revisa las credenciales del operador. Si aparece un error de conexión, revisa la IP, el Firewall, HTTPS y PostgreSQL: eso no significa necesariamente que el PIN esté mal.

## Seguridad y alcance

- Las cajas cliente se conectan al servidor únicamente mediante HTTPS en la red privada. El servicio no ofrece ventas desconectadas.
- La confirmación inicial fija la identidad del certificado presentado por esa IP en esa PC. Asegúrate de hacerla en la red correcta y cancela ante una identidad inesperada.
- La regla del Firewall limita el puerto 5155 al perfil privado de Windows. No abras el puerto hacia Internet ni habilites el POS en redes públicas.
- La conexión en una red concreta depende de la IP, el router y el Firewall de esa tienda; si no conecta, revisa esos elementos en el orden indicado.
