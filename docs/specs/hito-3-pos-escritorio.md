# Hito 3 — POS de escritorio

## Criterios de aceptación

- La aplicación WPF autentica al operador contra el servicio y permite configurar la dirección del API en el diálogo de acceso.
- El operador puede abrir su caja con saldo inicial y moneda, y el cierre de caja se confirma explícitamente.
- La pantalla prioriza el escáner de teclado, búsqueda por SKU/nombre, carrito y total; muestra cliente/operador a la izquierda y herramientas secundarias a la derecha.
- Al escanear y enviar Enter, una coincidencia exacta de SKU se agrega. La búsqueda no bloquea la interfaz y el foco vuelve al campo de escáner.
- El carrito permite cambiar cantidades y quitar artículos. No mezcla monedas.
- Cobro admite dividir el importe entre efectivo, punto de venta, transferencia y pago móvil; la suma debe ser igual al total antes de confirmar.
- El servidor registra la venta y su clave idempotente. Si se pierde conexión, el carrito queda bloqueado y el reintento conserva clave, líneas y pagos.
- Al confirmar, se muestra un comprobante marcado de forma visible como prueba no fiscal. Imprimir es opcional y un fallo de impresión no revierte la venta.
- El desktop solo usa HTTP API; no abre una conexión PostgreSQL.

## Pruebas y estado

- `PosViewModelTests`: agregar el mismo producto consolida unidades y calcula total decimal; quitar una línea recalcula el total.
- `PosViewModelTests`: ante pérdida de conexión, la venta queda bloqueada y el reintento reutiliza la misma clave, líneas y pagos originales.
- La compilación Release de la solución debe completar sin advertencias ni errores.
- La búsqueda de catálogo y lectura de caja actual se documentan en `contrato-api-v1.md`.

## Alcance visible

En el corte original del Hito 3, los accesos a clientes, inventario, precios/tasa, historial, reportes y ajustes eran navegación preparada. El Hito 4 habilita los flujos de inventario, precios/tasa, clientes/fiado, reportes, auditoría y caja descritos en su documento. El diseño no afirma compatibilidad fiscal ni de impresoras específicas.
