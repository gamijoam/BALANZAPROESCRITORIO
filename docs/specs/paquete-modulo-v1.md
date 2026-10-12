# Formato de paquete de módulo POS, versión 1

Este formato define un ZIP verificable antes de instalar un módulo. El verificador no extrae contenido, no carga ensamblados y no ejecuta migraciones.

## Archivos obligatorios y reservados

```text
package.index.json       # Índice firmado, UTF-8; se firman sus bytes exactos
package.signature        # Firma RSA-PSS/SHA-256 del índice, codificada en Base64
module.manifest.json     # Manifiesto que valida Pos.Modules.Abstractions
<archivos del módulo>    # Todos declarados en el inventario firmado
```

Un módulo ejecutable declara `entryAssembly` (nombre simple de un `.dll` en la raíz del ZIP) y `entryType` (tipo público con constructor vacío que implementa `IPosServerModule`). Debe declarar ambos; se omiten juntos en paquetes que solo contienen migraciones. El host carga ensamblados firmados en memoria, sin extraerlos a disco, y monta sus rutas exclusivamente bajo `/api/v1/modules/{id}`. Un filtro de host exige sesión autenticada y permiso `ManageModules` a todas las rutas del módulo.

Ejemplo de `package.index.json`:

```json
{
  "formatVersion": 1,
  "signerKeyId": "pos-oficial-2026",
  "files": [
    {
      "path": "module.manifest.json",
      "sha256": "<64 caracteres hexadecimales SHA-256 en minúsculas>"
    },
    {
      "path": "Pos.Modules.Warehouses.dll",
      "sha256": "<64 caracteres hexadecimales SHA-256 en minúsculas>"
    }
  ]
}
```

La firma es el resultado de RSA-PSS usando SHA-256 sobre los bytes UTF-8 exactos de `package.index.json`, sin salto de línea agregado por el firmador. La clave pública asociada al `signerKeyId` se configura de confianza en el servidor y no se acepta desde el ZIP. Se exige RSA de al menos 2048 bits. La clave privada de publicación nunca se distribuye con el POS ni se guarda en este repositorio.

`ModulePackageBuilder` recibe el manifiesto y una lista explícita de archivos en memoria; rechaza nombres duplicados, rutas reservadas/inseguras y límites excedidos. Escribe entradas en orden ordinal, con marca de tiempo ZIP fija y sin compresión. Esto deja estable el manifiesto, hashes e índice para los mismos insumos. RSA-PSS incorpora aleatoriedad criptográfica, por lo que la firma y el ZIP completo no son byte a byte reproducibles. El constructor devuelve bytes y no guarda ni obtiene la clave de publicación.

## Reglas de validación

- El índice debe tener `formatVersion: 1`, una clave firmante confiable y un inventario no vacío.
- El inventario enumera todos los archivos del ZIP, salvo el índice y la firma reservados; no puede haber rutas ausentes, adicionales o duplicadas (incluida duplicidad que no distinga mayúsculas/minúsculas en Windows).
- Cada hash se compara con SHA-256 del contenido descomprimido. El manifiesto debe estar inventariado y pasar la validación de `Pos.Modules.Abstractions`.
- Las rutas usan `/`, caracteres ASCII seguros y rutas relativas. Se rechazan `..`, `.`, segmentos vacíos, rutas absolutas, `\\`, `:`, entradas de directorio, enlaces simbólicos y tipos especiales.
- Límites actuales: máximo 2.048 entradas ZIP, 128 MiB descomprimidos por archivo, 512 MiB en total, 1 MiB para índice/manifiesto, 16 KiB para firma y razón de compresión máxima de 1.000:1 para archivos mayores de 1 MiB.
- Cualquier fallo rechaza el paquete completo antes de extraer o modificar datos.
- `module.manifest.json` debe declarar `clientApi.min` y `clientApi.maxExclusive` como versiones SemVer estables. Cada inicio de sesión informa la versión del cliente; la instalación se bloquea hasta que toda terminal emparejada activa reporte una versión dentro del rango. Una versión faltante también bloquea.

## Migraciones declarativas v1

Cada ID de migración declarado como `modulo:identificador` se entrega como `migrations/modulo-identificador.json`. El archivo está incluido en el inventario firmado y usa `formatVersion: 1` con una lista `operations`. Solo se admiten `createTable`, `addNullableColumn` y `createIndex`; los tipos PostgreSQL son una lista cerrada (`uuid`, `text`, `integer`, `bigint`, `boolean`, `numeric(18,4)`, `timestamptz`, `date`). Los nombres de tablas/columnas/índices son identificadores minúsculos con números y guion bajo. Las tablas se crean en el esquema `m_<id-normalizado>_<hash>` propio del módulo. No se acepta SQL libre.

Ejemplo de archivo:

```json
{
  "formatVersion": 1,
  "operations": [
    {
      "type": "createTable",
      "table": "warehouse",
      "columns": [
        { "name": "id", "dataType": "uuid", "nullable": false, "primaryKey": true },
        { "name": "name", "dataType": "text", "nullable": false, "primaryKey": false }
      ]
    }
  ]
}
```

## Límites de la implementación actual

`Pos.Modules.Packaging` construye ZIPs firmados, verifica el formato en memoria/flujo y almacena el ZIP original en una carpeta versionada. En el siguiente arranque el servidor vuelve a verificar firma, id, versión, host API y terminales; carga el punto de entrada desde memoria y conserva diagnóstico/estado si falla. Las rutas requieren sesión y aplican el permiso declarado por endpoint, que también debe figurar en el manifiesto; Administración → Módulos conserva su protección administrativa. El servidor no se cae si un módulo no activa; permanece disponible el POS base. Cada login informa versión del cliente; clientes anteriores pueden omitirla para seguir iniciando sesión, pero bloquean activación hasta actualizarse. Falta probar migración, instalación, activación y reinicio con PostgreSQL real. El código de módulos confiables corre dentro del proceso del servidor y debe tratarse como código de máxima confianza, no como sandbox.
