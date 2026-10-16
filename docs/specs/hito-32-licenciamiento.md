# Hito 32 — Licenciamiento local y renovación mensual

## Estado

**Aprobado para implementación por el usuario el 16 de octubre de 2026.** Este hito convierte en reglas de producto las decisiones acordadas para licencias firmadas, operación sin Internet y renovación de suscripciones.

## Decisiones

- `Pos.Server` es la autoridad local para licencias, permisos y cupos de terminales.
- El uso normal del POS no requiere Internet. El servidor local valida el archivo `.poslic` firmado.
- El archivo identifica la instalación del servidor, el plan, los permisos, los terminales permitidos y las fechas de emisión/vencimiento.
- La llave privada que emite licencias es distinta de la llave de firma de módulos. El POS incluye solamente llaves públicas confiables.
- Si Internet está disponible, el servidor consulta al VPS al iniciar, al recuperar conexión y cada cuatro horas. La consulta ocurre en segundo plano y nunca bloquea una venta.
- Una renovación pagada se descarga y guarda automáticamente. El usuario no importa un documento cada mes.
- La licencia conserva su validez sin conexión hasta el vencimiento. Después hay dos días de gracia; al terminar, el servidor entra en cierre seguro.
- Durante el cierre seguro se pueden terminar ventas ya iniciadas, cerrar cajas, registrar devoluciones y cobrar cuentas pendientes, además de leer historial/reportes. Se bloquean ventas nuevas, apertura de cajas y emisión de documentos nuevos.
- El cupo cuenta terminales emparejados y activos. Desactivar una terminal libera el cupo.
- Una instalación bloqueada mantiene activo el proceso de renovación. Si el pago consta en el VPS y vuelve Internet, la licencia se renueva y el servicio se desbloquea sin intervención local.
- El desbloqueo de emergencia usa una respuesta firmada de un solo uso, ligada a la instalación y a una solicitud reciente. No se usa una clave maestra.
- El reloj del sistema se vigila con una marca de tiempo local protegida. La restauración de copias antiguas puede evadir controles puramente locales; no se afirma que DPAPI sea un reloj inviolable.

## Fases

### 32A — Contrato y validación local

- Definir el formato versionado de `.poslic` y la separación entre contenido y firma.
- Verificar criptográficamente una licencia con llaves públicas confiables.
- Rechazar licencias malformadas, modificadas, vencidas o ligadas a otra instalación.
- Evaluar vigencia local con dos días de gracia.
- Guardar una identidad estable y el archivo activo de forma atómica en la carpeta de datos local.
- Exponer estado e importación de licencia en endpoints del servidor reservados para administradores.

El formato del primer corte es un sobre JSON con `schemaVersion`, `keyId`, `payload` y `signature`. `payload` contiene en Base64 los bytes UTF-8 del JSON de la licencia. La firma RSA-PSS con SHA-256 cubre `BalanzaPro.POS-LICENSE.v1\n`, seguido por `keyId`, un salto de línea y los bytes exactos de `payload`. Así, el verificador no depende de volver a serializar JSON con una forma canónica implícita. La licencia tiene `schemaVersion`, `licenseId`, `customerRif`, `customerName`, `installationId`, `plan`, `maxTerminals`, `entitlements`, `issuedAtUtc` y `expiresAtUtc`. La gracia de dos días es una política del servidor y no un valor que el archivo pueda ampliar.

### 32B — Autoridad local

- Persistir identidad de instalación, licencia y última hora confiable.
- Mostrar estado de licencia en administración.
- Comprobar permiso de entitlements en instalaciones/operaciones de módulos.
- Aplicar modo de cierre seguro en el servidor y límite de terminales en emparejamiento.

### 32C — Renovación con el VPS

- Definir e implementar el contrato HTTPS de consulta y renovación en el VPS.
- Consultar al iniciar, cuando vuelve la conexión y cada cuatro horas, con reintentos limitados.
- Guardar atómicamente la licencia firmada y conservar la última licencia válida ante errores de red.

**Estado 16 de octubre de 2026:** primera implementación disponible en `Pos.Licensing.Vps`. Ubuntu 24.04 puede alojarla como servicio systemd o como contenedor Docker detrás de Traefik. El POS consulta al inicio y cada cuatro horas; ante fallos de red reintenta cada quince minutos. Las suscripciones se guardan en un archivo JSON atómico, fuera del repositorio, y la llave privada solo existe en el VPS. La respuesta se verifica localmente con el anillo de llaves públicas antes de reemplazar la licencia.

El primer corte no integra cobros con una pasarela: un administrador registra el período pagado en el VPS mediante el endpoint administrativo. Tras ese registro, la renovación del POS es automática. El paquete oficial trae por defecto el endpoint HTTPS de `licencias.balanzapro.com`; la pestaña de licencia muestra si la consulta está configurada y si el bloqueo está activado. Ver `docs/operacion/licencias-vps.md` para instalar, respaldar y operar el servicio.
- Renovar automáticamente incluso si el POS está en cierre seguro.

