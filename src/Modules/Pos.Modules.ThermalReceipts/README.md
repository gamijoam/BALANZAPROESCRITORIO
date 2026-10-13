# Guía de desarrollo: `thermal-receipts`

Esta guía describe cómo está integrado el módulo de tickets térmicos y cómo mantenerlo. El módulo emite comprobantes comerciales no fiscales. La plantilla se comparte en el servidor; la impresora y las copias pertenecen a cada terminal.

## Componentes

| Ruta | Responsabilidad |
| --- | --- |
| `src/Modules/Pos.Modules.ThermalReceipts` | Módulo de servidor, endpoints, validación de plantilla y manifiesto. |
| `src/Modules/Pos.Modules.ThermalReceipts.Desktop` | Pantalla de configuración, vista previa, cola de Windows e integración con el anfitrión. |
| `src/Pos.Shared.Contracts/ThermalReceiptContracts.cs` | DTO compartidos por el servidor y el escritorio. |
| `src/Modules/Pos.Desktop.Modules.Abstractions/IPosDesktopModule.cs` | Contrato público de extensión de comprobantes del POS. |
| `src/Modules/Pos.Modules.ThermalReceipts/migrations/thermal-receipts-0001.json` | Migración aditiva de la plantilla compartida. |
| `modulos/thermal-receipts/1.0.0` | ZIP firmado distribuible, clave pública y ficha de publicación. |
| `scripts/Build-ThermalReceiptModulePackage.ps1` | Compila, empaqueta, firma y verifica el ZIP. |

El `.zip` publicado es el paquete que se instala desde Administración → Módulos. La actualización del anfitrión (POS/Desktop API 1.3.0) se publica por separado; los módulos que usan una API nueva necesitan ese anfitrión una sola vez.

## Manifiesto y compatibilidad

`module.manifest.json` define:

- ID `thermal-receipts`, versión semántica y API de servidor.
- Ensamblados y tipos de entrada para servidor y escritorio.
- Desktop API mínima `1.3.0`, con límite exclusivo `2.0.0`.
- Permisos `ThermalReceipts.Print` y `ThermalReceipts.Configure`.
- Migración `thermal-receipts:0001`; no tiene dependencias de otros módulos.

El cargador del anfitrión comprueba la compatibilidad de Desktop API antes de cargar el ensamblado. El publicador incluye en la firma el manifiesto y la lista cerrada de archivos del paquete. No agregues DLL, migraciones ni archivos auxiliares sin incorporarlos a `package-files.json` en el script y regenerar el ZIP firmado.

## Flujo de datos y permisos

1. El POS confirma la venta mediante el servidor.
2. Solo después de una respuesta exitosa, el anfitrión entrega el comprobante confirmado a `IPosReceiptExtension.PrintAsync`.
3. La extensión obtiene la plantilla compartida con `GET /api/modules/thermal-receipts/template`, consulta la impresora configurada y envía el documento a la cola de Windows.
4. Un error de impresión se devuelve como resultado de impresión fallido; no revierte ni vuelve a enviar la venta. Desde el historial, el mismo comprobante se puede previsualizar e intentar imprimir de nuevo.
5. Si la extensión no está disponible o no está activa, el anfitrión conserva su ventana de comprobante.

El servidor autoriza los endpoints: `ThermalReceipts.Print` permite leer la plantilla y `ThermalReceipts.Configure` permite guardarla. En el POS actual se asignan respectivamente a `ConfirmSale` y `ManageModules`. El cliente de escritorio usa contratos HTTP; no accede a PostgreSQL. La contraseña ni las credenciales de PostgreSQL se guardan en el módulo.

La plantilla compartida vive en el esquema PostgreSQL aislado del módulo `m_thermal_receipts_<hash>` y en la tabla `receipt_templates`. La migración crea la tabla; desactivar el módulo no elimina la plantilla. Los endpoints usan `ConnectionStrings:Pos` del servidor y nunca reciben credenciales desde la terminal.

La selección de impresora y las copias se guardan localmente en:

```text
%LOCALAPPDATA%\PuntoDeVenta\Configuracion\thermal-receipts-printer.json
```

Este archivo se excluye del ZIP y del repositorio. Las copias admitidas son 1 a 3. Cada terminal debe elegir una impresora Windows instalada y ejecutar una impresión de prueba.

## Esquema de plantilla

El contrato `ThermalReceiptTemplate` usa `SchemaVersion = 1`. El servidor valida y normaliza antes de persistir:

