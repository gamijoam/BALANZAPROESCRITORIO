# Corrección del visor Reportes avanzados 1.0.1

Esta actualización corrige el cierre al abrir **Reportes** cuando se carga el selector inicial de moneda. No cambia contratos, rutas del servidor ni el esquema de PostgreSQL; no hace falta actualizar el POS anfitrión ni repetir migraciones.

1. En la computadora servidor, abre **Administración → Módulos**.
2. Selecciona `advanced-reports-1.0.1.zip`, pulsa **Validar paquete** y luego **Instalar módulo**.
3. Pulsa **Reiniciar servidor y cargar módulos** y espera a que el POS indique que el servicio está listo.
4. Cierra y vuelve a abrir el POS en las terminales cliente. Abre **Reportes** para comprobar el visor.

Si el servidor informa que el firmante no está registrado, usa `1.0.1-publisher-public.pem` con `Install-WarehouseModuleTrust.ps1` y el ID `pos-warehouses-dev-2026`. La clave pública no es una contraseña ni permite firmar paquetes.

La versión 1.0.1 fue compilada y firmada; el ZIP y los hashes se verificaron. La prueba de regresión abre la ventana WPF y deja que termine su ciclo de carga en un hilo STA.
