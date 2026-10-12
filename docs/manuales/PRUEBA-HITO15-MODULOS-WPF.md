# Prueba del anfitrión modular y Almacenes

## Paquetes generados

- `artifacts/pos-modules-core-clean.zip`: POS base con el cargador de módulos y el contrato de extensiones. No incluye la pantalla ni la DLL de Almacenes.
- `artifacts/modules/warehouses/1.2.0.zip`: módulo firmado con el servicio, la pantalla WPF, el filtro de inventario y la migración declarada.
- `artifacts/modules/warehouses/1.2.0-publisher-public.pem`: clave pública para instalar en el anillo de confianza del servidor. No es una clave privada.

El usuario de PostgreSQL y los datos del comercio no van dentro de estos paquetes. El archivo base incluye el lanzador que usa el perfil local ya configurado. El módulo 1.2.0 conserva `warehouses:0001`; al actualizar desde 1.1.0 no vuelve a aplicar esa migración.

## Instalación para validar

1. Conserva una copia de seguridad externa de la base antes de probar una instalación/actualización de módulo.
2. En la computadora que ejecuta el servicio, actualiza el anfitrión con el paquete `pos-modules-core-clean.zip` usando `Actualizar POS.cmd`. Esto instala el cargador nuevo sin incluir Almacenes en el POS base. El actualizador coordina respaldo, servicio y migraciones base.
3. Inicia el POS y entra como administrador. En **Administración → Módulos**, selecciona `1.2.0.zip`. El ID se obtiene del manifiesto: `warehouses`; no hay que escribirlo a mano.
4. Pulsa **Validar paquete** y revisa el nombre **Almacenes y traslados**, el ID interno `warehouses`, versión `1.2.0`, componentes y permisos. Instala y reinicia el servicio cuando termine el respaldo.
5. Reinicia también las terminales POS. Tras iniciar sesión con un usuario que tenga `Warehouse.View`, debe aparecer **Almacenes**. `Warehouse.Manage` controla creación, desactivación y asignación de terminal; `Warehouse.Transfer` controla traslados.
6. Abre **Inventario**. El filtro **Almacén: todos** debe aparecer junto con los filtros del anfitrión. Elegir un almacén consulta existencias por el servicio; el POS de escritorio no usa PostgreSQL.

## Comprobación esperada

- Sin el módulo activo, no aparece el botón **Almacenes** ni el filtro.
- Con el módulo activo, aparecen desde el ZIP sin recompilar ni reemplazar la DLL del POS escritorio.
- El menú y las acciones se muestran según permisos, y cada llamada continúa protegida en el servidor.
- Si la firma o `desktopApi` no coincide, ese módulo no se carga; el POS base conserva sus funciones.

## Generar otra versión

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-WarehouseModulePackage.ps1 `
  -CertificateThumbprint '<huella del certificado de publicación>'
```

Verifica la firma y los hashes con `scripts/Test-SignedModulePackage.ps1`, usando el ZIP y la clave pública correspondiente. Para la activación integral de migraciones, ventas y traslados, usa una base PostgreSQL de pruebas restaurada; no la base activa del comercio.