- Papel: 58 u 80 mm; alineación: izquierda, centro o derecha; tamaño: 80 % a 150 %.
- Encabezado y pie: máximo 5 líneas, 60 caracteres por línea.
- Secciones permitidas: información de venta, cliente, artículos, total, ajuste de conversión y pagos. Artículos y total son obligatorios.
- Campos permitidos por artículo: SKU, descripción, cantidad, precio unitario e importe. Descripción, cantidad e importe son obligatorios.
- Las etiquetas se limitan al catálogo publicado; la mayoría admite 32 caracteres y el aviso no fiscal, 120.

Las etiquetas, secciones y columnas solo controlan presentación. No se debe calcular ni modificar precios, pagos, cantidades o importes dentro del módulo. Para ampliar el esquema, versiona el contrato y conserva la lectura de plantillas anteriores o añade una migración compatible.

La previsualización, la impresión de prueba, la venta y la reimpresión deben seguir usando `ThermalReceiptDocumentBuilder`; así no divergen las representaciones. La impresión usa `PrintQueue`/`XpsDocumentWriter` y el controlador instalado. El resultado confirma el envío a la cola, no la salida física del papel.

## Compilar, empaquetar y verificar

Requisitos: .NET SDK de la versión fijada por la solución, certificado de publicador disponible en `Cert:\CurrentUser\My` con clave privada y el script de verificación del repositorio. No guardes la clave privada ni su contraseña en Git.

```powershell
dotnet build .\src\Modules\Pos.Modules.ThermalReceipts\Pos.Modules.ThermalReceipts.csproj -c Release
dotnet build .\src\Modules\Pos.Modules.ThermalReceipts.Desktop\Pos.Modules.ThermalReceipts.Desktop.csproj -c Release
.\scripts\Build-ThermalReceiptModulePackage.ps1 -CertificateThumbprint <HUELLA_SHA1_DEL_CERTIFICADO>
```

El script coloca ZIP y clave pública bajo `artifacts\modules\thermal-receipts\<versión>`, comprueba que la ruta de salida esté dentro de `artifacts`, firma con el certificado del usuario actual y ejecuta la verificación criptográfica. Sustituye el marcador por la huella de 40 caracteres hexadecimal del certificado; nunca pases ni documentes la clave privada.

Para verificar el ZIP de publicación que está en el repositorio:

```powershell
.\scripts\Test-SignedModulePackage.ps1 `
  -PackagePath .\modulos\thermal-receipts\1.0.0\thermal-receipts-1.0.0.zip `
  -PublicKeyPath .\modulos\thermal-receipts\1.0.0\publisher-public.pem
```

Al publicar una nueva versión, incrementa `version` en el manifiesto, genera el ZIP en un directorio nuevo dentro de `modulos/thermal-receipts/<versión>/`, incluye su clave pública y SHA-256 en el README de publicación y actualiza el catálogo `modulos/README.md`. La distribución debe contener el ZIP firmado; el certificado privado nunca debe acompañarlo.

## Instalar y activar

1. Actualiza el POS anfitrión a una versión que ofrezca Desktop API 1.3.0 o posterior.
2. En el servidor POS, abre Administración → Módulos, elige el ZIP, valida e instala.
3. La instalación prepara respaldo y migración; sigue el flujo de reinicio del servidor y vuelve a abrir las terminales para cargar el ensamblado de escritorio firmado.
4. Con permiso de administración de módulos, abre Módulos → Ticket térmico, configura y guarda el diseño compartido.
5. En cada terminal, selecciona su impresora y cantidad de copias; ejecuta Imprimir prueba antes de usar ventas reales.

La plantilla se sincroniza a través del servidor. La configuración local de impresora se debe realizar en cada PC.

## Pruebas y límites verificados

Las pruebas relevantes están en `tests/Pos.Modules.Tests/ThermalReceiptModuleContractTests.cs`, `tests/Pos.Application.Tests/ThermalReceiptHostFlowTests.cs` y `tests/Pos.Integration.Tests/ModuleMigrationSqlGeneratorTests.cs`. La ficha de publicación `modulos/thermal-receipts/1.0.0/README.md` registra los comandos/resultados y sus límites para esta versión.

No declares validada la instalación real en PostgreSQL sin una base de pruebas desechable configurada. Tampoco declares compatibilidad física hasta probar el controlador y el rollo en los modelos concretos de 58/80 mm. La entrega actual es no fiscal.
