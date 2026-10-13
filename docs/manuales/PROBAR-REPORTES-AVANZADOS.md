# Instalar y probar Reportes avanzados

El ZIP de entrega incluye una actualización del POS anfitrión y el módulo firmado por separado. El anfitrión se actualiza primero porque aporta el contrato de escritorio, las rutas HTTP y la tabla donde se guardan las vistas personales.

## En la computadora servidor

1. Extrae el ZIP completo en una carpeta nueva.
2. Cierra las ventanas del POS.
3. Ejecuta `Actualizar POS.cmd`. En el servidor, este proceso crea y verifica el respaldo, aplica las migraciones del anfitrión y reinicia el servicio.
4. Ejecuta `Iniciar POS.cmd` y confirma que el servicio quede disponible.
5. Si el servidor no reconoce al firmante del módulo, ejecuta una vez `scripts\Install-WarehouseModuleTrust.ps1` con la clave pública que está junto a `modulos\advanced-reports\1.0.1.zip`:

   ```powershell
   .\scripts\Install-WarehouseModuleTrust.ps1 `
     -PublicKeyPath .\modulos\advanced-reports\1.0.1-publisher-public.pem `
     -KeyId pos-warehouses-dev-2026
   ```

6. Entra con un usuario que tenga permiso para administrar módulos. Abre **Administración → Módulos**, elige `modulos\advanced-reports\1.0.1.zip`, valida el paquete e instálalo.
7. Pulsa **Reiniciar servidor y cargar módulos** en la computadora servidor. Espera que el POS vuelva a indicar que el servicio está listo.
8. Abre **Reportes**. Si no aparece, revisa que el módulo figure activo y que el operador tenga `ViewReports`.

No vuelvas a cargar el ZIP para reactivar el módulo. Si ya estaba instalado, usa **Reactivar módulo seleccionado** y después reinicia el servidor.

## En las terminales cliente

Después del reinicio del servidor, cierra y vuelve a abrir el POS en cada terminal para descargar y verificar el ensamblado de escritorio. La terminal usa la misma IP del servidor; no necesita PostgreSQL local ni acceso directo a la base.

## Qué incluye el visor

- Ventas diarias, ventas detalladas, ventas por producto y sesiones de caja.
- Catálogo y movimientos de inventario; las fuentes de almacenes y traslados aparecen si el módulo Almacenes está activo y el operador tiene sus permisos.
- Búsqueda sobre las filas cargadas, fechas y moneda cuando corresponda, actualización, orden de tabla y visibilidad/orden de columnas.
- Vistas personales múltiples por operador, sincronizadas mediante el servidor.
- Exportación CSV y XLSX con las columnas, el orden y las filas visibles.

La impresión formal y la exportación PDF quedan fuera de esta versión.

## Verificación del paquete

El ZIP `modulos\advanced-reports\1.0.1.zip` fue firmado con el publicador interno `pos-warehouses-dev-2026` y se comprobó su firma y hashes con la clave pública incluida. La clave pública no permite firmar otros paquetes.
