# Hito 13 — Instalación controlada de módulos oficiales

> **Evolución aprobada:** Hito 14 agrega el primer módulo comercial oficial, Almacenes. Su alcance se documenta por separado; cada módulo nuevo sigue requiriendo un hito aprobado.

## Estado

**Aprobado por el usuario el 12 de octubre de 2026.** Se autoriza desarrollar instalación de paquetes oficiales bajo los controles de respaldo, migraciones y actualización coordinada descritos abajo.

**Avance actual (12 de octubre de 2026):** el ciclo de instalación, activación, desactivación y reinicio del módulo de muestra, además de la restauración en una segunda base temporal, pasó contra PostgreSQL real. La prueba usa bases aleatorias y las elimina al terminar. No apunta a datos de comercios.

| Subfase | Estado | Evidencia y límites |
|---|---|---|
| 13A | Implementada parcialmente | Planificador y endpoints autenticados de plan/instalación/consulta. Prueba del cliente multipart y controles del coordinador; falta prueba HTTP integral de autorización. |
| 13B | Componentes implementados | 4 pruebas cubren versión, firma inválida, idempotencia y concurrencia. Guarda el ZIP; no lo extrae. |
| 13C | Implementada y validada en PostgreSQL real | La prueba aislada ejecuta respaldo verificado con herramientas PostgreSQL, almacenamiento firmado y migración transaccional. La restauración del respaldo no se probó. |
| 13D | Implementada y validada en PostgreSQL real | El servidor reiniciado lee el registro persistido, verifica la firma/compatibilidad, publica la ruta y deja el estado `Active`; solicitud sin sesión recibe 401. La compatibilidad con terminales se cubre con pruebas separadas. |
| 13E | Implementada y validada | Módulo `Pos.Modules.Example`, endpoint ejecutable y migración aditiva; la prueba PostgreSQL integral instala, activa, desactiva, reinicia y comprueba que conserva datos. |
| 13F | Implementada y validada | Desactivación inmediata con drenaje de operaciones, estado persistido, rutas bloqueadas, auditoría y datos conservados. La prueba restaura el respaldo en una base nueva y verifica que no cambia la base activa. |

La aprobación abre el trabajo de instalación controlada de módulos oficiales, pero no autoriza módulos comerciales premium ni DLL arbitrarias de terceros. El Prompt Maestro sigue rigiendo cualquier hito comercial posterior.

## Objetivo

Permitir que un administrador instale un paquete firmado de un módulo oficial desde el servidor POS; validar su compatibilidad y dependencias; respaldar la base; aplicar migraciones propias del módulo una sola vez; y activar el módulo solo cuando servidor, base de datos y terminales sean compatibles.

La primera versión solo aceptará paquetes firmados por publicadores explícitamente confiables. No aceptará DLL sin firma, claves entregadas dentro del paquete ni código que WPF ejecute localmente. El servicio del POS conserva la autoridad sobre permisos, licencias, datos y migraciones.

La versión informada por terminal facilita coordinación de despliegue y no es una atestación criptográfica: no sustituye firma de binarios ni autoriza una terminal. El servidor sigue autenticando usuario, PIN, sesión, permisos y emparejamiento como antes.

## Historia y criterios de aceptación

**Como administrador**, quiero instalar o actualizar un módulo oficial desde el servidor para habilitar una capacidad compatible sin perder ventas, inventario ni datos locales.

- Dado un paquete con firma, hashes, manifiesto, host API y dependencias válidos, cuando el administrador solicita instalación, entonces el servidor muestra el plan de cambios sin alterar archivos ni base de datos.
- Dada una firma desconocida/revocada, un hash incorrecto, una versión incompatible, un entitlement ausente, una dependencia faltante o un ciclo, cuando se valida el paquete, entonces se rechaza con diagnóstico y sin escribir archivos ni datos.
- Dado un plan válido, cuando comienza instalación, entonces el servidor adquiere un bloqueo global de instalación, impide nuevas operaciones incompatibles y espera a que terminen las operaciones activas.
- Si el respaldo verificable no se completa, la instalación se cancela antes de ejecutar migraciones.
- Las migraciones son propiedad exclusiva del módulo, usan IDs inmutables, se ejecutan solo en el servidor y se registran en una tabla de historial propia con módulo, versión, checksum, fecha y resultado.
- Una migración inicial usa el DSL JSON firmado y solo puede crear tablas/índices y agregar columnas nullable dentro del esquema del módulo. Se aplica en una transacción PostgreSQL con bloqueo de asesoría e historial de checksum. Migraciones destructivas, SQL arbitrario, `suppressTransaction` y cambios de tablas de otros módulos se rechazan en esta etapa.
- Dos instalaciones concurrentes del mismo paquete producen una sola migración. Repetir la misma instalación devuelve estado idempotente sin duplicar efectos.
- Un error de migración deja el módulo inactivo, conserva diagnóstico y archivos previos, revierte la transacción y permite restaurar el respaldo verificado a una base nueva antes de cambiar la conexión activa.
- El servidor activa el módulo solo tras migraciones correctas, verificación posterior de salud y compatibilidad de todas las terminales registradas; no hay activación parcial.
- Desactivar un módulo conserva sus datos y bloquea nuevas operaciones protegidas; nunca elimina tablas ni historial.
- Ningún comando de interfaz aparece como disponible cuando el módulo está inactivo o el operador no tiene permiso; los endpoints también lo verifican.
- El POS base conserva venta, caja e inventario cuando el módulo opcional no está instalado.

