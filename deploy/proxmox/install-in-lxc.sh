#!/usr/bin/env bash
# Installs or updates the MediaMTX Panel inside an LXC container (or machine) running
# Debian 13 (Debian 12 works too).
# Normally started by create-lxc.sh, which copies the sources to /tmp/mediamtx-panel-install/source.tar.gz.
#
# MODE=install  first installation: packages, users, configuration, services
# MODE=update   rebuild and restart the panel, keep configuration and database
set -euo pipefail

MODE="${MODE:-install}"
INSTALL_MEDIAMTX="${INSTALL_MEDIAMTX:-yes}"
MEDIAMTX_VERSION="${MEDIAMTX_VERSION:-1.21.2}"
PANEL_PORT="${PANEL_PORT:-8080}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@localhost}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-}"
PANEL_HOST="${PANEL_HOST:-$(hostname -I | awk '{print $1}')}"
PUBLIC_HOST="${PUBLIC_HOST:-$PANEL_HOST}"

WORK=/tmp/mediamtx-panel-install
SOURCE_TARBALL="${SOURCE_TARBALL:-$WORK/source.tar.gz}"
APP_DIR=/opt/mediamtx-panel
DATA_DIR=/var/lib/mediamtx-panel
CONF_DIR=/etc/mediamtx-panel
MTX_DIR=/opt/mediamtx
MTX_CONF_DIR=/etc/mediamtx
MTX_REC_DIR=/var/lib/mediamtx/recordings

log() { echo -e "\e[1;34m[lxc]\e[0m $*"; }
# '|| true': head closing the pipe makes tr exit with SIGPIPE, which pipefail would turn into a failure.
random_secret() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c "${1:-24}" || true; }

[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
[[ -f "$SOURCE_TARBALL" ]] || { echo "Sources not found at $SOURCE_TARBALL." >&2; exit 1; }
export DEBIAN_FRONTEND=noninteractive
# Debian templates only ship the C.UTF-8 locale. XDG_RUNTIME_DIR from another system (e.g. /run/user/0
# passed on by pct exec) makes the .NET SDK fail to create its NuGet mutex.
export LANG=C.UTF-8 LC_ALL=C.UTF-8
unset LANGUAGE XDG_RUNTIME_DIR

# ---------------------------------------------------------------- packages
install_packages() {
  # shellcheck source=/dev/null
  . /etc/os-release
  apt-get update -qq
  apt-get install -y -qq curl ca-certificates tar sqlite3 iproute2 locales >/dev/null
  # English (en_US.UTF-8) as the container's default language, so logins from a host shell with that LANG work.
  if ! locale -a 2>/dev/null | grep -qi '^en_US\.utf-\?8$'; then
    sed -i 's/^# *\(en_US.UTF-8 UTF-8\)/\1/' /etc/locale.gen
    locale-gen >/dev/null
  fi
  update-locale LANG=en_US.UTF-8
  if [[ "$ID" != "debian" ]]; then
    echo "Unsupported OS '$ID': this installer is made for Debian 13." >&2
    exit 1
  fi
  # Debian does not ship .NET; use Microsoft's package feed for this Debian release.
  log "Installing packages (.NET 10 from packages.microsoft.com, Debian ${VERSION_ID})…"
  if ! dpkg -s packages-microsoft-prod >/dev/null 2>&1; then
    curl -fsSL "https://packages.microsoft.com/config/debian/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb >/dev/null
    rm -f /tmp/packages-microsoft-prod.deb
  fi
  apt-get update -qq
  apt-get install -y -qq dotnet-sdk-10.0 aspnetcore-runtime-10.0 >/dev/null
}

# ---------------------------------------------------------------- build
build_panel() {
  log "Building the panel…"
  rm -rf "$WORK/src"
  mkdir -p "$WORK/src"
  tar -xzf "$SOURCE_TARBALL" -C "$WORK/src"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  local revision=()
  [[ -n "${SOURCE_COMMIT:-}" ]] && revision=("-p:SourceRevisionId=${SOURCE_COMMIT}")
  dotnet publish "$WORK/src/src/MediaMtxPanel/MediaMtxPanel.csproj" -c Release -o "$WORK/publish" -v quiet -nologo "${revision[@]}"
  mkdir -p "$APP_DIR"
  # Replace the application files but keep nothing else in APP_DIR.
  find "$APP_DIR" -mindepth 1 -delete
  cp -a "$WORK/publish/." "$APP_DIR/"
  chown -R root:root "$APP_DIR"
}

# ---------------------------------------------------------------- MediaMTX
install_mediamtx() {
  local arch
  case "$(uname -m)" in
    x86_64) arch=amd64 ;;
    aarch64) arch=arm64 ;;
    armv7l) arch=armv7 ;;
    *) echo "Unsupported architecture $(uname -m)" >&2; exit 1 ;;
  esac
  log "Installing MediaMTX v${MEDIAMTX_VERSION} (${arch})…"
  local url="https://github.com/bluenviron/mediamtx/releases/download/v${MEDIAMTX_VERSION}/mediamtx_v${MEDIAMTX_VERSION}_linux_${arch}.tar.gz"
  mkdir -p "$MTX_DIR" "$MTX_CONF_DIR" "$MTX_REC_DIR"
  curl -fsSL "$url" | tar -xz -C "$MTX_DIR" mediamtx
  chmod 0755 "$MTX_DIR/mediamtx"
  id -u mediamtx >/dev/null 2>&1 || useradd --system --home /var/lib/mediamtx --shell /usr/sbin/nologin mediamtx
  chown -R mediamtx:mediamtx /var/lib/mediamtx

  # Start from the project's mediamtx.yml and point it at the local panel.
  sed -e "s#^authHTTPAddress:.*#authHTTPAddress: http://127.0.0.1:${PANEL_PORT}/api/mediamtx/auth?secret=${HOOK_SECRET}#" \
      -e "s#^webrtcAdditionalHosts:.*#webrtcAdditionalHosts: [${PUBLIC_HOST}]#" \
      -e "s#recordPath: .*#recordPath: ${MTX_REC_DIR}/%path/%Y-%m-%d_%H-%M-%S-%f#" \
      -e "s#^apiAddress:.*#apiAddress: 127.0.0.1:9997#" \
      "$WORK/src/deploy/mediamtx.yml" > "$MTX_CONF_DIR/mediamtx.yml"
  chmod 0640 "$MTX_CONF_DIR/mediamtx.yml"
  chown root:mediamtx "$MTX_CONF_DIR/mediamtx.yml"

  cat > /etc/systemd/system/mediamtx.service <<UNIT
