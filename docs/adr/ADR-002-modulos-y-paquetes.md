# ADR-002 Organización de módulos y paquetes instalables

- **Estado:** aceptado; instalación, migraciones declarativas, carga en memoria, permisos por endpoint y módulo de almacenes se implementaron. Hito 15 agrega extensiones WPF firmadas distribuibles desde el servicio; la aceptación operativa en copia PostgreSQL y el flujo de venta/traslado/devolución siguen pendientes.
- **Fecha:** 11 de octubre de 2026.
- **Decisión relacionada:** ADR-001 define el monolito modular y evita cargar DLL arbitrarias en la v1. Esta propuesta conserva esa base y define una evolución segura a paquetes oficiales instalables.

## Contexto observado

La solución actual separa anfitriones y capas (`Pos.Desktop`, `Pos.Server`, `Pos.Domain`, `Pos.Application`, `Pos.Infrastructure`, contratos y BuildingBlocks). Los casos de negocio todavía están agrupados principalmente por capa; no existe un registro de módulos, manifiesto de paquete ni instalador de funciones.

El servidor registra migraciones de EF Core al iniciar. La aplicación WPF consume el servicio por HTTP y el servidor es responsable de permisos y acceso a PostgreSQL. Hay proyectos de pruebas de arquitectura disponibles.

## Decisión

Organizar el código nuevo por capacidad comercial, empaquetar cada capacidad como módulo con identidad y versión, y mantener un solo servicio POS y una sola base PostgreSQL por instalación. Los paquetes serán oficiales, firmados y compatibles con una versión del anfitrión. El servidor validará e instalará los módulos; WPF nunca ejecutará migraciones ni tendrá credenciales PostgreSQL.

La organización de módulos será explícita. El nombre interno será `Module`; “plugin” puede conservarse como etiqueta visible para el usuario. No se resolverán dependencias mediante referencias a la infraestructura de otro módulo. La interacción entre módulos ocurrirá por contratos públicos versionados y eventos internos documentados.

## Organización destino

Los proyectos anfitriones y compartidos existentes conservan sus rutas durante la transición. Los módulos nuevos empiezan en `src/Modules`:

```text
src/
├── Pos.Desktop/                         # Shell WPF, inicio y composición visual
├── Pos.Desktop.Presentation/            # Cliente HTTP y modelos compartidos de presentación
├── Pos.Server/                          # Anfitrión HTTP, autorización y coordinador de módulos
├── Pos.Shared.Contracts/                # Contratos HTTP versionados
├── Pos.BuildingBlocks/                  # Abstracciones pequeñas y neutrales
├── Pos.Domain/                          # Código existente; se extrae gradualmente por módulo
├── Pos.Application/                     # Código existente; se extrae gradualmente por módulo
├── Pos.Infrastructure/                  # Persistencia compartida y código existente
└── Modules/
    ├── Catalog/
    │   ├── Pos.Modules.Catalog.Contracts/
    │   └── Pos.Modules.Catalog/
    │       ├── Domain/
    │       ├── Application/
    │       ├── Infrastructure/
    │       ├── Endpoints/
    │       ├── Desktop/
    │       ├── Migrations/
    │       ├── CatalogModule.cs
    │       └── module.manifest.json
    ├── InventoryBasic/                  # Parte del producto base
    ├── Customers/
    ├── Sales/
    ├── Pos.Modules.Warehouses/          # Servicio, reglas, esquema y migraciones
    └── Pos.Modules.Warehouses.Desktop/  # Pantalla y extensiones WPF opcionales
```

Las carpetas `Domain`, `Application`, `Infrastructure`, `Endpoints` y `Desktop` pertenecen al módulo. `CatalogModule.cs` registra sus servicios y rutas. `Contracts` contiene solo las interfaces/tipos públicos que otros módulos pueden consumir. `module.manifest.json` declara metadatos, no lógica ni secretos.

No se moverán de golpe los archivos actuales. Cada extracción será una historia acotada, mantendrá el comportamiento y tendrá pruebas de arquitectura antes de cambiar referencias. Se empezará por un módulo opcional nuevo, como `Warehouses`, para probar los límites sin poner en riesgo la venta diaria.

## Paquete y rutas de operación

```text
packaging/modules/<module-id>/                 # Definición reproducible del paquete
artifacts/modules/<module-id>/<version>.zip    # Salida generada; no fuente ni secretos
%ProgramData%/Pos/Modules/<module-id>/<version>/ # Módulo instalado por el servicio
```

