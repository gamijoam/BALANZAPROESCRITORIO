# Hito 14 — Almacenes y traslados

- **Estado:** 1.0.0 instalado; actualización compatible 1.1.0 preparada para incluir filtro de existencia por almacén. La 1.1.0 no agrega migraciones.
- **Aprobación:** alcance aprobado explícitamente por el usuario el 12 de octubre de 2026.
- **Paquete:** `artifacts/modules/warehouses/1.0.0.zip`.
- **Actualización:** `artifacts/modules/warehouses/1.1.0.zip`, conserva el ID `warehouses` y la migración ya aplicada `warehouses:0001`.

## Criterios de aceptación

1. La migración del paquete crea únicamente objetos dentro del esquema del módulo y es aditiva.
2. Existencias actuales se inicializan en el almacén principal. Un almacén adicional empieza sin existencias y se desactiva solo si está vacío y no tiene traslados abiertos.
3. El servidor valida que cada terminal use un almacén activo y descuenta cada venta en la ubicación asignada, en la misma transacción que reduce el total global.
4. Una devolución con reingreso recupera el almacén de la venta original.
5. El traslado usa estados Borrador/Despachado/Recibido: no descuenta en borrador, descuenta del origen al despachar y acredita destino al recibir.
6. La recepción parcial se conserva con su variación, actor y fecha, y ajusta el inventario total.
7. El servidor exige sesión y permiso por operación; el manifiesto declara cada permiso del endpoint. WPF solo usa HTTP.
8. El ZIP no contiene claves privadas, cadenas de conexión ni datos de comercios; firma y SHA-256 son validados antes de migrar.
9. Las pruebas de transferencia validan estados, cantidades, stock insuficiente y variaciones; pruebas PostgreSQL verifican operaciones concurrentes e instalación antes de autorizar uso productivo.
10. Al elegir un único ZIP, Administración lee el identificador y la versión del manifiesto, los muestra junto al nombre “Almacenes y traslados” y completa el ID sin pedir al usuario que lo adivine.
11. Si la planificación del paquete falla, la pantalla muestra el motivo devuelto por el servidor (por ejemplo, clave pública no confiable o servicio desactualizado) y conserva la posibilidad de corregirlo.
12. Al filtrar el inventario por almacén, la cantidad mostrada y los filtros de existencia usan ese almacén; “Todos los almacenes” conserva la existencia global.
13. La actualización 1.1.0 devuelve productos inactivos solo cuando la terminal lo solicita expresamente; la consulta habitual del módulo mantiene su respuesta anterior.

## Actualización 1.1.0

- El endpoint `GET warehouses/{id}/stock` acepta `includeInactive=true` para el inventario administrativo; por omisión solo devuelve productos activos.
- No se agregan ni modifican tablas, políticas de traslado, permisos o datos existentes.
- En Administración → Módulos, cargar el ZIP firmado, confirmar que el nombre mostrado sea **Almacenes y traslados** y que el ID sea `warehouses`, validar e instalar la versión 1.1.0.
- Al ser una actualización sin migraciones nuevas, no cambia el saldo de existencias ni el almacén asignado a las terminales.

## Estado de verificación

- Definición y lectura del manifiesto/migración: verificado con pruebas unitarias.
- Regla de dominio y permisos: 7 pruebas aprobadas dentro de `Pos.Domain.Tests` (suite completa: 92 aprobadas).
- Contratos, manifiestos y migraciones: `Pos.Modules.Tests`, 48 pruebas aprobadas.
- Carga del ensamblado firmado y permisos por endpoint: 4 pruebas de runtime aprobadas.
- Compilación del servidor, cliente WPF y módulo: aprobada en salidas aisladas.
- Firma e integridad de `warehouses-1.1.0.zip`: verificado con el anillo local de confianza; ID `warehouses`, versión `1.1.0`, firmante `pos-warehouses-dev-2026`, tres archivos verificados (manifiesto, ensamblado y migración existente).
- Paquete POS 1.1.0: publicación correcta; 50 entradas, incluye lanzador/actualizador, sin carpetas `bin`, configuración Development ni archivos de clave/secretos.
- Descubrimiento PostgreSQL: ejecutado con `POS_POSTGRES_BIN` inválido y detectó `C:\Program Files\PostgreSQL\18\bin` con `pg_dump`, `pg_restore` y `psql` presentes.
- Pruebas disponibles: 347 aprobadas entre Domain, Application, Modules e Integration; dos pruebas de activación PostgreSQL aislada no se ejecutaron porque faltan `POS_MODULE_VALIDATION_ADMIN_CONNECTION` y `POS_MODULE_VALIDATION_PG_BIN`.
- Activación de la actualización y recorrido visual del selector en una base de prueba, además de validación de terminales: pendientes. No usar la base real del comercio para esa aceptación.
