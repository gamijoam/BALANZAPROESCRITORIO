#!/usr/bin/env bash
set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Ejecuta este instalador con sudo."
  exit 1
fi

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
admin_tool="${source_dir}/pos-license-admin.py"
token_file="/etc/pos-license-api/admin-token"

if ! command -v python3 >/dev/null 2>&1; then
  echo "No se encontró Python 3. Instálalo con: apt-get update && apt-get install -y python3"
  exit 1
fi
if [[ ! -s "${token_file}" ]]; then
  echo "No se encontró el token administrativo de licencias en ${token_file}."
  exit 1
fi
if [[ ! -f "${admin_tool}" ]]; then
  echo "No se encontró ${admin_tool}. Actualiza primero el repositorio."
  exit 1
fi

install -o root -g root -m 0700 "${admin_tool}" /usr/local/sbin/pos-license-admin
echo "Herramienta instalada en /usr/local/sbin/pos-license-admin"
echo "Iníciala con: sudo /usr/local/sbin/pos-license-admin"
