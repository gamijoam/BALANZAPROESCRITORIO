# Reportes avanzados 1.0.2

Módulo firmado que agrega un visor de reportes WPF al POS. Consulta los datos mediante los endpoints HTTP autorizados del servidor; el cliente de escritorio no accede directamente a PostgreSQL.

## Funciones

- Fuentes iniciales de ventas por día, ventas detalladas, ventas por producto, cajas, inventario y movimientos. Las fuentes de Almacenes aparecen si ese módulo está activo y el operador cuenta con los permisos necesarios.
- Búsqueda, fechas y moneda según la fuente, orden de filas y reordenamiento de columnas.
- Columnas que se pueden mostrar u ocultar; CSV y XLSX exportan las filas y columnas visibles. No requieren Microsoft Excel.
- Vistas personales con nombre. Guardan columnas y filtros, se asocian al operador y se sincronizan entre sus terminales.
- Al desactivar el módulo, el POS conserva disponibles sus reportes básicos.

## Requisitos

- POS anfitrión con Desktop API 1.2 o posterior, soporte de fuentes de reportes y la migración `OperatorReportViews` aplicada para guardar vistas personales.
- Permisos `ViewReports` para los reportes generales y `ViewInventory` para existencias. Los informes de Almacenes también respetan sus permisos específicos.
- Certificado público del publicador `pos-warehouses-dev-2026`, incluido como `publisher-public.pem`.

El POS anfitrión debe incluir la corrección del envío de vistas personales: el cliente debe enviar `reportId` y el objeto anidado `view` con `name`, `columns` y `filters`. Si Guardar vista falla, actualiza el POS anfitrión con el paquete de la versión correspondiente, generado mediante `scripts/Publish-LocalPos.ps1`; ese paquete es independiente del ZIP firmado del módulo.

## Instalar y actualizar

1. Actualiza el POS anfitrión si todavía no tiene la API y la migración requeridas.
2. En **Administración → Módulos**, selecciona `advanced-reports-1.0.2.zip`, valida el paquete e instálalo. Para actualizar una versión ya instalada, selecciona el paquete y sigue el flujo de actualización del POS.
3. Reinicia el servidor y vuelve a abrir el POS en las terminales para cargar el componente de escritorio.
4. Abre **Reportes**. Usa **Columnas (N)** para ajustar la tabla; escribe un nombre en **Vistas personales** y pulsa **Guardar vista** para sincronizar la configuración del operador.

## Compatibilidad y verificación

- ID: `advanced-reports`; versión: `1.0.2`.
- Firma oficial, sin migraciones propias del módulo.
- SHA-256 del ZIP: `7152CA5881201D164561B54B06B9FC8398A898A5138CDF92A1465980AF667919`.
- La impresión formal y PDF quedan para una fase posterior.

Verifica la firma desde la raíz del repositorio:

```powershell
.\scripts\Test-SignedModulePackage.ps1 `
  -PackagePath .\modulos\advanced-reports\1.0.2\advanced-reports-1.0.2.zip `
  -PublicKeyPath .\modulos\advanced-reports\1.0.2\publisher-public.pem
```

El código fuente y la guía para extender el visor están en [`src/Modules/Pos.Modules.Reporting.Desktop`](../../../src/Modules/Pos.Modules.Reporting.Desktop/README.md). La guía general de creación e integración está en [`docs/manuales/crear-modulos-pos.md`](../../../docs/manuales/crear-modulos-pos.md).
