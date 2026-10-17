# Actualizar POS y Almacenes a 1.1.0

La entrega trae dos paquetes: una actualización del POS de escritorio y el módulo firmado `warehouses` versión `1.1.0`. El ID del módulo no cambia. La actualización no modifica las tablas ni los saldos existentes.

## Orden recomendado

1. Hazlo cuando no haya ventas en curso. Extrae `pos-warehouses-1.1.0.zip` en una carpeta temporal y ejecuta `Actualizar POS.cmd`. El actualizador verifica la base, guarda un respaldo, detiene el servidor POS identificado, aplica las migraciones pendientes antes de reemplazar los archivos y abre la nueva versión. En una terminal cliente actualiza también el POS desde el mismo paquete; el actualizador detecta el perfil y no busca una base local.
2. Entra como administrador, abre **Administración → Módulos** y elige `warehouses-1.1.0.zip`.
3. Comprueba que se muestre **Almacenes y traslados**, ID `warehouses`, versión `1.1.0`. Pulsa **Validar** y luego **Instalar**. El respaldo preventivo se ejecuta antes de instalar.
4. Cuando el módulo solicite reinicio, cierra el POS y vuelve a abrirlo con `Iniciar POS.vbs`. El servicio carga el ensamblado actualizado.
5. Abre **Inventario**. El selector **Todos los almacenes** conserva la existencia global; elige un almacén para ver sus cantidades y filtrar **Con existencia** o **Agotados** usando ese almacén.

## Qué actualiza cada paquete

- El POS agrega el selector de almacén al inventario y automatiza la detección local de `pg_dump`, `pg_restore` y `psql` en el arranque y al actualizar.
- `warehouses-1.1.0.zip` amplía la consulta de existencias para permitir que el inventario administrativo consulte productos inactivos. Sin ese parámetro, el endpoint sigue devolviendo solo productos activos.
- La versión 1.1.0 conserva la migración `warehouses:0001`; si el módulo ya estaba instalado en 1.0.0, no se repite. Si es una instalación nueva, se ejecuta como primera instalación.
- Actualiza todas las terminales en las que se usará el filtro de almacén. Los perfiles de terminal cliente no guardan ni necesitan credenciales PostgreSQL.

## Si algo falla

No borres el respaldo. Anota el mensaje mostrado y revisa `%LOCALAPPDATA%\PuntoDeVenta\Logs\servidor-local.log` o el mensaje del actualizador. Si falla el respaldo, el instalador conserva el servidor y los archivos actuales. Si falla una migración, no reemplaza la instalación actual.
