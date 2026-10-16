#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Ejecuta este script con sudo desde la raíz del repositorio." >&2
  exit 1
fi

repo_root="$(git rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$repo_root" || ! -f "$repo_root/src/Pos.Licensing.Vps/Pos.Licensing.Vps.csproj" ]]; then
  echo "No se encontró src/Pos.Licensing.Vps/Pos.Licensing.Vps.csproj. Ejecuta el script desde el repositorio actualizado." >&2
  exit 1
fi

if [[ "$(. /etc/os-release && echo "$ID:$VERSION_ID")" != "ubuntu:24.04" ]]; then
  echo "Este instalador está preparado para Ubuntu 24.04." >&2
  exit 1
fi

case "$(uname -m)" in
  x86_64) runtime_id="linux-x64" ;;
  aarch64|arm64) runtime_id="linux-arm64" ;;
  *) echo "Arquitectura no compatible: $(uname -m)" >&2; exit 1 ;;
esac

apt-get update
apt-get install -y ca-certificates curl openssl

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | awk '{print $1}' | grep -Eq '^10\.'; then
  apt-get install -y wget
  if ! apt-cache show dotnet-sdk-10.0 >/dev/null 2>&1; then
    package_config="/tmp/packages-microsoft-prod.deb"
    wget --https-only --secure-protocol=TLSv1_2 -O "$package_config" \
      https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb
    dpkg -i "$package_config"
    rm -f "$package_config"
    apt-get update
  fi
  apt-get install -y dotnet-sdk-10.0
fi

if ! id pos-license-api >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/pos-license-api --create-home --shell /usr/sbin/nologin pos-license-api
fi

install -d -o root -g pos-license-api -m 0750 /etc/pos-license-api
install -d -o pos-license-api -g pos-license-api -m 0750 /var/lib/pos-license-api
install -d -o root -g root -m 0755 /opt/pos-license-api/releases

private_key="/etc/pos-license-api/license-private.pem"
if [[ ! -f "$private_key" ]]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$private_key"
  chown root:pos-license-api "$private_key"
  chmod 0640 "$private_key"
fi
chown root:pos-license-api "$private_key"
chmod 0640 "$private_key"

environment_file="/etc/pos-license-api/pos-license-api.env"
if [[ ! -f "$environment_file" ]]; then
  admin_token="$(openssl rand -hex 32)"
  umask 027
  cat > "$environment_file" <<EOF
ASPNETCORE_URLS=http://127.0.0.1:5180
LicensingVps__KeyId=pos-publisher-vps-2026
LicensingVps__PrivateKeyPath=/etc/pos-license-api/license-private.pem
LicensingVps__StorePath=/var/lib/pos-license-api/subscriptions.json
LicensingVps__AdminToken=$admin_token
EOF
  chown root:pos-license-api "$environment_file"
  chmod 0640 "$environment_file"
  unset admin_token
fi
chown root:pos-license-api "$environment_file"
chmod 0640 "$environment_file"

admin_token="$(awk -F= '$1 == "LicensingVps__AdminToken" { print substr($0, index($0, "=") + 1); exit }' "$environment_file")"
if [[ -z "$admin_token" || "${#admin_token}" -lt 32 ]]; then
  echo "El archivo de entorno no contiene un token administrativo válido." >&2
  exit 1
fi
umask 077
printf 'header = "Authorization: Bearer %s"\n' "$admin_token" > /etc/pos-license-api/admin.curlrc
chown root:root /etc/pos-license-api/admin.curlrc
chmod 0600 /etc/pos-license-api/admin.curlrc
unset admin_token

publish_dir="$(mktemp -d /tmp/pos-license-api-publish.XXXXXX)"
release_id="$(date -u +%Y%m%d%H%M%S)-$$"
release_dir="/opt/pos-license-api/releases/$release_id"
trap 'rm -rf "$publish_dir"' EXIT

dotnet publish "$repo_root/src/Pos.Licensing.Vps/Pos.Licensing.Vps.csproj" \
  --configuration Release --runtime "$runtime_id" --self-contained true --output "$publish_dir"
install -d -o root -g root -m 0755 "$release_dir"
cp -a "$publish_dir"/. "$release_dir"/
chmod -R a+rX "$release_dir"

previous_release="$(readlink -f /opt/pos-license-api/current 2>/dev/null || true)"
ln -sfn "$release_dir" /opt/pos-license-api/current.new
mv -Tf /opt/pos-license-api/current.new /opt/pos-license-api/current
install -o root -g root -m 0644 "$repo_root/deploy/vps/pos-license-api.service" \
  /etc/systemd/system/pos-license-api.service
systemctl daemon-reload
systemctl enable --now pos-license-api.service
systemctl restart pos-license-api.service

for attempt in $(seq 1 20); do
  if curl --fail --silent http://127.0.0.1:5180/api/v1/health >/dev/null; then
    echo "POS License API listo en http://127.0.0.1:5180."
    echo "Clave pública para configurar el POS:"
    openssl pkey -in "$private_key" -pubout
    exit 0
  fi
  sleep 1
done

systemctl --no-pager --full status pos-license-api.service || true
journalctl -u pos-license-api.service -n 80 --no-pager || true
if [[ -n "$previous_release" ]]; then
  echo "Se restaura la versión anterior del API."
  ln -sfn "$previous_release" /opt/pos-license-api/current.rollback
  mv -Tf /opt/pos-license-api/current.rollback /opt/pos-license-api/current
  systemctl restart pos-license-api.service || true
fi
echo "El servicio no pasó la verificación de salud." >&2
exit 1
