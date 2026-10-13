# Publicar paquetes de módulos

`Pos.Modules.Publisher` crea ZIP firmados y exporta claves públicas. Está pensado para ejecutarse en la computadora de publicación. La instalación se hace desde **Administración → Módulos** en el POS, no desde esta CLI. La clave privada se conserva en el almacén de certificados de Windows; la herramienta solo la abre para firmar y nunca la exporta.

## Firma rápida desde una carpeta preparada

Desde la raíz del repositorio, ejecuta `scripts\Sign-PosModule.cmd`. El script permite elegir la carpeta de staging del módulo, selecciona un certificado vigente con clave privada de `Cert:\CurrentUser\My`, crea el ZIP, exporta únicamente la clave pública PEM y verifica la firma y los hashes. La carpeta debe contener `module.manifest.json` y `package-files.json`; la lista de archivos debe estar revisada antes de firmar.

Por defecto usa el ID `pos-warehouses-dev-2026`, que debe estar registrado como confiable en cada servidor POS que vaya a instalar el paquete. El ZIP y el PEM público se crean junto a la carpeta seleccionada. También se puede usar `scripts\Sign-PosModule.ps1` directamente con `-StageDirectory`, `-OutputZip`, `-KeyId`, `-CertificateThumbprint` y `-PublisherPath`.

La firma solo se puede producir en una cuenta que tenga acceso a la clave privada. La clave del publicador actual es no exportable y permanece en la PC firmante; copiar el PEM público a otra PC no le da capacidad para firmar. Para cambiar de computadora publicadora, crea un par nuevo con un `keyId` único, distribuye primero su clave pública a los servidores POS y luego firma con ese nuevo ID.

## Preparar un certificado de publicación en Windows

Para desarrollo o publicación interna, PowerShell puede crear una clave RSA no exportable en el almacén del usuario actual:

```powershell
$cert = New-SelfSignedCertificate `
  -Subject "CN=POS Module Publisher" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -KeyAlgorithm RSA `
  -KeyLength 3072 `
  -KeyUsage DigitalSignature `
  -KeyExportPolicy NonExportable `
  -NotAfter (Get-Date).AddYears(2)

$cert.Thumbprint
```

Este certificado autofirmado es apropiado para desarrollo controlado; para distribuir módulos a clientes, la clave debe estar custodiada por el responsable de publicación (preferiblemente HSM o servicio de firma) y la clave pública debe llegar a cada servidor por un canal administrativo confiable. No guardes certificados privados, contraseñas, claves ni secretos en el repositorio o dentro del ZIP.

## Exportar la clave pública para el servidor

Desde la raíz de la solución:

```powershell
dotnet run --project .\src\Modules\Pos.Modules.Publisher -- `
  export-public-key `
  --thumbprint "HUELLA_DEL_CERTIFICADO" `
  --output "C:\ProgramData\Pos\Security\publisher-public.pem" `
  --store CurrentUser
```

El archivo PEM contiene solo SubjectPublicKeyInfo público. Copia su contenido a `publicKeyPem` en `%ProgramData%\Pos\Security\TrustedModuleKeys.json`, junto al `keyId` que usarás al firmar:

```json
{
  "schemaVersion": 1,
  "keys": [
    {
      "keyId": "pos-publisher-2026",
      "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----",
      "status": "Active"
    }
  ]
}
```

La configuración del servicio apunta a ese archivo externo usando `ModuleTrust:KeyRingPath` o `ModuleTrust__KeyRingPath`. Protege el directorio con ACL: lectura para la identidad del servicio y escritura solo para administradores autorizados.

## Preparar los archivos

Organiza solo salidas de compilación revisadas dentro de un directorio de staging. Crea un JSON que enumere cada archivo que pertenece al módulo; no se escanean carpetas ni se incluyen archivos automáticamente:

```json
{
  "files": [
    "Pos.Modules.Warehouses.dll",
    "Desktop/Module.xaml"
  ]
}
```

Las rutas usan `/`, son relativas al staging y no pueden salir de él. Se rechazan rutas ambiguas, duplicadas, enlaces simbólicos y nombres/extensiones habituales de secretos (`.pfx`, `.p12`, `.pem`, `.key`, `.env`, `secrets.json`, `appsettings*.json`, entre otros). El manifiesto se entrega aparte y el constructor valida su contenido. La salida del ZIP debe quedar fuera del staging.

## Construir el paquete

```powershell
dotnet run --project .\src\Modules\Pos.Modules.Publisher -- `
  build `
  --manifest "C:\build\warehouses\module.manifest.json" `
  --stage "C:\build\warehouses\stage" `
  --files "C:\build\warehouses\package-files.json" `
  --output "C:\build\artifacts\warehouses-1.0.0.zip" `
  --key-id "pos-publisher-2026" `
  --thumbprint "HUELLA_DEL_CERTIFICADO" `
  --store CurrentUser
```

Usa `--store LocalMachine` si el certificado fue instalado en el almacén del equipo y la identidad actual tiene permiso para usar su clave privada. La herramienta requiere certificado vigente, clave RSA de al menos 2048 bits y acceso de firma. El ZIP contiene manifiesto, inventario SHA-256 y firma RSA-PSS. Vuelve a verificarlo con el verificador y conserva el artefacto de distribución fuera de Git.

## Instalar el paquete en el POS

El administrador valida e instala el ZIP firmado desde **Administración → Módulos**. El servidor verifica el anillo de confianza, la firma, los hashes, el manifiesto, la compatibilidad y los permisos; crea y verifica un respaldo antes de ejecutar migraciones. La instalación queda en `RestartPending` hasta reiniciar el servidor POS. En ese reinicio el servidor vuelve a verificar el paquete y carga su punto de entrada. Para módulos con interfaz, las terminales descargan el ZIP activo del servidor y verifican firma y compatibilidad antes de cargarlo.

La CLI no instala paquetes, no ejecuta migraciones y no activa funcionalidades: esas tareas pertenecen al servidor POS y a su flujo administrativo. Consulta [Módulos del POS: guía y estado](../manuales/MODULOS-POS.md) para el flujo completo.
