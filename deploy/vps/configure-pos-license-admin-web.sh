#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Ejecuta este script con sudo."
  exit 1
fi

config_dir="/etc/pos-license-api"
config_file="$config_dir/admin-web.json"
install -d -o root -g 1654 -m 0750 "$config_dir"
if [[ ! -e "$config_file" ]]; then
  printf '{"enabled":false}\n' > "$config_file"
fi

python3 - "$config_file" "${1:-}" <<'PY'
import base64
import datetime as dt
import getpass
import hashlib
import json
import os
import re
import secrets
import shutil
import sys
from pathlib import Path

config_path = Path(sys.argv[1])
reset = len(sys.argv) > 2 and sys.argv[2] == "--reset"
if len(sys.argv) > 2 and sys.argv[2] not in ("", "--reset"):
    raise SystemExit("Uso: configure-pos-license-admin-web.sh [--reset]")
try:
    current = json.loads(config_path.read_text(encoding="utf-8"))
except (OSError, json.JSONDecodeError) as exc:
    raise SystemExit(f"No se pudo leer la configuración actual: {exc}")

def terminal_input(prompt):
    try:
        with open("/dev/tty", "r+", encoding="utf-8") as terminal:
            terminal.write(prompt)
            terminal.flush()
            value = terminal.readline()
    except OSError as exc:
        raise SystemExit(f"No se pudo leer la respuesta desde la terminal interactiva: {exc}")
    if value == "":
        raise SystemExit("No se recibió una respuesta desde la terminal.")
    return value.rstrip("\r\n")

if current.get("enabled"):
    if not reset:
        raise SystemExit("El panel ya está configurado. Para rotar credenciales, vuelve a ejecutar con --reset.")
    if terminal_input("Escribe RECONFIGURAR para generar nuevas credenciales: ").strip() != "RECONFIGURAR":
        raise SystemExit("No se cambió la configuración.")
    backup = config_path.with_name("admin-web.json.bak-" + dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ"))
    shutil.copy2(config_path, backup)
    os.chown(backup, 0, 0)
    os.chmod(backup, 0o600)
    print(f"Respaldo de configuración anterior: {backup}")

while True:
    username = terminal_input("Usuario administrador (3 a 64 letras, números, punto, guion o guion bajo): ").strip()
    if re.fullmatch(r"[A-Za-z0-9._-]{3,64}", username):
        break
    print("El usuario no tiene el formato permitido.")

while True:
    password = getpass.getpass("Contraseña (mínimo 14 caracteres): ")
    confirm = getpass.getpass("Repite la contraseña: ")
    if password != confirm:
        print("Las contraseñas no coinciden.")
    elif len(password) < 14:
        print("Usa una contraseña de al menos 14 caracteres.")
    elif len(password) > 1024:
        print("La contraseña supera el máximo permitido.")
    else:
        break

salt = secrets.token_bytes(16)
password_hash = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, 310000, dklen=32)
totp_secret = base64.b32encode(secrets.token_bytes(20)).decode("ascii").rstrip("=")
session_key = secrets.token_bytes(32)
document = {
    "enabled": True,
    "username": username,
    "passwordSalt": base64.b64encode(salt).decode("ascii"),
    "passwordHash": base64.b64encode(password_hash).decode("ascii"),
    "totpSecret": totp_secret,
    "sessionHmacKey": base64.b64encode(session_key).decode("ascii"),
}
temporary_path = config_path.with_suffix(".json.tmp")
with temporary_path.open("w", encoding="utf-8") as output:
    json.dump(document, output, separators=(",", ":"))
    output.write("\n")
    output.flush()
    os.fsync(output.fileno())
temporary_path.replace(config_path)
os.chmod(config_path, 0o440)
print("\nPanel configurado. Guarda esta semilla TOTP en tu autenticador; se muestra una sola vez:")
print(totp_secret)
print("Usuario:", username)
print("Añade la cuenta manualmente con algoritmo SHA1, 6 dígitos y periodo de 30 segundos.")
print("No compartas la semilla, el archivo admin-web.json ni la contraseña.")
PY

chown root:1654 "$config_file"
chmod 0440 "$config_file"
echo "Configuración protegida en $config_file. Reconstruye y reinicia la API para habilitar /admin."