El paquete incluye manifiesto, ensamblados, migraciones, versión mínima del anfitrión, hash y firma. No incluye configuración local del comercio ni contraseñas. Los paquetes y binarios generados no se guardan en Git.

Manifiesto mínimo:

```json
{
  "id": "warehouses",
  "version": "1.0.0",
  "hostApi": { "min": 1, "max": 1 },
  "dependencies": [
    { "id": "catalog", "minVersion": "1.0.0", "maxVersionExclusive": "2.0.0" },
    { "id": "inventory-basic", "minVersion": "1.0.0", "maxVersionExclusive": "2.0.0" }
  ],
  "permissions": ["Warehouse.View", "Warehouse.Transfer"],
  "entitlement": "warehouses.advanced",
  "migrations": ["warehouses:0001"]
}
```

Las versiones de módulo usan SemVer estable `major.minor.patch`; los rangos de dependencia incluyen el mínimo y excluyen el máximo. El anfitrión también valida el rango entero `hostApi`.

## Instalación, activación y actualización

1. El administrador selecciona un paquete desde el catálogo local/confiable.
2. El servidor valida firma/hash, versión del anfitrión, licencia y permisos de instalación.
3. Resuelve dependencias y rechaza versiones incompatibles, ciclos o módulos ausentes antes de modificar datos.
4. Entra en mantenimiento, espera a que no haya operaciones activas y crea un respaldo verificable.
5. Ejecuta las migraciones idempotentes del módulo bajo un bloqueo de migración central; registra módulo, versión y migraciones aplicadas.
6. Activa el módulo y publica su estado y versión a las terminales conectadas.
7. Las terminales reciben una versión compatible del cliente WPF en el mismo ciclo de actualización. El servidor sigue validando permisos y entitlements en cada operación.

Desactivar un módulo oculta sus acciones y rechaza nuevas operaciones protegidas, pero conserva sus datos. La reversión de una migración destructiva no se intenta automáticamente: se restaura un respaldo o se aplica una migración compensatoria probada.

## Límites de la primera entrega

- La venta, el catálogo y el inventario básico siguen incluidos en la instalación base, conforme al MVP aprobado.
- El Hito 13 permite instalar y cargar paquetes oficiales firmados al reiniciar. Las rutas de módulos se publican solo bajo su prefijo; en su corte inicial todas exigen `ManageModules`. Hito 14 habilita permisos declarados por endpoint para el módulo oficial Almacenes.
- El entrypoint se carga en el mismo proceso del servidor; la firma identifica al publicador pero no crea un sandbox. Solo se debe confiar en código oficial revisado.
- Almacenes registra su entrada WPF y el filtro de almacén desde su ensamblado de escritorio firmado. Las terminales reciben el paquete desde el servicio y verifican manifiesto, firma, hashes y rango `desktopApi`. Las extensiones disponibles son explícitas y el anfitrión no permite reemplazar pantallas de venta/cobro.
- Instalar DLL de terceros copiándolas a una carpeta queda fuera de alcance. La distribución independiente de módulos requiere firma, compatibilidad, ciclo de actualización de todas las terminales y revisión de seguridad.
- No se crean microservicios, bases separadas por módulo ni acceso directo a PostgreSQL desde módulos de escritorio.
- Una licencia no autoriza ignorar permisos del operador; el servicio valida ambas condiciones.

## Riesgos y mitigaciones

| Riesgo | Mitigación propuesta |
|---|---|
| Módulo incompatible deja la instalación inutilizable | Validar dependencias y compatibilidad antes de escribir; mantener paquete anterior y recuperación por respaldo |
| Dos terminales migran a la vez | Un único coordinador en el servidor con bloqueo transaccional |
| Un módulo altera tablas de otro módulo | Propiedad explícita de tablas/esquema, APIs/contratos entre módulos y pruebas de arquitectura |
| Código no confiable ejecuta acciones en el servidor | Paquetes oficiales firmados; no permitir DLL arbitraria en la primera versión |
| Desactivar módulo oculta información necesaria | Retener datos y permitir consulta/exportación con el nivel de acceso previsto |
| Aplicaciones WPF de distintas versiones | El servicio expone versión de módulo/API; el actualizador despliega cliente y servidor compatibles juntos |

## Gate aprobado para Hito 13