[Unit]
Description=MediaMTX media server
After=network-online.target mediamtx-panel.service
Wants=network-online.target

[Service]
User=mediamtx
Group=mediamtx
WorkingDirectory=/var/lib/mediamtx
ExecStart=${MTX_DIR}/mediamtx ${MTX_CONF_DIR}/mediamtx.yml
Restart=always
RestartSec=3
AmbientCapabilities=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
UNIT
}

# ---------------------------------------------------------------- panel config + service
configure_panel() {
  id -u mediamtx-panel >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin mediamtx-panel
  mkdir -p "$DATA_DIR/keys" "$CONF_DIR"
  chown -R mediamtx-panel:mediamtx-panel "$DATA_DIR"
  chmod 0750 "$DATA_DIR"

  local api_password
  api_password="$(random_secret 32)"
  {
    echo "# MediaMTX Panel configuration. Restart after changes: systemctl restart mediamtx-panel"
    echo "ASPNETCORE_URLS=http://0.0.0.0:${PANEL_PORT}"
    echo "ConnectionStrings__DefaultConnection=DataSource=${DATA_DIR}/panel.db;Cache=Shared"
    echo "Panel__DataProtectionKeysPath=${DATA_DIR}/keys"
    echo "Panel__AdminEmail=${ADMIN_EMAIL}"
    echo "Panel__AdminPassword=${ADMIN_PASSWORD}"
    echo "# Address at which (remote) MediaMTX servers reach this panel; used in the config shown per server."
    echo "Panel__HookBaseUrl=http://${PANEL_HOST}:${PANEL_PORT}"
    if [[ "$INSTALL_MEDIAMTX" == "yes" ]]; then
      echo "# First server (the MediaMTX in this container). Only used when the panel has no servers yet."
      echo "MediaMtx__ServerName=Local"
      echo "MediaMtx__ApiUrl=http://127.0.0.1:9997"
      echo "MediaMtx__ApiUser=panel"
      echo "MediaMtx__ApiPassword=${api_password}"
      echo "MediaMtx__AuthHookSecret=${HOOK_SECRET}"
      echo "MediaMtx__PublicHost=${PUBLIC_HOST}"
    fi
  } > "$CONF_DIR/panel.env"
  chmod 0640 "$CONF_DIR/panel.env"
  chown root:mediamtx-panel "$CONF_DIR/panel.env"

  cat > /etc/systemd/system/mediamtx-panel.service <<UNIT
[Unit]
Description=MediaMTX Panel
After=network-online.target
Wants=network-online.target

[Service]
User=mediamtx-panel
Group=mediamtx-panel
WorkingDirectory=${APP_DIR}
EnvironmentFile=${CONF_DIR}/panel.env
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=/usr/bin/dotnet ${APP_DIR}/MediaMtxPanel.dll
Restart=always
RestartSec=5
NoNewPrivileges=true
ProtectSystem=full
ReadWritePaths=${DATA_DIR}

[Install]
WantedBy=multi-user.target
UNIT
}

wait_for_panel() {
  for _ in $(seq 1 60); do
    curl -fs -o /dev/null "http://127.0.0.1:${PANEL_PORT}/Account/Login" && return 0
    sleep 2
  done
  echo "The panel did not start; see: journalctl -u mediamtx-panel" >&2
  exit 1
}

# ---------------------------------------------------------------- main
case "$MODE" in
  install)
    [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$(random_secret 16)Aa1"
    HOOK_SECRET="$(random_secret 32)"
    install_packages
    build_panel
    configure_panel
    [[ "$INSTALL_MEDIAMTX" == "yes" ]] && install_mediamtx
    systemctl daemon-reload
    systemctl enable --now mediamtx-panel >/dev/null
    wait_for_panel
    # The administrator now exists; the one-time password does not need to stay on disk.
    sed -i '/^Panel__AdminPassword=/d' "$CONF_DIR/panel.env"
    if [[ "$INSTALL_MEDIAMTX" == "yes" ]]; then
      systemctl enable --now mediamtx >/dev/null
    fi
    # The SDK is only needed to build; keep the container small. 'update' installs it again.
    apt-get purge -y -qq dotnet-sdk-10.0 >/dev/null || true
    apt-get autoremove -y -qq >/dev/null || true
    ;;
  update)
    install_packages
    build_panel
    systemctl restart mediamtx-panel
    wait_for_panel
    apt-get purge -y -qq dotnet-sdk-10.0 >/dev/null || true
    apt-get autoremove -y -qq >/dev/null || true
    ;;
  *)
    echo "Unknown MODE '$MODE' (use install or update)." >&2
    exit 1
    ;;
esac

rm -rf "$WORK"
log "Done."
