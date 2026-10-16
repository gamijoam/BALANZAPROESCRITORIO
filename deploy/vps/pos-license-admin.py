#!/usr/bin/env python3
"""Interactive VPS administration for BalanzaPro POS subscriptions."""

import datetime as dt
import json
import os
import re
import stat
import ssl
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path


TOKEN_PATH = Path("/etc/pos-license-api/admin-token")
EXPORT_DIR = Path("/var/lib/pos-license-api/exports")
AUDIT_PATH = Path("/var/lib/pos-license-api/audit.jsonl")
DEFAULT_API = "https://licencias.balanzapro.com"
TIMEOUT_SECONDS = 20


class AdminError(Exception):
    pass


def load_config():
    if os.geteuid() != 0:
        raise AdminError("Ejecuta esta herramienta con sudo para leer el token administrativo protegido.")
    try:
        mode = stat.S_IMODE(TOKEN_PATH.stat().st_mode)
        if mode & 0o027:
            raise AdminError(f"El archivo {TOKEN_PATH} no debe dar escritura ni acceso a otros usuarios.")
        token = TOKEN_PATH.read_text(encoding="utf-8").strip()
    except OSError as exc:
        raise AdminError(f"No se pudo leer el token administrativo ({TOKEN_PATH}): {exc}") from exc
    if len(token) < 32:
        raise AdminError("El token administrativo está vacío o no tiene la longitud mínima.")

    base = os.environ.get("POS_LICENSE_API_BASE", DEFAULT_API).rstrip("/")
    if not base.startswith("https://") or any(char in base for char in "\r\n"):
        raise AdminError("POS_LICENSE_API_BASE debe ser una dirección HTTPS válida.")
    return base, token


