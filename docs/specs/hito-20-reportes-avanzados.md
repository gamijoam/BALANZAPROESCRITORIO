# Hito 20 — Visor de reportes avanzados

## Criterios de aceptación

- El botón **Reportes** abre el visor avanzado si un módulo activo y autorizado registra el centro de reportes; en otro caso mantiene la pantalla básica actual.
- Los módulos pueden publicar fuentes mediante un contrato de escritorio versionado. Cada fuente declara sus columnas y permiso; no puede enviar SQL ni reemplazar consultas del servidor.
- El visor permite buscar, filtrar, actualizar, ordenar, ocultar/mostrar y reordenar columnas. La exportación incluye exactamente las filas filtradas y columnas visibles.
- Cada operador puede guardar varias vistas con nombre por reporte. Las vistas se almacenan en el servidor, se sincronizan entre terminales y solo el dueño puede consultarlas, cambiarlas o borrarlas.
- Las fuentes iniciales cubren resumen y detalle de ventas, ventas por producto, cajas, productos/inventario, movimientos de inventario y traslados/recepciones. Se respetan los permisos actuales de reportes e inventario/almacenes.
- CSV y XLSX se generan sin requerir Microsoft Excel. No se incluye impresión formal/PDF en este hito.

## Pruebas requeridas antes de implementar

- La registry acepta fuentes válidas, rechaza permisos no declarados, IDs duplicados y más de un centro de reportes.
- La pantalla anfitriona enruta al módulo autorizado y conserva el reporte básico como fallback.
- Las columnas visibles y su orden controlan tanto la tabla como los exportadores CSV/XLSX.
- Los DTOs de vistas guardadas contienen solo configuración de presentación y validan nombres, límite de tamaño y columnas permitidas.
- Las rutas de vistas exigen `ViewReports`, guardan por operador autenticado y bloquean lectura/modificación de vistas ajenas.
- Las consultas de inventario y traslados permanecen del lado servidor y responden de acuerdo con los permisos del operador.

## Restricciones

- Cliente WPF solo usa HTTP; nunca PostgreSQL.
- La API sigue siendo autoridad para permisos y datos.
- No usar valores de punto flotante para importes.
- Verificar mediante pruebas y compilación; inspección visual se reportará por separado de validación por código.

## Registro de implementación

- Las pruebas del contrato se añadieron antes de las implementaciones; inicialmente fallaron al faltar el contrato de fuentes de reportes. Después de implementar el contrato, las rutas, el visor y el exportador, la suite `AdvancedReportModuleContractTests` quedó en verde (9/9).
- La prueba de apertura WPF encontró una excepción durante `InitializeComponent`: el selector de moneda disparaba el filtro antes de crear la tabla. Se protegió el manejador durante la carga; la ventana ahora se abre y completa su ciclo de carga en STA.
- `Pos.Application.Tests`: 226/226 pruebas aprobadas (incluye la regresión de apertura de la ventana).
- `Pos.Integration.Tests` con filtro `TerminalClientVersionReportingTests`: 2/2 aprobadas. Estas pruebas inspeccionan el modelo EF y el registro de migraciones; no se conectan a PostgreSQL.
- `Pos.Modules.Tests` con filtro `ModulePackageVerifierTests`: 9/9 aprobadas.
- Compilaron sin advertencias ni errores `Pos.Server`, `Pos.Desktop`, `Pos.Modules.Reporting` y `Pos.Modules.Reporting.Desktop`.
- El paquete firmado `advanced-reports 1.0.0` se verificó con su clave pública: firma RSA-PSS, hashes y los ensamblados declarados en el manifiesto.
- El paquete de corrección `advanced-reports 1.0.1` también se verificó con su clave pública.
- No se probó la migración sobre una base PostgreSQL desechable, la sincronización entre terminales ni una revisión visual interactiva. Esas verificaciones quedan pendientes antes de considerar validado el flujo de instalación real.
