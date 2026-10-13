# Hito 19 — Historial de traslados y rediseño de Inventario

## Objetivo

Integrar los despachos y recepciones confirmados del módulo Almacenes al historial del producto y reorganizar las ventanas de Inventario y alta para mejorar legibilidad y espacio útil.

## Criterios de aceptación

- El historial existente de ajustes y ventas se conserva en una pestaña independiente.
- Con Almacenes activo y permiso `Warehouse.View`, aparece la pestaña Traslados; sin permiso o sin módulo, no se muestra.
- Un despacho confirmado refleja la cantidad enviada como negativa; una recepción refleja la cantidad que realmente ingresó como positiva. Los borradores y recepciones de cero unidades no figuran como movimientos.
- Cada movimiento muestra fecha, tipo, cantidad, almacén de origen y destino, referencia y operador. Se pagina mediante HTTP del módulo; WPF no accede a PostgreSQL.
- Inventario mantiene filtros y acciones, amplía la lista y habilita virtualización. El panel derecho queda dedicado al producto, mientras el ajuste se muestra en una barra horizontal a todo lo ancho, con cantidad, motivo, estado y acción.
- La creación de productos presenta SKU/nombre y precio/moneda en dos columnas y aclara que existencia inicial se expresa en unidades.
- El módulo requiere Desktop API 1.1.0; el POS anfitrión sigue cargando extensiones existentes compatibles con 1.0.0.

## Pruebas

- Los tests de contrato cubren la ruta protegida por `Warehouse.View`, el signo del despacho y la cantidad real de recepción.
- Los tests de diseño verifican ubicación del ajuste, virtualización de la tabla, formulario en columnas y etiqueta de unidades.
- La distribución requiere compilar el POS y firmar `warehouses` 1.3.3 con el certificado oficial. La revisión visual requiere abrir Inventario en Windows a 1366×768 y 125 % de escala.

## Instalación

No hay migración nueva: continúa `warehouses:0001`. Instala el ZIP firmado 1.3.3 en el servidor, reinicia el servicio y cierra/reabre las terminales con el POS actualizado a Desktop API 1.1.0.
