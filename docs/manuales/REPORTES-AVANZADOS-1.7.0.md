# Reportes avanzados 1.7.0

Esta versión agrega dos reportes operativos:

- **Productos bajo mínimo:** muestra productos activos cuya existencia está en el mínimo configurado o por debajo. Indica cuántas unidades faltan para regresar al mínimo. El valor se ajusta desde **Inventario → Nuevo producto** o **Editar SKU/nombre**; dejarlo en 0 desactiva el aviso.
- **Antigüedad de cuentas por cobrar:** organiza la deuda en por vencer, vencida de 1 a 30, 31 a 60, 61 a 90 y más de 90 días, además de saldos sin fecha de vencimiento. Los abonos se aplican primero a los cargos más antiguos. La deuda histórica conserva su saldo y se muestra como **Sin vencimiento**, porque no tenía una fecha registrada.

Al cobrar una venta que incluya crédito, el POS propone una fecha de vencimiento a 30 días. El operador puede cambiarla antes de confirmar. El vencimiento queda guardado en el cargo y aparece también en la cuenta del cliente. Si falla la confirmación y se reintenta, se conserva la misma fecha.

## Actualización

1. Después de fusionar los cambios, abre **Actions → Empaquetar POS para Windows**, genera el paquete de `main`, descarga el artifact e instala el ZIP con **Actualizar POS**. Esta actualización agrega la migración `Hito35StockMinimumAndReceivableDueDate`; no modifica las existencias ni asigna fechas inventadas a deudas anteriores.
2. Abre **Actions → Publicar módulo firmado → Run workflow**, selecciona `main` y `advanced-reports`, descarga `signed-module-output` e instala el paquete `advanced-reports` **1.7.0** desde **Administración → Módulos**.
3. Reinicia el servidor POS cuando lo solicite el administrador de módulos.
4. En **Inventario**, configura el mínimo de cada producto al que quieras aplicar la alerta. Los productos existentes comienzan en 0 hasta que se les asigne un mínimo.

El paquete 1.7.0 requiere Desktop API 1.6. Actualiza el POS antes de instalar el módulo. El reporte requiere los permisos de inventario y reportes ya usados por el centro de reportes; no agrega permisos nuevos.
