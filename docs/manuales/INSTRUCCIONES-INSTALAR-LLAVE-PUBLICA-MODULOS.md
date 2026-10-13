# Instalar la llave pública de módulos en otra PC

La llave pública permite que el servidor compruebe quién firmó un módulo antes de instalarlo. Se configura una sola vez en la computadora que ejecuta el **servidor POS**. Las terminales cliente no necesitan instalarla.

## Archivos necesarios

Copia a la PC servidor:

- El ZIP firmado del módulo que vas a instalar.
- El archivo `.pem` público correspondiente a ese ZIP.

Para las entregas actuales de BalanzaPro, el identificador del publicador es `pos-warehouses-dev-2026`. El PEM es público; **no copies ni compartas la llave privada** del publicador.

## Registrar la llave

1. Inicia sesión en Windows con la misma cuenta que administra la instalación del POS.
2. Abre PowerShell y ejecuta el siguiente comando. Cambia `D:\ruta\publisher-public.pem` por la ubicación real del archivo `.pem`:

   ```powershell
   $pos = Join-Path $env:LOCALAPPDATA 'Programs\Punto de Venta'
   & (Join-Path $pos 'scripts\Install-WarehouseModuleTrust.ps1') `
     -PublicKeyPath 'D:\ruta\publisher-public.pem' `
     -KeyId 'pos-warehouses-dev-2026'
   ```

3. Confirma que PowerShell indique que la llave quedó en el anillo de confianza.

El script conserva las demás llaves registradas. Si el mismo ID ya existe con una llave pública diferente, se detiene sin reemplazarla.

## Recargar la confianza

Si esta PC tiene instalado el servicio de Windows `BalanzaProPosServer`:

1. Cierra las ventanas del POS.
2. Desde `C:\Users\<usuario>\AppData\Local\Programs\Punto de Venta`, ejecuta **Administrar servicio POS.cmd**.
3. Elige **1. Instalar / actualizar configuración y arrancar** y acepta el aviso de administrador de Windows.
4. Espera a que el servicio quede en ejecución y vuelve a abrir el POS.

Esto actualiza la configuración protegida del servicio para que pueda leer el archivo de llaves. No desinstala el módulo ni modifica sus datos.

Si no instalaste el servicio de Windows, cierra completamente el POS y vuelve a abrirlo para que el servidor local cargue la llave.

## Instalar el módulo

En el POS servidor, abre **Administración → Módulos**, selecciona el ZIP firmado, pulsa **Validar paquete** y luego **Instalar módulo**. Cuando la instalación termine, usa **Reiniciar servidor y cargar módulos** si la pantalla lo solicita.

Si el POS indica que el firmante no está registrado, comprueba que elegiste el `.pem` que acompaña a ese ZIP y que usaste el ID `pos-warehouses-dev-2026`. No desactives la verificación de firmas.