### 13F — Desactivación y recuperación verificables

**Como administrador**, quiero desactivar un módulo sin perder sus datos y comprobar que el respaldo de instalación se puede restaurar en una base separada.

- Al desactivar un módulo activo, el servidor espera que terminen las operaciones en curso, persiste el estado `Disabled` y bloquea inmediatamente sus rutas.
- El servidor reiniciado omite los módulos `Disabled`; los archivos, tablas, migraciones y datos permanecen intactos.
- Solo una sesión con `ManageModules` puede desactivar; un módulo desconocido produce 404 y otros usuarios reciben el rechazo de autorización correspondiente.
- La desactivación genera auditoría, no ejecuta migraciones destructivas y no requiere editar configuración ni reiniciar el servidor.
- La prueba restaura el respaldo verificado en una base aleatoria distinta; verifica el historial base y que el esquema del módulo aún no estaba en el respaldo previo a su instalación.
- La prueba destruye las bases temporales incluso ante fallo y no cambia la conexión activa del servicio.

## Decisiones recomendadas para aprobar

1. **Carga de código:** no cargar DLL arbitrarias en caliente. Instalar paquetes oficiales en una carpeta versionada, verificar firma y reiniciar el servicio para cargar la versión. Todas las terminales deben actualizarse a la versión compatible antes de activar.
2. **Propiedad de migración:** cada módulo posee sus tablas/esquema y su historial; la primera versión permite solo migraciones aditivas, transaccionales e idempotentes.
3. **Respaldo y recuperación:** exigir `pg_dump` exitoso, `pg_restore --list`, SHA-256 y destino fuera de la carpeta de actualización. Ante fallo, no cambiar la conexión activa; la restauración se prueba en una base nueva y solo se activa mediante operación administrativa explícita. Reutilizar los mecanismos existentes de `Backup-LocalDatabase.ps1` y `Restore-LocalDatabase.ps1` después de extraerlos a un servicio reutilizable con pruebas.
4. **Consistencia de terminales:** paquete servidor/WPF versionado en una misma publicación. No activar si hay terminales emparejadas con versión incompatible o desconectadas cuyo estado impida confirmar actualización.
5. **Confianza:** solo `Active` y `Retiring` del anillo de confianza; claves revocadas rechazan instalación. No existe modo de saltar verificación, incluso en modo administrador.

## Entregas y orden TDD

### 13A — Plan de instalación sin efectos

Endpoint autenticado de administración que acepta `multipart/form-data`, con `requestedModuleId` y de 1 a 32 campos `packages`, hasta 64 MiB en total. Verifica firma y hashes en memoria, valida compatibilidad, resuelve dependencias incluidas en el mismo lote y devuelve el orden previsto. Solo un administrador puede usarlo; las excepciones de permisos no delegan `ManageModules`.

La planificación no escribe archivos ni datos del módulo, no inicia migraciones y no abre conexiones SQL como parte del planificador. La comprobación normal de sesión y permisos del endpoint sí puede hacer consultas de solo lectura a PostgreSQL. Los módulos con entitlement se rechazan hasta que exista el servicio de licencias; no se omite esa validación.

La pantalla **Administración → Módulos** permite seleccionar el paquete y sus dependencias, validar el plan y luego solicitar la instalación. La instalación requiere `PostgreSql:BinaryDirectory` (o la variable `POS_POSTGRES_BIN`) para localizar `pg_dump` y `pg_restore`. El almacén y los respaldos usan `%LOCALAPPDATA%\POS\Modules` y `%LOCALAPPDATA%\POS\Backups` por defecto; se pueden fijar con `Modules:StoragePath` y `Modules:BackupPath`. El instalador registra `RestartPending`; no debe mostrarse el módulo como activo hasta completar la carga segura tras reinicio.

