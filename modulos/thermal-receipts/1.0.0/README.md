# Tickets térmicos 1.0.0

Módulo firmado para configurar, previsualizar y enviar comprobantes comerciales a impresoras instaladas en Windows. Esta versión no emite documentos fiscales.

## Funciones

- Editor guiado para ancho de 58 u 80 mm, encabezado, pie, etiquetas, alineación, tamaño de letra, secciones y orden/visibilidad de campos de artículos.
- Vista previa y prueba de impresión en Administración → Módulos → Ticket térmico.
- Después de confirmar una venta, envía el comprobante a la cola de Windows sin abrir una ventana que detenga al cajero.
- Si falla la impresora, la venta queda confirmada y se puede reintentar desde Historial de ventas.
- El diseño se guarda en el servidor y se comparte entre terminales. La impresora y copias (1 por defecto; máximo 3) son configuración local de cada terminal.

## Requisitos e instalación

- POS anfitrión actualizado con Desktop API 1.3.0 o posterior y soporte de extensiones de comprobante.
- En cada terminal, una impresora y su controlador instalados en Windows. Configura la impresora desde Ticket térmico y ejecuta primero Imprimir prueba.
- En Administración → Módulos, selecciona thermal-receipts-1.0.0.zip, valida e instala el módulo. El servidor hará respaldo y aplicará su migración. Reinicia el servicio y vuelve a abrir el POS para cargar la extensión.
- Un usuario con permiso de administrar módulos configura el diseño compartido. Cada terminal selecciona su propia impresora.

Los tickets son comerciales y no fiscales. La cola de Windows confirma el envío del trabajo, no que el papel haya salido físicamente. La compatibilidad física debe comprobarse con cada modelo de impresora.

## Verificación

- ID: thermal-receipts; versión: 1.0.0.
- Desktop API mínima: 1.3.0.
- Migración: thermal-receipts:0001.
- Firma: pos-warehouses-dev-2026, con clave pública publisher-public.pem.
- SHA-256: 6C0DF00FA6616CFF2CDC43C20921D5B9B53F39569DB1972D28073913AB3899DD.

Verifica la firma desde la raíz del repositorio:

    .\scripts\Test-SignedModulePackage.ps1 -PackagePath .\modulos\thermal-receipts\1.0.0\thermal-receipts-1.0.0.zip -PublicKeyPath .\modulos\thermal-receipts\1.0.0\publisher-public.pem

El script que compila y firma el paquete está en scripts/Build-ThermalReceiptModulePackage.ps1.

## Documentación para desarrolladores

La arquitectura, los contratos, permisos, plantilla, migración y pasos de compilación/publicación están documentados en [la guía de desarrollo del módulo](../../../src/Modules/Pos.Modules.ThermalReceipts/README.md).
