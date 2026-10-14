# Publicar módulos desde cualquier equipo

## Qué cambia

La compilación WPF se hace en un Windows hospedado por GitHub. Después, un Ubuntu dedicado recibe el ZIP temporal, comprueba su contenido y lo firma. Así puedes iniciar la publicación desde cualquier PC con acceso al repositorio; no hace falta instalar el certificado de firma en esa PC.

El nuevo identificador de firma es `pos-publisher-vps-2026`. La clave privada queda en el VPS, en `/etc/pos-module-signer/pos-publisher-vps-2026-private.pem`; el archivo tiene permisos `0600` y pertenece a la cuenta dedicada `pos-module-signing`. No se copia a GitHub, al paquete instalador ni al POS. La clave pública está en `security/module-publishers/pos-publisher-vps-2026.pem`.

La firma sigue siendo RSA-PSS con SHA-256, igual que el verificador actual. Los paquetes firmados antes siguen funcionando con su ID de clave anterior.

## Publicar un módulo

1. Integra en `main` el código del módulo que quieres publicar.
2. En GitHub abre **Actions → Publicar módulo firmado → Run workflow**.
3. Deja seleccionada la rama `main`. Elige un módulo individual o **all** para publicar juntos los tres módulos distribuibles vigentes: `warehouses`, `thermal-receipts` y `advanced-reports`.
4. Si GitHub muestra una aprobación para `module-signing`, revisa el módulo y aprueba el trabajo de firma.
5. Descarga el artefacto `signed-module-output`. Incluye un ZIP firmado y su SHA-256 por cada módulo elegido, además de `publisher-public.pem`. La opción **all** valida que estén los tres antes de firmar.
6. En el POS, instala el paquete desde **Administración → Módulos**. El publicador nuevo se incluye automáticamente en el paquete Windows x64 y se agrega al anillo sin borrar las claves existentes. El servidor debe reiniciarse para volver a cargar los módulos.

El flujo manual rechaza ejecuciones desde ramas que no sean `main`. El runner de firma no descarga ni compila el repositorio: solo recibe el artefacto temporal y ejecuta el publicador Linux autónomo que compiló Windows.

## Incorporar la clave a un POS existente

En la carpeta extraída de la entrega Windows x64, abre PowerShell y ejecuta:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-ModulePublisherTrust.ps1 `
  -PublicKeyPath .\trusted-publishers\pos-publisher-vps-2026.pem `
  -KeyId 'pos-publisher-vps-2026'
```

El script conserva las entradas actuales. Si el ID ya existe con otra clave, se detiene y deja el archivo intacto. En el flujo normal, `Actualizar POS.cmd` llama al script automáticamente.

## Configurar el runner de GitHub en el VPS

La clave ya está creada en el VPS y no se debe regenerar. Falta registrar el runner con el repositorio. Esto requiere un token temporal que GitHub muestra en su página; vence aproximadamente una hora después de crearlo. **No uses ni pegues aquí el token personal de GitHub.**

1. En el repositorio abre **Settings → Actions → Runners → New self-hosted runner** y elige **Linux / x64**.
2. En el VPS, usa la cuenta `pos-module-signing` y el directorio `/opt/pos-module-runner`. Ejecuta las instrucciones oficiales de descarga y extracción que GitHub muestra allí.
3. Cuando ya estén descargados los archivos, vuelve a GitHub, crea un token de registro temporal y en el VPS ejecuta `./config.sh` con la URL del repositorio, el nombre `pos-module-signing-vps` y la etiqueta `pos-module-signing`. Para que el token no quede en el historial de la terminal, léelo de forma oculta y pásalo a `config.sh`:

   ```bash
   read -r -s -p 'Token temporal de registro de GitHub: ' RUNNER_TOKEN; echo
   ./config.sh --unattended --url https://github.com/gamijoam/BALANZAPROESCRITORIO --token "$RUNNER_TOKEN" --name pos-module-signing-vps --labels pos-module-signing --work _work
   unset RUNNER_TOKEN
   ```

4. Desde `/opt/pos-module-runner`, instala y arranca el servicio del runner como administrador:

   ```bash
   sudo ./svc.sh install pos-module-signing
   sudo ./svc.sh start
   sudo ./svc.sh status
   ```

5. En **Settings → Environments**, crea `module-signing`, limita sus despliegues a `main` y configura al propietario del repositorio como revisor requerido.
6. Confirma en **Settings → Actions → Runners** que `pos-module-signing-vps` aparece `Idle` con la etiqueta `pos-module-signing`.

El repositorio es público. No añadas eventos `pull_request`, `push` u otros que envíen código al runner privado. Conserva la aprobación requerida de `module-signing` y protege los cambios del flujo y del publicador en `main`. GitHub advierte que los runners propios pueden quedar comprometidos si ejecutan código no confiable; las revisiones y las restricciones de rama reducen quién puede iniciar la firma, pero el runner no es una VM desechable.

## Comandos locales de preparación

Para comprobar la preparación local de todos los módulos sin usar GitHub Actions, desde Windows ejecuta:

```powershell
.\scripts\Prepare-ModulePackageForSigning.ps1 -Module all
```

El resultado se guarda debajo de `artifacts\module-release-input`. Nunca distribuyas ese ZIP temporal: no tiene firma. La clave privada no se necesita ni se entrega a los equipos de desarrollo.
