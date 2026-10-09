# Hito 2 — Persistencia PostgreSQL y servicio

## Estado

Hito 2 completado para el alcance aprobado: persistencia PostgreSQL, migraciones, servicio con endpoints versionados, autenticación, idempotencia y concurrencia sobre PostgreSQL real. Las funciones añadidas posteriormente están descritas en el documento del Hito 4.

## Entrega de este corte

- `Pos.Infrastructure` contiene el `PosDbContext` y el modelo inicial de productos, existencias, ventas, líneas, pagos, idempotencia y auditoría.
- Las migraciones crean el esquema inicial, operadores/sesiones, turnos de caja y las extensiones operativas de los hitos posteriores. El índice parcial impide más de una caja abierta por terminal.
- El servidor expone `/health/live` y `/health/ready`; el segundo comprueba conexión con PostgreSQL.
- Login con PIN almacenado con PBKDF2-HMAC-SHA256 (600.000 iteraciones y salt aleatorio), bloqueo temporal tras cinco fallos y token opaco de 256 bits. PostgreSQL almacena solo el hash del token; la sesión vence en 12 horas y puede revocarse.
- El alta del primer operador solo está habilitada en `Development` desde loopback y solo mientras no existan operadores.
- `scripts/Configure-InitialOperator.ps1` pide el usuario y PIN en forma interactiva y manda el alta solo a una URL loopback.
- `POST /api/v1/sales` requiere bearer token, caja abierta y `Idempotency-Key`. La venta, líneas, pagos, idempotencia, auditoría y descuentos de stock ocurren en una transacción. Una clave repetida devuelve la venta original; la misma clave con otro contenido produce conflicto.
- El stock se descuenta con una actualización condicional en PostgreSQL; no se confirma una venta que deje existencias negativas.
- El script `scripts/Configure-LocalDatabase.ps1` crea la base `pos_dev`, el rol de aplicación y guarda la cadena de conexión en .NET User Secrets fuera del repositorio.
- `scripts/Test-LocalCheckout.ps1` realiza una comprobación HTTP local completa con datos temporales y los elimina al finalizar.
- El cambio local de esquema se aplica con `dotnet-ef database update`. El servidor no ejecuta migraciones automáticamente al iniciar.
- En desarrollo local, el rol de aplicación también es dueño de `pos_dev` para poder aplicar migraciones. Producción debe separar la identidad que despliega migraciones de la identidad de runtime.

## Verificación realizada

- Conexión al PostgreSQL local y aplicación de las migraciones completadas.
- `PostgresSchemaTests` comprueba esquema, autenticación, hash/revocación del token, idempotencia, atomicidad y concurrencia usando PostgreSQL real.
- Prueba manual del host en `Development`: `/health/live` respondió `ok` y `/health/ready` confirmó `ready` contra PostgreSQL.
- La comprobación HTTP local validó alta inicial, login, apertura de caja, venta, reintento idempotente y conflicto ante payload cambiado.
- En la prueba concurrente, dos ventas disputaron la última unidad: solo una fue confirmada.
- `dotnet test Pos.sln --configuration Release`: 55 pruebas aprobadas (50 de dominio y 5 de integración). Application, Architecture y EndToEnd todavía no tienen casos.

## Límites pendientes

El alcance original del Hito 2 no incluía devoluciones ni cálculos fiscales. Las funciones operativas incorporadas después no equivalen a validación fiscal; el comprobante continúa siendo de prueba.
