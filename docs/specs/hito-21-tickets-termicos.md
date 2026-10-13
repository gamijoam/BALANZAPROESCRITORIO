# Hito 21: tickets térmicos

## Objetivo

Agregar el módulo opcional firmado `thermal-receipts`. La venta se confirma en el servidor antes de intentar imprimir. El módulo usa un diseño comercial compartido por el POS y una impresora instalada localmente en cada terminal. No emite documentos fiscales.

## Criterios de aceptación

- **H21-01:** Sin el módulo activo, cobrar y consultar el historial conserva la ventana de comprobante actual.
- **H21-02:** Con el módulo activo y un cajero con `ThermalReceipts.Print`, cada respuesta exitosa de confirmación imprime una sola vez sin abrir un diálogo de selección.
- **H21-03:** Un fallo de impresión no altera ni repite una venta confirmada; el cajero recibe un diagnóstico y puede abrir el comprobante desde el historial para reintentar.
- **H21-04:** Un administrador puede editar y guardar el diseño común; otra terminal ve la última versión al cargarlo. El servidor rechaza plantillas inválidas y cambios sin permiso.
- **H21-05:** Cada terminal puede seleccionar una cola de Windows, configurar copias y emitir una prueba; esta configuración no se sincroniza ni contiene secretos.
- **H21-06:** Vista previa, impresión de prueba, venta y reimpresión usan el mismo constructor de documento y los mismos datos.
- **H21-07:** El editor restringe ancho a 58/80 mm, etiquetas y campos a listas permitidas y longitud de textos; la personalización no altera cálculos ni datos de la venta.
- **H21-08:** Los paquetes inválidos o incompatibles no reemplazan el comportamiento base; la firma y el manifiesto del paquete se verifican antes de distribuirlo.

## Decisiones y límites

- Diseño compartido en el servidor; cola de impresión y copias locales por terminal.
- Cola de impresión administrada por Windows; no se promete compatibilidad con un modelo sin probar su controlador físico.
- Ticket comercial no fiscal en esta entrega. La integración fiscal requiere un hito aparte y equipo/requisitos definidos.
- Al no existir hoy un punto de extensión de comprobantes, el POS anfitrión sube una vez su Desktop API versionada. El módulo no reemplaza arbitrariamente el flujo de venta.
- Datos persistidos por migración aislada `thermal-receipts:0001`; al desactivar se conservan plantilla y tablas.

## Verificación

- Pasaron las pruebas de plantilla/manifiesto/migración (8), flujo del anfitrión y registro de permisos (6), compilación del servidor y escritorio, y verificación criptográfica del paquete firmado.
- También pasó la prueba aislada que genera el SQL aditivo de la migración (1).
- La suite completa de integración requiere una base PostgreSQL desechable. En este equipo no estaban configuradas las variables de validación y la conexión local de pruebas rechazó la contraseña del usuario pos_app; por ello no se considera validada la activación real contra PostgreSQL.
- Quedan pendientes la inspección visual de la pantalla en el POS en ejecución y las pruebas con impresoras físicas de 58 y 80 mm. No declarar compatibilidad física ni fiscal sin esa evidencia.
