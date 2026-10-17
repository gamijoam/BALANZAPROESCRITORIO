# Instalar la clave pública de confianza de módulos

## Sí, el POS servidor debe confiar en esta clave

El servidor POS necesita la clave pública para comprobar que cada módulo está firmado por el publicador oficial y que el paquete no fue alterado. No sirve para descifrar el ZIP. La clave privada que firma los módulos permanece protegida en el VPS y nunca se instala en una PC del POS.

La clave pública oficial ya viene en `trusted-publishers\pos-publisher-vps-2026.pem`. No generes otra clave: no coincidiría con la firma de los módulos incluidos.

## Instalación nueva

1. Abre `Iniciar POS.vbs` y configura el POS servidor.
2. Ejecuta `Actualizar POS.cmd`. Este paso registra automáticamente la clave pública en el anillo de confianza de la cuenta actual de Windows.
3. Instala o actualiza el servicio desde `Administrar servicio POS.cmd`, con la opción **Instalar / actualizar configuración y arrancar**. Así el servicio de Windows recibe la ruta del anillo de confianza y permiso de lectura.
4. En el POS servidor, entra en **Administración → Módulos**, valida e instala los ZIP de `modulos` y luego elige **Reiniciar servidor y cargar módulos**.

La clave se registra en:

```text
%LOCALAPPDATA%\PuntoDeVenta\Security\TrustedModuleKeys.json
```

El servicio de Windows corre como `SYSTEM`; la configuración del servicio le señala ese archivo y le concede permiso de lectura.

## POS servidor ya instalado

Ejecuta `Actualizar POS.cmd` con la misma cuenta de Windows que usa el POS. Luego, desde la instalación permanente, abre `Administrar servicio POS.cmd` y elige **Instalar / actualizar configuración y arrancar**. Este paso vuelve a guardar en el servicio la ruta al anillo de confianza, algo necesario si el servicio se configuró antes de registrar esta clave.

No hace falta instalar esta clave en las terminales cliente: el servidor principal es quien valida e instala los módulos.

## Registro manual si falló el automático

Desde la carpeta raíz del paquete extraído, abre PowerShell con la misma cuenta de Windows que usa el POS servidor y ejecuta:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-ModulePublisherTrust.ps1 -PublicKeyPath .\trusted-publishers\pos-publisher-vps-2026.pem -KeyId 'pos-publisher-vps-2026'
```

No hace falta abrir PowerShell como administrador para registrar la clave. Si el servicio ya está instalado, después actualiza su configuración con la opción indicada arriba.

El script agrega esta clave sin quitar otras. Si ya existe el mismo identificador asociado a una clave distinta, se detiene sin sobrescribir el anillo; en ese caso no borres el archivo manualmente.

## Crear una clave nueva para firmar

No es parte de la instalación de un POS. La generación o rotación del par de firma se realiza en el VPS y requiere distribuir la nueva clave pública a los servidores POS antes de aceptar módulos firmados con ella.
