# Licencias mensuales con VPS

## Qué hace esta primera versión

- `Pos.Licensing.Vps` firma licencias con una llave privada que solo queda en el VPS.
- El VPS guarda las suscripciones en `/var/lib/pos-license-api/subscriptions.json`, compara el hash del secreto y conserva una copia cifrada con la llave privada para volver a emitir licencias.
- El POS local consulta al iniciar y luego cada cuatro horas. Ante un error de red reintenta cada quince minutos. Las ventas no esperan esa consulta.
- La licencia sigue funcionando offline hasta su fecha de vencimiento y los dos días de gracia acordados.
- No se importa un archivo en las renovaciones. La primera licencia sí se importa una vez.
- En este corte, el pago se registra en el VPS con una operación administrativa. No hay pasarela de pago ni webhook todavía; por eso una persona o sistema externo debe actualizar `paidThroughUtc` al confirmar cada pago.

## Instalación con Docker y Traefik

Esta opción sirve cuando Traefik ya corre en Docker. Crea en Cloudflare un registro `A` llamado `licencias` que apunte a la IPv4 del VPS. Déjalo como **DNS only** mientras se obtiene el primer certificado; no cambies los registros existentes de `@` o `www`. El dominio de esta instalación será `licencias.balanzapro.com`.

En el VPS, actualiza la copia del repositorio hasta incluir este despliegue y entra en su raíz (en este VPS: `/opt/balanza-license-source`). Verifica que la red externa de Traefik existe:

```bash
docker network inspect web_publica >/dev/null
```

Si habías instalado antes la API con systemd, detén esa instancia antes de arrancar el contenedor. Ambos modos usan la misma carpeta de datos y no deben escribirla a la vez. Este comando deshabilita solo esa unidad, si existe; conserva su llave y datos:

```bash
if sudo systemctl cat pos-license-api.service >/dev/null 2>&1; then
  sudo systemctl disable --now pos-license-api.service
fi
```

Prepara las carpetas y secretos. El script conserva una llave ya creada, crea una si falta y no imprime la llave ni el token:

```bash
sudo bash deploy/vps/prepare-pos-license-api-docker.sh
```

Construye y arranca la API en la red de Traefik. No publica un puerto del contenedor en el host; Traefik la alcanza por `web_publica` y solicita HTTPS con `myresolver`:

```bash
sudo docker compose -f deploy/vps/pos-license-api.compose.yml config --quiet
sudo docker compose -f deploy/vps/pos-license-api.compose.yml up -d --build
sudo docker compose -f deploy/vps/pos-license-api.compose.yml ps
curl --fail --show-error https://licencias.balanzapro.com/api/v1/health
```

La primera solicitud puede tardar mientras Let's Encrypt emite el certificado. Si falla, revisa `sudo docker compose -f deploy/vps/pos-license-api.compose.yml logs --tail=80` y confirma que el registro DNS apunta al VPS y que los puertos 80 y 443 llegan a Traefik. No compartas salidas de secretos ni archivos `.env`.

La imagen se construye en el VPS con Docker y .NET 10. La llave privada y el token se montan como archivos de solo lectura; los datos persistentes quedan en `/var/lib/pos-license-api`. Guarda respaldos cifrados de esos datos y de `/etc/pos-license-api`.

## Alternativa: instalación como servicio systemd

Para un VPS sin Traefik en Docker, desde una copia actualizada del repositorio ejecuta `sudo bash deploy/vps/install-pos-license-api.sh`. El instalador instala .NET 10 SDK si hace falta, crea un usuario aislado, genera una llave RSA privada y un token administrativo, y publica el servicio con `systemd`. La API escucha solo en `127.0.0.1:5180`; configura un proxy HTTPS local, por ejemplo Caddy con `deploy/vps/Caddyfile.example`. El POS rechazará una dirección de renovación que no use HTTPS. No ejecutes el instalador systemd junto al contenedor Docker.

## Confiar en la llave pública

Después de habilitar el dominio HTTPS, descarga desde el servidor POS Windows el anillo público. Este endpoint devuelve el formato que `Pos.Server` ya entiende:

```powershell
$licenseDirectory = Join-Path $env:ProgramData 'BalanzaPro\PosServer\Licensing'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
Invoke-WebRequest 'https://licencias.balanzapro.com/api/v1/license/public-key' -OutFile (Join-Path $licenseDirectory 'trusted-license-keys.json')
Restart-Service BalanzaProPosServer
```

La ruta es para la computadora que ejecuta `BalanzaProPosServer`, no para cada terminal cliente.

## Activar la consulta automática

En PowerShell como administrador, configura la dirección del endpoint:

```powershell
[Environment]::SetEnvironmentVariable('BALANZAPRO_LICENSE_RENEWAL_ENDPOINT', 'https://licencias.balanzapro.com/api/v1/renewals', 'Machine')
```

Luego ejecuta `Administrar servicio POS.cmd` como administrador, selecciona **Instalar / actualizar configuración y arrancar** y confirma que el servicio queda iniciado. El script guarda la URL en la configuración local protegida con DPAPI.

El endpoint se valida con HTTPS. Para desactivarlo, borra esa variable a nivel máquina y vuelve a reiniciar el servicio.

Cuando la llave pública esté instalada, la licencia inicial importada y el estado muestre `Active`, habilita el cumplimiento local así:

```powershell
[Environment]::SetEnvironmentVariable('BALANZAPRO_LICENSE_ENFORCE', 'true', 'Machine')
```

Vuelve a ejecutar `Administrar servicio POS.cmd` como administrador y selecciona **Instalar / actualizar configuración y arrancar**. No habilites esto antes de instalar una licencia y el anillo público, porque el POS entraría en modo de cierre seguro.

## Crear una suscripción y la licencia inicial

Abre una consola root en el VPS y usa la instalación de POS que muestra Administración → Licencia para copiar su `installationId`.

El archivo `/etc/pos-license-api/admin.curlrc`, solo legible por root, permite llamar al API sin pegar el secreto en el comando. Sustituye los datos de ejemplo y guarda el documento inicial en un archivo:

```bash
sudo curl --fail --silent --show-error --config /etc/pos-license-api/admin.curlrc \
  --header 'Content-Type: application/json' \
  --data '{"installationId":"GUID-DE-ADMINISTRACION-LICENCIA","customerRif":"J-00000000-0","customerName":"Comercio","plan":"mensual","maxTerminals":3,"entitlements":[],"paidThroughUtc":"2026-11-16T00:00:00Z"}' \
  https://licencias.balanzapro.com/api/v1/admin/licenses \
  | jq -r '.documentBase64' | base64 --decode > licencia-inicial.poslic
```

Transfiere `licencia-inicial.poslic` de forma segura al servidor POS Windows e impórtala una sola vez desde Administración → Licencia. No guardes el archivo en el repositorio.

Si se pierde la respuesta del alta antes de guardar el archivo, puedes recuperar el documento firmado con el mismo `installationId`:

```bash
sudo curl --fail --silent --show-error --config /etc/pos-license-api/admin.curlrc \
  https://licencias.balanzapro.com/api/v1/admin/installations/GUID-DE-ADMINISTRACION-LICENCIA/license-document \
  | jq -r '.documentBase64' | base64 --decode > licencia-inicial.poslic
```

## Confirmar pagos mensuales

Cuando se confirma un pago, el sistema administrativo extiende la fecha pagada del registro. El servicio emitirá automáticamente una licencia firmada cuando el POS vuelva a consultar:

```bash
sudo curl --fail --silent --show-error --config /etc/pos-license-api/admin.curlrc \
  --request PUT --header 'Content-Type: application/json' \
  --data '{"paidThroughUtc":"2026-12-16T00:00:00Z"}' \
  https://licencias.balanzapro.com/api/v1/admin/licenses/ID-DE-LICENCIA/paid-through
```

El identificador `lic_...` se devuelve al crear la suscripción. Para conectar una pasarela de pago y actualizar ese campo automáticamente hará falta implementar y configurar el webhook del proveedor elegido.

## Respaldo

Respalda `/var/lib/pos-license-api/subscriptions.json`, `/etc/pos-license-api/license-private.pem` y el secreto administrativo en `/etc/pos-license-api` en un lugar cifrado y con acceso limitado. Sin el registro no se conoce el estado pagado; sin la llave privada no se pueden emitir renovaciones para las licencias existentes. No incluyas estos archivos en GitHub Actions artifacts ni en el repositorio.