El usuario aprobó abrir Hito 13 el 12 de octubre de 2026 bajo los controles definidos en la especificación. La aprobación habilita el desarrollo escalonado; cada fase mantiene sus propios criterios de aceptación y no se considera funcional hasta pasar el flujo integral. Las primeras pruebas son:

- El manifiesto rechaza IDs duplicados, versión inválida y permisos repetidos.
- El resolvedor acepta dependencias compatibles y rechaza dependencia ausente, versión incompatible y ciclos sin tocar la base de datos.
- La migración de un módulo se aplica una sola vez y solo desde el servidor; dos intentos concurrentes no la duplican.
- Un error de migración impide habilitar el módulo y conserva un diagnóstico recuperable.
- La terminal no presenta comandos del módulo si el módulo está inactivo o el operador carece de permiso; el servicio también rechaza la llamada directa.
- El paquete no contiene secretos ni datos locales del comercio y una firma/hash inválidos son rechazados.
- El producto base sigue vendiendo y cerrando caja cuando un módulo premium no está instalado.

## Siguiente paso

Los cortes iniciales implementaron contratos/manifiestos, resolución, paquetes firmados, anillo de confianza y CLI de publicación. Hito 13 agrega respaldo, migraciones, bloqueo de mantenimiento, versiones de terminal y carga en memoria después de reiniciar. `ModulePostgresActivationTests` valida el paquete de ejemplo, el respaldo, la migración, la activación, desactivación y reinicio del servidor, además de restaurar el respaldo previo a la instalación en otra base PostgreSQL temporal. Comprueba que la base activa conserva las tablas del módulo desactivado y que el respaldo recuperado contiene el esquema base. Las bases temporales se eliminan al finalizar; no se cambia la configuración activa.

### Criterios de aceptación del primer corte

- Un manifiesto válido con versiones SemVer estables `major.minor.patch` se lee como contrato tipado.
- Se rechazan ids inválidos, versiones no estables o con ceros iniciales, permisos repetidos y metadatos estructuralmente inválidos con un diagnóstico de manifiesto.
- El resolvedor calcula, en memoria y en orden determinista, el cierre de dependencias con cada dependencia antes de su módulo.
- Se rechazan ids duplicados en el catálogo, módulos solicitados ausentes, dependencias ausentes o incompatibles, ciclos y rangos de host API incompatibles.
- La validación y resolución no abren conexiones, no escriben datos y no cargan ensamblados.
- La plantilla documenta las carpetas/propiedad de capas y no presenta el módulo como instalable en esta etapa.

### Criterios de aceptación del verificador de paquetes

- Acepta un ZIP cuyo índice esté firmado por una clave pública confiable y cuyos archivos coincidan con los hashes declarados.
- Rechaza claves desconocidas, firmas inválidas, hashes distintos, inventarios que omitan o agreguen archivos, entradas duplicadas y rutas de extracción inseguras.
- Rechaza formatos, tamaños y tipos de entrada no admitidos antes de extraer contenido o modificar datos.
- No extrae archivos, no carga ensamblados y no abre conexiones a la base de datos.
- El constructor genera el mismo índice para los mismos archivos ordenados, deja una firma PSS aleatoria y no guarda la clave privada.

### Criterios de aceptación del anillo de confianza

- Acepta solo claves públicas SPKI PEM RSA de al menos 2048 bits y estados `Active`, `Retiring` o `Revoked`.
- `Active` y `Retiring` validan paquetes; `Revoked` se rechaza y un archivo inválido deja el anillo sin confianza cargada.
- La configuración vive fuera del paquete/directorio de actualización, no contiene material privado y solo la modifica una identidad administradora autorizada.
- Cuando se configura `ModuleTrust:KeyRingPath`, el servidor exige ruta absoluta y carga/valida el archivo durante el arranque.

### Criterios de aceptación de la CLI de publicación

- Solo acepta comandos/opciones conocidos y requiere manifiesto, lista explícita de archivos, staging, salida, ID de clave y huella para construir.
- Busca un certificado vigente en `CurrentUser\\My` o `LocalMachine\\My`, requiere clave privada RSA utilizable y nunca la exporta.
- Exporta únicamente clave pública RSA SPKI PEM para incorporarla al anillo del servidor.
- Rechaza rutas inseguras, duplicadas, enlaces, salidas dentro del staging y nombres/extensiones típicos de secretos.
- Escribe el ZIP final atómicamente y no extrae ni ejecuta módulos.
- La herramienta no instala módulos ni ejecuta migraciones.