### 13B — Almacenamiento y coordinación

El primer corte guarda el ZIP firmado original, ya verificado, en una carpeta versionada con movimiento atómico; no lo extrae ni lo carga. Esto evita introducir rutas de extracción y código ejecutable antes de cerrar el diseño de migraciones y activación. El almacén rechaza puntos de reanálisis, limita tamaño, serializa instalaciones entre procesos con bloqueo global e identifica reintentos por SHA-256. Pruebas cubren paquete inválido, conflicto de versión, idempotencia y concurrencia.

### 13C — Respaldo y migraciones

Puerto de respaldo verificable y ejecutor de migraciones de módulos en el servidor. El respaldo usa `pg_dump` en formato custom, `pg_restore --list`, SHA-256 y publicación atómica; la contraseña va en el entorno del proceso y no en argumentos. La migración usa DSL JSON firmado, SQL generado, una transacción PostgreSQL para el lote completo y un historial con checksum inmutable. PostgreSQL real vía Testcontainers; respaldo/restauración probados antes de ampliar el alcance. Fallo de respaldo/migración no activa el módulo.

### 13D — Activación coordinada

Registro de estado/versiones, permisos/entitlements, diagnóstico/auditoría y compatibilidad servidor-terminal. Solo activar con salud y versiones compatibles. Exponer contrato HTTP para WPF; ninguna conexión directa a PostgreSQL desde WPF.

### 13E — Muestra oficial

Después de aprobar el MVP, crear un módulo de muestra no comercial que tenga contrato, manifiesto y una migración aditiva en base de pruebas. Demostrar instalación, reinicio, estado, desactivación y recuperación. No mover la lógica de ventas o inventario básico en esta muestra.

## Pruebas y evidencia requerida

- RED antes de cada entrega: pruebas de aplicación/arquitectura primero; PostgreSQL real con Testcontainers para transacciones, concurrencia y restauración.
- Verificar dos instalaciones simultáneas, reintentos, apagado durante extracción/migración, base no disponible, respaldo corrupto, clave revocada y terminal desactualizada.
- Correr Stryker.NET después del primer green sobre resolución, estado de instalación e idempotencia; informar alcance y mutantes reales.
- Prueba manual de instalación/recuperación en una copia de la base; no usar datos reales para la primera migración.
- No afirmar instalación funcional hasta que el ZIP publicado por la CLI cruce el endpoint, el almacén, el respaldo, la migración y la activación en un entorno de prueba reproducible.

### Criterios de aceptación de 13A

- El endpoint exige sesión válida y permiso reservado a administrador; cajeros y supervisores no pueden obtener el plan mediante excepción PIN/permisos.
- Solo acepta `requestedModuleId` y de 1 a 32 paquetes con campo `packages`, con límite total de 64 MiB.
- Firma, inventario, hash, compatibilidad, dependencias y entitlements se validan antes de producir el plan.
- Una dependencia no incluida en el lote se reporta como inválida; las dependencias se devuelven antes del módulo solicitado.
- Paquete inválido, límite excedido, dependencia faltante o licencia ausente no escriben archivos ni datos del módulo.
- No se declara la instalación completa en esta fase: el endpoint solo planifica.

## Archivos probables

- `src/Modules/Pos.Modules.Abstractions`: contrato de módulo ejecutable y estado versionado solo tras aprobarlo.
- `src/Modules/Pos.Modules.Packaging`: extracción segura/validación adicional, sin acceso a EF ni a PostgreSQL.
- `src/Pos.Application` o `src/Modules/Operations`: casos de uso de plan/instalación y puertos neutrales.
- `src/Pos.Infrastructure`: almacenamiento, coordinación y adaptadores EF/PostgreSQL.
- `src/Pos.Server`: endpoints administrativos autenticados y coordinador de instalación.
- `src/Pos.Shared.Contracts` y `src/Pos.Desktop`: contratos y pantalla administrativa después de asegurar el flujo del servidor.
- `tests/Pos.Modules.Tests`, `tests/Pos.Integration.Tests` y pruebas de instalación/recuperación dedicadas.

## Fuera de alcance

Construir el módulo comercial de almacenes, transferencias u otras funciones premium; cargar código de terceros; instalar desde WPF/terminal cliente; migraciones destructivas; rollback automático de datos; sincronización en la nube; y permitir que un módulo acceda directamente a las tablas privadas de otro módulo.
