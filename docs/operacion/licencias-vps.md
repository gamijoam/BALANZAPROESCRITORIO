# Licencias mensuales con VPS

## Qué hace esta primera versión

- `Pos.Licensing.Vps` firma licencias con una llave privada que solo queda en el VPS.
- El VPS guarda las suscripciones en `/var/lib/pos-license-api/subscriptions.json`, compara el hash del secreto y conserva una copia cifrada con la llave privada para volver a emitir licencias.
- El POS local consulta al iniciar y luego cada cuatro horas. Ante un error de red reintenta cada quince minutos. Las ventas no esperan esa consulta.
- La licencia sigue funcionando offline hasta su fecha de vencimiento y los dos días de gracia acordados.
- No se importa un archivo en las renovaciones. La primera licencia sí se importa una vez.
- En este corte, el pago se registra en el VPS con una operación administrativa. No hay pasarela de pago ni webhook todavía; por eso una persona o sistema externo debe actualizar `paidThroughUtc` al confirmar cada pago.

## Primera instalación en Ubuntu 24.04

Publica primero el cambio del servicio en `main`. En el VPS, desde una copia actualizada de este repositorio, ejecuta:

```bash
sudo bash deploy/vps/install-pos-license-api.sh
```

El instalador detecta `x86_64` o `aarch64`, instala .NET 10 SDK si hace falta, crea el usuario aislado `pos-license-api`, genera una llave RSA privada de 3072 bits bajo `/etc/pos-license-api`, crea un token administrativo aleatorio y publica el servicio con `systemd`. La llave privada y el token no se imprimen. El servicio solo escucha en `127.0.0.1:5180`.

Comprueba el proceso:

```bash
sudo systemctl status pos-license-api --no-pager
curl --fail http://127.0.0.1:5180/api/v1/health
```

Configura un dominio con HTTPS en el proxy inverso del VPS. Si ya usas Caddy, añade una entrada como `deploy/vps/Caddyfile.example`, sustituyendo `license.example.com` por tu dominio y recargando Caddy. El POS rechazará una dirección de renovación que no use HTTPS.

## Confiar en la llave pública

Después de habilitar el dominio HTTPS, descarga desde el servidor POS Windows el anillo público. Este endpoint devuelve el formato que `Pos.Server` ya entiende:

```powershell
$licenseDirectory = Join-Path $env:ProgramData 'BalanzaPro\PosServer\Licensing'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
Invoke-WebRequest 'https://license.example.com/api/v1/license/public-key' -OutFile (Join-Path $licenseDirectory 'trusted-license-keys.json')
Restart-Service BalanzaProPosServer
```

Sustituye el dominio de ejemplo. La ruta es para la computadora que ejecuta `BalanzaProPosServer`, no para cada terminal cliente.

## Activar la consulta automática

En PowerShell como administrador, configura la dirección del endpoint:

```powershell
[Environment]::SetEnvironmentVariable('BALANZAPRO_LICENSE_RENEWAL_ENDPOINT', 'https://license.example.com/api/v1/renewals', 'Machine')
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

El token administrativo se creó al instalar y quedó en `/etc/pos-license-api/pos-license-api.env`. El archivo `/etc/pos-license-api/admin.curlrc`, solo legible por root, permite llamar al API sin pegar el secreto en el comando. Sustituye los datos de ejemplo y guarda el documento inicial en un archivo:

```bash
sudo curl --fail --silent --show-error --config /etc/pos-license-api/admin.curlrc \
  --header 'Content-Type: application/json' \
  --data '{"installationId":"GUID-DE-ADMINISTRACION-LICENCIA","customerRif":"J-00000000-0","customerName":"Comercio","plan":"mensual","maxTerminals":3,"entitlements":[],"paidThroughUtc":"2026-11-16T00:00:00Z"}' \
  http://127.0.0.1:5180/api/v1/admin/licenses \
  | jq -r '.documentBase64' | base64 --decode > licencia-inicial.poslic
```

Transfiere `licencia-inicial.poslic` de forma segura al servidor POS Windows e impórtala una sola vez desde Administración → Licencia. No guardes el archivo en el repositorio.

Si se pierde la respuesta del alta antes de guardar el archivo, puedes recuperar el documento firmado con el mismo `installationId`:

```bash
sudo curl --fail --silent --show-error --config /etc/pos-license-api/admin.curlrc \
  http://127.0.0.1:5180/api/v1/admin/installations/GUID-DE-ADMINISTRACION-LICENCIA/license-document \
  | jq -r '.documentBase64' | base64 --decode > licencia-inicial.poslic
```

## Confirmar pagos mensuales

Cuando se confirma un pago, el sistema administrativo extiende la fecha pagada del registro. El servicio emitirá automáticamente una licencia firmada cuando el POS vuelva a consultar:

```bash
sudo curl --fail --silent --show-error --config /etc/pos-license-api/admin.curlrc \
  --request PUT --header 'Content-Type: application/json' \
  --data '{"paidThroughUtc":"2026-12-16T00:00:00Z"}' \
  http://127.0.0.1:5180/api/v1/admin/licenses/ID-DE-LICENCIA/paid-through
```

El identificador `lic_...` se devuelve al crear la suscripción. Para conectar una pasarela de pago y actualizar ese campo automáticamente hará falta implementar y configurar el webhook del proveedor elegido.

## Respaldo

Respalda juntos `/var/lib/pos-license-api/subscriptions.json`, `/etc/pos-license-api/license-private.pem` y `/etc/pos-license-api/pos-license-api.env` en un lugar cifrado y con acceso limitado. Sin el registro no se conoce el estado pagado; sin la llave privada no se pueden emitir renovaciones para las licencias existentes. No incluyas estos archivos en GitHub Actions artifacts ni en el repositorio.
