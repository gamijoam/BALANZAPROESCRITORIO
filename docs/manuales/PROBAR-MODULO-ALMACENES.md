# Probar el módulo Almacenes

## Archivos de esta entrega

- `pos-warehouses-preview.zip`: aplicación POS base actualizada, necesaria para instalar y mostrar el módulo.
- `warehouses-1.1.0.zip`: actualización firmada que se importa desde Administración → Módulos. El ID sigue siendo `warehouses`.
- `publisher-public.pem`: clave pública del firmante de desarrollo; nunca contiene la clave privada.
- `Install-WarehouseModuleTrust.ps1`: prepara el anillo local de confianza y conserva las otras claves existentes.
- `crear-modulos-pos.md`: guía para programadores sobre estructura, migraciones, pruebas, firma, instalación y actualización de módulos.

## Preparar y ejecutar en esta PC

1. Extrae esta entrega en una carpeta local que no sea temporal ni esté dentro de `Program Files`.
2. Abre PowerShell en esa carpeta y ejecuta:

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-WarehouseModuleTrust.ps1 -PublicKeyPath .\publisher-public.pem
   ```

   El script agrega la clave pública al perfil actual de Windows. Si el anillo ya contiene ese identificador con otra clave, se detiene y conserva el archivo sin cambios.
3. Extrae `pos-warehouses-preview.zip` y abre `Iniciar POS.vbs` en el equipo que actuará como servidor POS. Usa el perfil de instalación existente de la misma cuenta de Windows; la contraseña de PostgreSQL no está en los ZIP.
4. Entra con un usuario administrador y abre **Administración → Módulos**. Elige `warehouses-1.1.0.zip`; la pantalla debe mostrar **Almacenes y traslados**, completar el ID interno `warehouses` y mostrar la versión `1.1.0`. Valida e instala la actualización y deja que termine el respaldo.
5. Cierra y vuelve a abrir el POS actualizado. Confirma que Almacenes figure como activo; después usa una base recuperable y productos de prueba para seguir el flujo en `INSTRUCCIONES-ALMACENES.md`.

El arranque del POS busca automáticamente PostgreSQL en las carpetas habituales de Archivos de programa y pasa las herramientas encontradas al servicio local. El actualizador conserva y usa la configuración de PostgreSQL ya guardada; primero verifica el respaldo, detiene solo el servicio POS confirmado, migra antes de reemplazar los archivos y abre la versión nueva. No ejecutes la aceptación del flujo de almacenes sobre la base con ventas reales: primero crea/restaura una copia y comprueba en ella el respaldo, activación, asignación de terminal, venta, traslado, recepción y devolución.

## En otra computadora servidor

El archivo PEM se debe registrar en el anillo de confianza de la PC que ejecuta el servicio POS y reiniciar ese servicio antes de validar el ZIP. En esa PC se puede usar el script anterior bajo la misma cuenta Windows que ejecuta el servidor local. Si el servidor usa otra identidad o un servicio de Windows, configura la ruta del anillo en `ModuleTrust:KeyRingPath` y concede lectura al servicio. Nunca copies ni exportes la clave privada del publicador.
