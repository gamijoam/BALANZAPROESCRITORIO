# Custodia y rotación de claves de módulos

## Dónde vive cada clave

- **Clave privada de publicación:** solo en el entorno controlado que construye y publica módulos (idealmente un almacén de claves/certificados o vault de CI). No se copia al servidor POS, a las terminales, a los ZIP ni al repositorio. `ModulePackageBuilder` recibe el objeto `RSA` del proceso publicador y no persiste la clave.
- **Claves públicas confiables:** archivo externo al directorio de la aplicación, recomendado en Windows: `%ProgramData%\Pos\Security\TrustedModuleKeys.json`. El servidor lo carga si se configura `ModuleTrust:KeyRingPath` (equivalente por entorno: `ModuleTrust__KeyRingPath`). La ruta debe ser absoluta; el servidor expande variables de entorno de Windows como `%ProgramData%`.
- **Identificador:** cada clave tiene un `keyId` nuevo e inmutable. Nunca se reutiliza un ID con otro material criptográfico.

## Formato

```json
{
  "schemaVersion": 1,
  "keys": [
    {
      "keyId": "pos-publisher-2026",
      "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n<CLAVE PUBLICA SPKI PEM>\n-----END PUBLIC KEY-----",
      "status": "Active"
    }
  ]
}
```

Los únicos estados son `Active`, `Retiring` y `Revoked`, con esa capitalización. `Active` y `Retiring` se aceptan al verificar paquetes; `Revoked` se rechaza inmediatamente. Se permiten varias claves activas durante la transición. El archivo contiene únicamente claves públicas y metadatos, nunca contraseñas, claves privadas ni datos del comercio.

## Rotación normal

1. Generar un nuevo par RSA de al menos 2048 bits en el almacén seguro del publicador; dar al nuevo par un `keyId` único.
2. Añadir la clave pública nueva como `Active` al archivo del servidor y cambiar la anterior de `Active` a `Retiring`.
3. Validar y desplegar el archivo de manera atómica; asegurar que todos los servidores/instalaciones que vayan a verificar paquetes reciban el nuevo anillo.
4. Publicar paquetes nuevos solo con la clave nueva. Mantener la anterior `Retiring` durante el periodo de transición para que los paquetes ya publicados sigan verificándose.
5. Cuando ningún despliegue permitido dependa de la clave anterior, eliminarla o marcarla `Revoked` y desplegar nuevamente el archivo.

## Clave comprometida

Cambiar inmediatamente el estado a `Revoked` y distribuir el anillo corregido. El anillo se lee al iniciar el servidor. En el siguiente arranque, la verificación de un paquete instalado con esa clave falla y el servidor registra el diagnóstico; el POS base permanece disponible y el módulo no se activa. La revocación no descarga código que ya está cargado en un proceso en ejecución, por lo que también se debe reiniciar el servidor para aplicar el anillo nuevo. El registro, el ZIP y los datos del módulo se conservan para permitir investigación y recuperación administrativa.

## Permisos del archivo

Ubicar el archivo en un directorio que no se reemplace al actualizar el POS. Conceder lectura a la identidad del servicio POS y modificación solo a administradores/distribución autorizada. El servicio no necesita permiso de escritura. Protege su ACL y audita cada cambio: quien pueda modificar este archivo puede cambiar qué publicadores se consideran confiables.

El servidor comprueba la ruta absoluta y valida todo el archivo al arrancar cuando `ModuleTrust:KeyRingPath` está configurado; un archivo ilegible o inválido impide el arranque en ese modo. `ModuleTrustKeyRingStore.ReloadFromFile` permite recargarlo en memoria, pero no hay endpoint ni observador de archivos conectado a esa recarga. La instalación de módulos se realiza desde **Administración → Módulos**; los paquetes quedan pendientes hasta el reinicio del servidor, donde se verifican y cargan. Consulta [Módulos del POS: guía y estado](../manuales/MODULOS-POS.md).

## Cambios de clave durante actualizaciones

El actualizador del POS debe conservar este archivo externo y no copiarlo ni reemplazarlo desde el ZIP de la aplicación. El anillo de confianza se distribuye como configuración local administrada, aparte del paquete de actualización. Si se rota en conjunto con una actualización, desplegar primero el anillo que confía en ambas claves y después el código/paquete firmado con la nueva clave; retirar la anterior en una actualización posterior.
