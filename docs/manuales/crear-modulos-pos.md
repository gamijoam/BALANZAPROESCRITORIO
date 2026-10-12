# Guía para crear e integrar módulos del POS

Esta guía es el proceso repetible para que un programador agregue una capacidad opcional, genere un ZIP firmado y la integre con el anfitrión POS. El ejemplo de referencia es `src/Modules/Pos.Modules.Warehouses`.

## 1. Decidir si algo debe ser módulo

Un módulo agrega una capacidad opcional a una instalación POS. La venta diaria, caja, catálogo e inventario básico pertenecen al anfitrión. Antes de empezar, registrar un hito con alcance, entidades propietarias, permisos, migraciones, compatibilidad, recuperación y criterios de aceptación. No mover código base al módulo como parte de una tarea funcional sin pruebas y decisión arquitectónica.

## 2. Estructura recomendada

```text
src/Modules/<Pos.Modules.Nombre>
├── Domain/                 # reglas puras; dinero decimal, unidades decimal
├── Application/            # casos de uso y contratos
├── Infrastructure/         # persistencia del esquema propio del módulo
├── Endpoints/              # rutas HTTP del servidor
    ├── Pos.Modules.Nombre.Desktop/ # pantalla WPF opcional; consume solo HTTP
├── migrations/             # migraciones JSON declarativas
├── Pos.Modules.Nombre.csproj
├── NombreModule.cs         # IPosServerModule
├── module.manifest.json
└── README.md
```

El paquete puede incluir dos puntos de entrada independientes: `entryAssembly` para el servicio y `desktopEntryAssembly` para WPF. El servicio instala el mismo ZIP firmado y lo distribuye a terminales autenticadas. Cada terminal vuelve a verificar firma, hashes, identidad, versión y `desktopApi`, y carga la pantalla desde memoria. La API de escritorio (`IPosDesktopModule`) registra comandos y extensiones que el anfitrión publicó. Venta y Cobro no admiten reemplazo arbitrario. Si una idea requiere un punto de extensión nuevo, se incorpora una vez al anfitrión y las siguientes versiones del módulo lo reutilizan.

## 3. Contratos y límites

- Implementa `IPosServerModule` y declara rutas relativas, métodos y permiso requerido por endpoint.
- Cada permiso del endpoint también aparece en `module.manifest.json`; el host rechaza discrepancias.
- Mantén la autorización real en servidor. Las rutas requieren sesión más `Warehouse.View`, `Warehouse.Manage` o `Warehouse.Transfer`.
- Agrega una llamada tipada a `IPosApiClient` / `PosApiClient` y una pantalla WPF. No uses SQL ni una conexión PostgreSQL desde escritorio.
- El esquema generado por `ModuleMigrationSqlGenerator.SchemaForModule(id)` pertenece al módulo. No leas o escribas tablas privadas de otro módulo desde el paquete.
- Si una operación de POS debe cambiar una ubicación en la misma transacción de una venta/devolución, implementa un puente explícito en `Pos.Infrastructure` como `WarehouseStockBridge`; no hagas una segunda llamada HTTP después de confirmar la venta.

## 4. Migraciones seguras

Usa `migrations/<id>-0001.json` y su referencia `id:0001` en el manifiesto. La DSL acepta `createTable`, `addNullableColumn` y `createIndex`; tipos permitidos: `uuid`, `text`, `integer`, `bigint`, `boolean`, `numeric(18,4)`, `timestamptz` y `date`. No agregues SQL libre, columnas destructivas ni migraciones que dependan de datos locales sin definir respaldo y recuperación.

La migración de almacenes crea tablas en el esquema aislado y deja la carga inicial de cantidades para el primer uso del servicio, donde se puede leer el inventario existente con la transacción activa. Todas las nuevas tablas llevan sus índices únicos declarados en el mismo archivo JSON.

## 5. Pruebas TDD obligatorias

1. Escribe criterios de aceptación antes de cambiar código.
2. Agrega pruebas que fallen para la regla de dominio, permisos, manifiesto, migración, idempotencia, operaciones simultáneas y efectos sobre el inventario global.
3. Ejecuta las pruebas rojas y conserva el diagnóstico; implementa después.
4. Ejecuta el proyecto de pruebas enfocado y las pruebas completas afectadas.
5. Para cambios PostgreSQL, ejecuta pruebas de integración con una base temporal; nunca uses los datos reales del comercio para validar una migración nueva.
6. No declares el módulo listo para producción solo porque compila o se firmó.

Comandos desde la raíz:

```powershell
dotnet test .\tests\Pos.Domain.Tests\Pos.Domain.Tests.csproj --filter FullyQualifiedName~WarehouseTransferPolicyTests
dotnet test .\tests\Pos.Modules.Tests\Pos.Modules.Tests.csproj --filter FullyQualifiedName~WarehouseModuleContractTests
dotnet test .\tests\Pos.Integration.Tests\Pos.Integration.Tests.csproj --filter FullyQualifiedName~ModulePostgresActivationTests
dotnet build .\src\Pos.Server\Pos.Server.csproj
dotnet build .\src\Pos.Desktop\Pos.Desktop.csproj
dotnet build .\src\Modules\Pos.Modules.Warehouses\Pos.Modules.Warehouses.csproj
```

## 6. Construir el staging y ZIP

La CLI es `src/Modules/Pos.Modules.Publisher`. Publica únicamente los archivos revisados; no zipees la carpeta `bin` completa. Para el módulo de almacenes el staging contiene:

```text
Pos.Modules.Warehouses.dll
Pos.Modules.Warehouses.Desktop.dll
migrations/warehouses-0001.json
```

`package-files.json` es una lista explícita de rutas relativas, y `module.manifest.json` se suministra aparte a la CLI. El manifiesto de escritorio declara `desktopEntryAssembly`, `desktopEntryType` y `desktopApi`. La clave privada debe existir en el almacén `CurrentUser\My` del publicador, con RSA vigente de al menos 2048 bits y no exportable. La clave pública se instala por canal confiable en el anillo del servidor. No guardes `.pfx`, `.p12`, PEM privado, credenciales, `secrets.json` ni datos de clientes en Git o en el ZIP.

Ejemplo reproducible con PowerShell (usar una huella de certificado vigente controlada por el publicador):

```powershell
$root = (Get-Location).Path
$stage = Join-Path $root 'artifacts\module-staging\warehouses-1.1.0'
$out = Join-Path $root 'artifacts\modules\warehouses\1.1.0.zip'
New-Item -ItemType Directory -Force -Path $stage, (Split-Path $out) | Out-Null
$migrationStage = Join-Path $stage 'migrations'
New-Item -ItemType Directory -Force -Path $migrationStage | Out-Null
Copy-Item '.\src\Modules\Pos.Modules.Warehouses\bin\Release\net10.0\Pos.Modules.Warehouses.dll' $stage
Copy-Item '.\src\Modules\Pos.Modules.Warehouses.Desktop\bin\Release\net10.0-windows\Pos.Modules.Warehouses.Desktop.dll' $stage
Copy-Item '.\src\Modules\Pos.Modules.Warehouses\migrations\warehouses-0001.json' $migrationStage
@'
{
  "files": ["Pos.Modules.Warehouses.dll", "Pos.Modules.Warehouses.Desktop.dll", "migrations/warehouses-0001.json"]
}'@ | Set-Content -Encoding utf8 (Join-Path $stage 'package-files.json')
dotnet run --project .\src\Modules\Pos.Modules.Publisher -- build `
  --manifest '.\src\Modules\Pos.Modules.Warehouses\module.manifest.json' `
  --stage $stage --files (Join-Path $stage 'package-files.json') --output $out `
  --key-id 'pos-publisher-<id-aprobado>' --thumbprint '<huella>' --store CurrentUser
```

La CLI firma el índice y genera los hashes; no incluye la clave privada. Cambia la versión del manifiesto para cada publicación y agrega una migración nueva sin editar el identificador/hash de una migración que ya se haya publicado.

## 7. Instalar, reiniciar y probar

1. Instala la clave pública del publicador en el anillo externo `%ProgramData%\Pos\Security\TrustedModuleKeys.json` y configura `ModuleTrust:KeyRingPath` en el servidor.
2. Inicia sesión en la terminal que administra el servidor, abre **Administración → Módulos**, selecciona `warehouses`, el ZIP, y ejecuta **Validar**. Comprueba compatibilidad, permisos y migraciones.
3. Si el plan es correcto, ejecuta **Instalar**. El servidor hace respaldo verificable y aplica el DSL; el registro queda `RestartPending`.
4. Reinicia el servidor POS para verificar y cargar el ensamblado firmado. Comprueba `/health/ready`, el estado `Active` y las funciones de Almacenes.
5. Aceptación: confirmar saldos migrados al principal; crear segundo almacén; asignar terminal de prueba; registrar venta y revisar que descuente solo el saldo asignado; transferir, despachar y recibir; registrar variación; probar devolución; revisar historial/auditoría; desactivar/reactivar y confirmar consistencia.
6. Ejecuta estos pasos en una base restaurada y datos ficticios antes de probar en la base activa. El respaldo generado debe conservarse fuera de la carpeta de actualización.

La instalación no se considera completada hasta que la firma, el respaldo, la migración, activación tras reinicio, terminal compatible y pruebas de flujos operativos tengan evidencia. Para una instalación local de desarrollo, la clave pública también puede guardarse en `%LOCALAPPDATA%\PuntoDeVenta\Security\TrustedModuleKeys.json`; el anfitrión local la carga al iniciar el servidor. El anillo permanece fuera del ZIP y de la carpeta reemplazable de actualización.

## 8. Actualización y propiedad

- No sustituyas silenciosamente migraciones publicadas. Agrega `0002`, `0003`, etc., registra cada checksum y define compatibilidad.
- Mantén dependencias explícitas y acíclicas; solo declara paquetes presentes en el catálogo instalable. Los componentes base del host se documentan como contratos, no como dependencias falsas.
- Preserva idempotencia del cliente. Registra actor, terminal, hora y causa en las mutaciones operativas.
- Una desactivación deja datos intactos; no borra datos ni intenta rollback automático.
- Incluye README de usuario, notas de migración y documentación de recuperación con cada ZIP.

## Archivos de referencia

- Manifiesto y migración: `src/Modules/Pos.Modules.Warehouses/`.
- Esquema general de paquetes: `docs/specs/paquete-modulo-v1.md`.
- Firma y confianza: `docs/security/claves-firmantes-de-modulos.md` y `docs/security/publicar-modulos.md`.
- Instalación: `docs/specs/hito-13-instalacion-de-modulos.md`.
- Criterios funcionales: `docs/specs/hito-14-modulo-almacenes.md`.
