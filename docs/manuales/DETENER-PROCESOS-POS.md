# Detener procesos del POS

Ejecuta `Detener proceso por puerto.cmd`. La herramienta solicita permisos de administrador cuando hace falta y ofrece dos acciones:

1. **Liberar un puerto TCP:** muestra qué proceso y servicio lo ocupan, solicita confirmación y verifica que el puerto quede libre. Si es un servicio de Windows, detiene el servicio para evitar que el Administrador de servicios reinicie su proceso.
2. **Cerrar Pos.Desktop.exe:** cierra la aplicación de escritorio aunque no tenga una ventana visible. Esta es la opción que corresponde cuando `Actualizar POS.cmd` informa que sigue activo el escritorio.

Detener el puerto del servidor no cierra las ventanas de escritorio. Son procesos distintos. No uses la herramienta para detener PostgreSQL, ni detengas el servidor POS mientras hay ventas abiertas. El actualizador integrado hace su respaldo antes de detener el servicio.

La herramienta muestra el PID y la ruta, pide confirmación y comprueba el resultado. Si el proceso permanece, muestra su PID y ruta para identificar si faltan permisos o si otra aplicación lo vuelve a iniciar.
