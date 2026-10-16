#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Ejecuta este script con sudo." >&2
  exit 1
fi

config_dir="/etc/pos-license-api"
data_dir="/var/lib/pos-license-api"
private_key="$config_dir/license-private.pem"
admin_token="$config_dir/admin-token"
admin_web_config="$config_dir/admin-web.json"

install -d -o root -g 1654 -m 0750 "$config_dir"
install -d -o 1654 -g 1654 -m 0750 "$data_dir"

if [[ ! -s "$private_key" ]]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$private_key" 2>/dev/null
fi
chown root:1654 "$private_key"
chmod 0440 "$private_key"
openssl pkey -in "$private_key" -check -noout >/dev/null

if [[ ! -s "$admin_token" ]]; then
  openssl rand -hex 32 > "$admin_token"
fi
chown root:1654 "$admin_token"
chmod 0440 "$admin_token"

if [[ ! -e "$admin_web_config" ]]; then
  printf '{"enabled":false}\n' > "$admin_web_config"
fi
chown root:1654 "$admin_web_config"
chmod 0440 "$admin_web_config"

token="$(<"$admin_token")"
if [[ ! "$token" =~ ^[[:xdigit:]]{64}$ ]]; then
  echo "El token administrativo debe contener exactamente 64 caracteres hexadecimales." >&2
  exit 1
fi

chown -R 1654:1654 "$data_dir"
printf 'header = "Authorization: Bearer %s"\n' "$token" > "$config_dir/admin.curlrc"
chown root:root "$config_dir/admin.curlrc"
chmod 0600 "$config_dir/admin.curlrc"
unset token

echo "Archivos de licencia preparados. La llave privada y el token no se mostraron."