### 32D — Recuperación offline

- Generar un código de solicitud local ligado a instalación y nonce aleatorio.
- Validar una respuesta firmada de un solo uso importada por código, QR o USB.
- Registrar motivo, autorización y resultado en auditoría.

## Criterios de aceptación

- Una licencia con firma confiable y contenido intacto se verifica sin Internet.
- Una firma incorrecta, llave desconocida, versión de formato desconocida, licencia malformada o instalación distinta se rechaza.
- La vigencia es normal antes de `expiresAtUtc`, está en gracia hasta dos días después y queda bloqueada al alcanzar exactamente ese límite.
- La interrupción de Internet no bloquea operaciones dentro de la vigencia/gracia.
- La respuesta del VPS no se considera renovada hasta verificarse su firma y escribirse atómicamente.
- Los errores de red no borran ni reemplazan la última licencia válida.
- La consulta al VPS no se ejecuta en la ruta de una venta ni puede retrasar una venta.
- La revocación remota se aplica al siguiente pulso exitoso; una terminal aislada no puede recibirla antes de reconectarse.
- Las llaves de licencias y módulos no se comparten.

## Límite del primer corte

La renovación requiere que el servicio VPS esté desplegado por HTTPS y que el servidor POS local tenga la llave pública. El paquete oficial configura su endpoint de renovación. El cumplimiento de licencias permanece desactivado por defecto hasta habilitarlo después de instalar una licencia válida.

## Configuración de instalación

- El modo de cumplimiento queda desactivado por defecto mediante `Licensing:Enforce=false`. Activarlo antes de instalar las llaves públicas y una licencia válida bloquearía el POS.
- En ejecución como servicio de Windows, los datos se guardan en `%ProgramData%\BalanzaPro\PosServer\Licensing`; en ejecución interactiva se guardan en `%LocalAppData%\BalanzaPro\PosServer\Licensing`. Se puede cambiar con `Licensing:DataPath`.
- El servidor busca `trusted-license-keys.json` dentro de esa carpeta, salvo que `Licensing:TrustKeyRingPath` indique otra ruta. El archivo debe contener solo las llaves públicas confiables; nunca se copia la llave privada del emisor al POS.
- En Windows, el servidor guarda el máximo horario observado en `license-clock.dpapi`, protegido con DPAPI para el equipo y persistido cada cinco minutos. Un retroceso superior a cinco minutos respecto de la marca guardada o un marcador ilegible impide validar/renovar la licencia. Esto no resiste la restauración de un respaldo antiguo ni sustituye un reloj seguro; en otros sistemas operativos no se aplica esta protección.
- Administración > Licencia consulta el estado y permite importar el `.poslic`; el servidor vuelve a verificar la firma, el emisor y el ID de instalación antes de guardarlo.
- `GET /api/v1/license/status` y `POST /api/v1/license/import` requieren sesión de administrador. El endpoint de importación acepta JSON con `documentBase64`.

## Estado actual — 16 de octubre de 2026

- Implementado el contrato versionado, la verificación RSA-PSS/SHA-256 con llaves públicas, la identidad estable de instalación, la persistencia atómica y la gracia fija de dos días.
- `Pos.Server` expone estado e importación para administradores; la pestaña Licencia de Administración permite consultar/importar el `.poslic` desde Windows.
- Si se activa `Licensing:Enforce`, el servidor bloquea operaciones comerciales nuevas al vencer la gracia; deja leer, cerrar caja, hacer devoluciones, cobrar cuentas, importar una renovación, desparear terminales y terminar un carrito activo que se guardó antes del bloqueo. Las ventas reintentadas con la misma llave de idempotencia solo pueden reproducir el resultado anterior.
- El emparejamiento respeta el cupo firmado y el desparejamiento desactiva la terminal y revoca sus sesiones. Los módulos solo reciben sus entitlements mientras la licencia esté activa o en gracia.
- El servicio remoto y la consulta automática de 32C están implementados. Aún faltan el despliegue real en el VPS, el dominio HTTPS, instalar el anillo público en el Windows Server, registrar la suscripción y habilitar el endpoint de renovación. El registro del pago aún es manual; no hay pasarela de pago/webhook.
- El marcador horario DPAPI de Windows detecta retrocesos mayores de cinco minutos; no resiste restauraciones antiguas del equipo o sus respaldos.
- El desbloqueo de emergencia 32D aún está pendiente.
- La llave pública real del emisor todavía debe provisionarse fuera del código; no se creó ni se almacenó ninguna llave privada en el repositorio.