def api(method, path, body=None):
    base, token = load_config()
    data = None if body is None else json.dumps(body, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        base + path,
        data=data,
        method=method,
        headers={"Authorization": "Bearer " + token, "Accept": "application/json", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT_SECONDS, context=ssl.create_default_context()) as response:
            payload = response.read(2 * 1024 * 1024 + 1)
            if len(payload) > 2 * 1024 * 1024:
                raise AdminError("La respuesta del servicio supera el tamaño permitido.")
            return None if not payload else json.loads(payload.decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = ""
        try:
            payload = json.loads(exc.read(64 * 1024).decode("utf-8"))
            detail = payload.get("error") or payload.get("detail") or ""
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            pass
        if exc.code == 401:
            raise AdminError("El VPS rechazó el token administrativo. Revisa su configuración sin mostrarlo.") from exc
        raise AdminError(f"El servicio respondió HTTP {exc.code}" + (f": {detail}" if detail else ".")) from exc
    except (urllib.error.URLError, TimeoutError, ssl.SSLError) as exc:
        raise AdminError(f"No se pudo conectar con el servicio de licencias: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise AdminError("El servicio devolvió una respuesta que no es JSON válido.") from exc


def ask(label, default=None):
    suffix = f" [{default}]" if default is not None else ""
    value = input(f"{label}{suffix}: ").strip()
    return value if value else ("" if default is None else str(default))


def ask_required(label, maximum=None):
    while True:
        value = ask(label)
        if not value:
            print("Este dato es obligatorio.")
        elif maximum and len(value) > maximum:
            print(f"Máximo {maximum} caracteres.")
        else:
            return value


def ask_int(label, default, minimum, maximum):
    while True:
        value = ask(label, default)
        try:
            parsed = int(value)
        except ValueError:
            parsed = minimum - 1
        if minimum <= parsed <= maximum:
            return parsed
        print(f"Ingresa un número entre {minimum} y {maximum}.")


def ask_amount(label):
    while True:
        value = ask_required(label).replace(",", ".")
        try:
            amount = float(value)
            if 0 < amount <= 1_000_000_000:
                return amount
        except ValueError:
            pass
        print("Ingresa un monto mayor que cero, con punto o coma decimal.")


def ask_expiry(label="Vigente hasta (AAAA-MM-DD)"):
    while True:
        value = ask_required(label)
        try:
            date = dt.date.fromisoformat(value)
            if date <= dt.datetime.now(dt.timezone.utc).date():
                print("La fecha debe ser futura.")
                continue
            return date.isoformat() + "T23:59:59Z"
        except ValueError:
            print("Usa el formato AAAA-MM-DD, por ejemplo 2026-11-30.")


def ask_entitlements(default=None):
    current = ",".join(default or [])
    value = ask("Módulos autorizados, separados por coma (vacío = ninguno)", current)
    result = sorted({item.strip() for item in value.split(",") if item.strip()})
    invalid = [item for item in result if not re.fullmatch(r"[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*", item)]
    if invalid:
        raise AdminError("Módulo(s) con formato inválido: " + ", ".join(invalid))
    return result


def get_subscriptions():
    result = api("GET", "/api/v1/admin/licenses")
    if not isinstance(result, list):
        raise AdminError("El VPS devolvió una lista de suscripciones inválida.")
    return result


def print_subscriptions(records):
    if not records:
        print("Todavía no hay suscripciones registradas.")
        return
    print("\nSuscripciones POS")
    print("-" * 112)
    for i, item in enumerate(records, 1):
        active = "ACTIVA" if item.get("active") else "SUSPENDIDA"
        last_seen = item.get("lastSeenAtUtc") or "nunca sincronizada"
        print(f"{i:>2}. {item.get('customerName', '—')} · {item.get('customerRif', '—')} · {item.get('plan', '—')} · {active}")
        print(f"    ID: {item.get('licenseId')} · Instalación: {item.get('installationId')}")
        print(f"    Vence: {item.get('paidThroughUtc')} · Terminales: {item.get('maxTerminals')} · Última conexión: {last_seen}")
        if item.get("suspensionReason"):
            print(f"    Motivo: {item['suspensionReason']}")
    print("-" * 112)


def select_subscription(records):
    if not records:
        print("No hay suscripciones para seleccionar.")
        return None
    print_subscriptions(records)
    value = ask_required("Escribe el ID de licencia o el número de la lista")
    if value.isdigit() and 1 <= int(value) <= len(records):
        return records[int(value) - 1]
    return next((item for item in records if item.get("licenseId") == value), None)


def save_license_document(installation_id, response=None):
    if response is None:
        response = api("GET", f"/api/v1/admin/installations/{installation_id}/license-document")
    license_id = response.get("licenseId", "")
    document_base64 = response.get("documentBase64", "")
    if not re.fullmatch(r"lic_[a-z0-9]+", license_id) or not document_base64:
        raise AdminError("El servicio devolvió datos de archivo de licencia incompletos.")
    import base64

    try:
        document = base64.b64decode(document_base64, validate=True)
    except (ValueError, TypeError) as exc:
        raise AdminError("El archivo de licencia recibido no está codificado correctamente.") from exc
    if not document or len(document) > 64 * 1024:
        raise AdminError("El archivo de licencia está vacío o supera 64 KiB.")
    EXPORT_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(EXPORT_DIR, 0o700)
    path = EXPORT_DIR / f"{license_id}.poslic"
    temp_path = path.with_suffix(".poslic.tmp")
    fd = os.open(temp_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(document)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temp_path, path)
        os.chmod(path, 0o600)
    finally:
        if temp_path.exists():
            temp_path.unlink()
    print(f"Archivo generado: {path}")
    print("En Windows, puedes copiarlo con SCP (reemplaza TU_VPS por la IP o dominio SSH):")
    print(f'  scp root@TU_VPS:{path} "$env:USERPROFILE\\Downloads\\"')
    return path


def create_subscription():
    print("\nNueva suscripción. El ID de instalación se copia desde Administración > Licencia en el POS.")
    installation_id = ask_required("ID de instalación")
    try:
        installation_id = str(uuid.UUID(installation_id))
    except ValueError as exc:
        raise AdminError("El ID de instalación no tiene formato GUID.") from exc
    payload = {
        "installationId": installation_id,
        "customerRif": ask_required("RIF o identificación", 32),
        "customerName": ask_required("Nombre del comercio", 200),
        "plan": ask_required("Plan (por ejemplo mensual, anual)", 64),
        "maxTerminals": ask_int("Terminales permitidas", 1, 1, 1000),
        "entitlements": ask_entitlements(),
        "paidThroughUtc": ask_expiry(),
        "initialPaymentAmount": ask_amount("Monto del pago inicial"),
        "initialPaymentCurrency": ask_required("Moneda del pago (USD, VES, USDT, etc.)", 8).upper(),
        "initialPaymentReference": ask_required("Referencia o número de comprobante inicial", 128),
    }
    result = api("POST", "/api/v1/admin/licenses", payload)
    path = save_license_document(installation_id, result)
    print(f"Suscripción creada: {result['licenseId']} · vence {result['expiresAtUtc']}")
    print(f"Este primer archivo se importa una vez en Administración > Licencia. Después, las renovaciones son automáticas: {path.name}")


def record_payment():
    item = select_subscription(get_subscriptions())
    if item is None:
        return
    amount_text = ask_required("Monto recibido")
    try:
        amount = float(amount_text.replace(",", "."))
        if amount <= 0:
            raise ValueError()
    except ValueError as exc:
        raise AdminError("El monto debe ser un número mayor que cero.") from exc
    payload = {
        "amount": amount,
        "currency": ask_required("Moneda (USD, VES, USDT, etc.)", 8).upper(),
        "reference": ask_required("Referencia o número de comprobante", 128),
        "paidThroughUtc": ask_expiry("Nueva fecha de vencimiento (AAAA-MM-DD)"),
    }
    result = api("POST", f"/api/v1/admin/licenses/{item['licenseId']}/payments", payload)
    print(f"Pago registrado; la suscripción vence el {result['payment']['paidThroughUtc']}.")
    if not result.get("active"):
        print("La suscripción continúa suspendida. Reactívala por separado cuando corresponda.")
    else:
        print("El POS se renueva automáticamente en su próxima conexión al VPS (intenta cada 4 horas). No hace falta importar un archivo cada mes.")
    if ask("¿Generar también un .poslic para importación manual? [s/N]").lower() in ("s", "si", "sí"):
        save_license_document(item["installationId"])


def suspend_subscription():
    item = select_subscription(get_subscriptions())
    if item is None:
        return
    reason = ask_required("Motivo breve de suspensión", 512)
    confirm = ask(f"Escribe SUSPENDER para suspender {item['customerName']}")
    if confirm != "SUSPENDER":
        print("Operación cancelada.")
        return
    result = api("POST", f"/api/v1/admin/licenses/{item['licenseId']}/suspend", {"reason": reason})
    print(f"Suspensión registrada (versión de control {result['controlVersion']}).")
    print("Se aplicará cuando el POS vuelva a conectarse al VPS; una PC sin Internet no puede recibir el aviso hasta reconectarse.")


def reactivate_subscription():
    item = select_subscription(get_subscriptions())
    if item is None:
        return
    expiry_text = item.get("paidThroughUtc", "")
    try:
        expiry = dt.datetime.fromisoformat(expiry_text.replace("Z", "+00:00"))
    except ValueError:
        expiry = dt.datetime.min.replace(tzinfo=dt.timezone.utc)
    if expiry <= dt.datetime.now(dt.timezone.utc):
        raise AdminError("La fecha pagada ya venció. Registra primero el pago y la nueva fecha, y luego reactiva la licencia.")
    confirm = ask(f"Escribe REACTIVAR para reactivar {item['customerName']}")
    if confirm != "REACTIVAR":
        print("Operación cancelada.")
        return
    result = api("POST", f"/api/v1/admin/licenses/{item['licenseId']}/enable")
    print(f"Suscripción reactivada (versión de control {result['controlVersion']}).")
    print("Al reconectarse, el POS descargará la licencia vigente y eliminará la suspensión local.")


def update_terms():
    item = select_subscription(get_subscriptions())
    if item is None:
        return
    print("Los cambios se aplicarán en la próxima sincronización del POS.")
    payload = {
        "plan": ask("Plan", item.get("plan", "")),
        "maxTerminals": ask_int("Terminales permitidas", item.get("maxTerminals", 1), 1, 1000),
        "entitlements": ask_entitlements(item.get("entitlements", [])),
    }
    result = api("PUT", f"/api/v1/admin/licenses/{item['licenseId']}/terms", payload)
    print(f"Plan actualizado: {result['plan']} · terminales: {result['maxTerminals']}.")


def export_license():
    item = select_subscription(get_subscriptions())
    if item is None:
        return
    save_license_document(item["installationId"])


def issue_offline_recovery():
    request_path = Path(ask_required("Ruta del archivo .posreq copiado desde la PC POS"))
    try:
        if request_path.stat().st_size not in range(1, 16 * 1024 + 1):
            raise AdminError("El archivo .posreq está vacío o supera 16 KiB.")
        recovery_request = json.loads(request_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AdminError(f"No se pudo leer el archivo .posreq: {exc}") from exc
    if not isinstance(recovery_request, dict) or not recovery_request.get("requestId"):
        raise AdminError("El archivo no contiene una solicitud de recuperación reconocible.")
    duration = ask_int("Duración del desbloqueo en horas (máximo 48)", 24, 1, 48)
    reason = ask_required("Motivo del desbloqueo de emergencia", 512)
    if ask("Escribe AUTORIZAR para firmar un desbloqueo temporal").strip() != "AUTORIZAR":
        print("Operación cancelada.")
        return
    result = api("POST", "/api/v1/admin/recoveries", {
        "request": recovery_request,
        "durationHours": duration,
        "reason": reason,
    })
    import base64

    try:
        document = base64.b64decode(result.get("documentBase64", ""), validate=True)
        request_id = str(uuid.UUID(result["requestId"]))
    except (KeyError, ValueError, TypeError) as exc:
        raise AdminError("El VPS devolvió un desbloqueo con formato inválido.") from exc
    if not document or len(document) > 32 * 1024:
        raise AdminError("El archivo .posunlock está vacío o supera 32 KiB.")
    EXPORT_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(EXPORT_DIR, 0o700)
    path = EXPORT_DIR / f"{request_id}.posunlock"
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as output:
        output.write(document)
        output.flush()
        os.fsync(output.fileno())
    os.chmod(path, 0o600)
    print(f"Desbloqueo temporal firmado hasta {result['expiresAtUtc']}: {path}")
    if result.get("alreadyIssued"):
        print("Esta solicitud ya se había emitido; se devolvió el mismo archivo, sin extender su duración.")
    print("Cópialo a la PC POS y usa Administración > Licencia > Importar .posunlock.")
    print("En Windows, puedes copiarlo con SCP (reemplaza TU_VPS por la IP o dominio SSH):")
    print(f'  scp root@TU_VPS:{path} "$env:USERPROFILE\\Downloads\\"')


def show_audit():
    if not AUDIT_PATH.exists():
        print("Todavía no hay eventos de auditoría.")
        return
    with AUDIT_PATH.open("rb") as source:
        source.seek(max(0, AUDIT_PATH.stat().st_size - 256 * 1024))
        lines = source.read().splitlines()[-30:]
    print("\nÚltimos eventos administrativos")
    for line in lines:
        try:
            event = json.loads(line.decode("utf-8"))
            print(f"{event.get('occurredAtUtc')} · {event.get('action')} · {event.get('licenseId')} · {event.get('details', {})}")
        except (UnicodeDecodeError, json.JSONDecodeError):
            print("[línea de auditoría dañada; se conserva el archivo original]")


def main():
    print("BalanzaPro · administración de licencias (VPS)")
    load_config()
    actions = {
        "1": ("Consultar suscripciones", lambda: print_subscriptions(get_subscriptions())),
        "2": ("Crear suscripción y generar primer .poslic", create_subscription),
        "3": ("Registrar pago / extender vencimiento", record_payment),
        "4": ("Cambiar plan, módulos o terminales", update_terms),
        "5": ("Suspender licencia", suspend_subscription),
        "6": ("Reactivar licencia", reactivate_subscription),
        "7": ("Generar / descargar .poslic", export_license),
        "8": ("Autorizar desbloqueo offline desde .posreq", issue_offline_recovery),
        "9": ("Ver auditoría reciente", show_audit),
        "0": ("Salir", None),
    }
    while True:
        print("\n" + "\n".join(f"{key}. {label}" for key, (label, _) in actions.items()))
        choice = ask("Selecciona una opción")
        if choice == "0":
            print("Listo.")
            return
        action = actions.get(choice)
        if action is None:
            print("Opción no válida.")
            continue
        try:
            action[1]()
        except (AdminError, OSError, KeyError, TypeError) as exc:
            print(f"\nNo se completó la operación: {exc}")
        except KeyboardInterrupt:
            print("\nOperación cancelada.")


if __name__ == "__main__":
    try:
        main()
    except AdminError as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        print("\nCancelado.")
