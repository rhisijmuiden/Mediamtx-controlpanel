#!/usr/bin/env bash
# Installs or updates the MediaMTX Panel on Debian 13 (Debian 12 works too): in an LXC container,
# a VM or on bare metal. This is the shared core of every installer:
#   - create-lxc.sh (Proxmox host) runs it inside the new container,
#   - deploy/debian/install.sh (standalone) runs it on the machine itself,
#   - deploy/lxc-template/build-template.sh runs it in a chroot to make the Proxmox CT template.
# Those put the sources at /tmp/mediamtx-panel-install/source.tar.gz.
#
# MODE=install    first installation: packages, build, configuration, services, start
# MODE=update     rebuild and restart the panel, keep configuration and database
# MODE=image      everything of 'install' except the configuration (secrets, passwords, addresses);
#                 the machine configures itself at its first boot (mediamtx-panel-firstboot.service).
#                 Used to build the Proxmox CT template; PREBUILT_APP=<dir> skips the build there.
# MODE=configure  the configuration part of 'install', on a machine prepared by MODE=image. Run by
#                 mediamtx-panel-firstboot.service; needs no sources and no internet.
set -euo pipefail

MODE="${MODE:-install}"
INSTALL_MEDIAMTX="${INSTALL_MEDIAMTX:-yes}"
MEDIAMTX_VERSION="${MEDIAMTX_VERSION:-1.21.2}"
PANEL_PORT="${PANEL_PORT:-8080}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@localhost}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-}"
PANEL_HOST="${PANEL_HOST:-}"                   # empty = first IP of this machine
PUBLIC_HOST="${PUBLIC_HOST:-}"                 # empty = PANEL_HOST
SET_LOCALE="${SET_LOCALE:-yes}"                # make en_US.UTF-8 the system language (no = leave it alone)
LOG_TAG="${LOG_TAG:-lxc}"
PANEL_VERSION="${PANEL_VERSION:-}"             # version of the release being installed (empty = from the sources)
RELEASE_CHANNEL="${RELEASE_CHANNEL:-stable}"   # stable | test; a new installation starts on this channel
PREBUILT_APP="${PREBUILT_APP:-}"               # directory with a 'dotnet publish' of the panel: no SDK, no build
LOGIN_BANNER="${LOGIN_BANNER:-no}"             # yes = show address, login and one-time password on the console

WORK=/tmp/mediamtx-panel-install
SOURCE_TARBALL="${SOURCE_TARBALL:-$WORK/source.tar.gz}"
APP_DIR=/opt/mediamtx-panel
DATA_DIR=/var/lib/mediamtx-panel
CONF_DIR=/etc/mediamtx-panel
LIB_DIR=/usr/local/lib/mediamtx-panel
MTX_DIR=/opt/mediamtx
MTX_CONF_DIR=/etc/mediamtx
MTX_REC_DIR=/var/lib/mediamtx/recordings

log() { echo -e "\e[1;34m[${LOG_TAG}]\e[0m $*"; }
die() { echo "$*" >&2; exit 1; }
# '|| true': head closing the pipe makes tr exit with SIGPIPE, which pipefail would turn into a failure.
random_secret() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c "${1:-24}" || true; }

[[ $EUID -eq 0 ]] || die "Run as root."
if [[ "$MODE" != "configure" ]]; then
  [[ -f "$SOURCE_TARBALL" ]] || die "Sources not found at $SOURCE_TARBALL."
fi
export DEBIAN_FRONTEND=noninteractive
# Debian templates only ship the C.UTF-8 locale. XDG_RUNTIME_DIR from another system (e.g. /run/user/0
# passed on by pct exec) makes the .NET SDK fail to create its NuGet mutex.
export LANG=C.UTF-8 LC_ALL=C.UTF-8
unset LANGUAGE XDG_RUNTIME_DIR

# ---------------------------------------------------------------- packages
install_packages() {
  # shellcheck source=/dev/null
  . /etc/os-release
  [[ "${ID:-}" == "debian" ]] || die "Unsupported OS '${ID:-unknown}': this installer is made for Debian 13."
  apt-get update -qq
  apt-get install -y -qq curl ca-certificates tar sqlite3 iproute2 locales ffmpeg >/dev/null
  # English (en_US.UTF-8) as the container's default language, so logins from a host shell with that LANG work.
  if [[ "$SET_LOCALE" == "yes" ]] && ! locale -a 2>/dev/null | grep -qi '^en_US\.utf-\?8$'; then
    sed -i 's/^# *\(en_US.UTF-8 UTF-8\)/\1/' /etc/locale.gen
    locale-gen >/dev/null
  fi
  [[ "$SET_LOCALE" == "yes" ]] && update-locale LANG=en_US.UTF-8
  # Debian does not ship .NET; use Microsoft's package feed for this Debian release.
  local packages=(aspnetcore-runtime-10.0)
  [[ -n "$PREBUILT_APP" ]] || packages+=(dotnet-sdk-10.0)
  log "Installing packages (${packages[*]} from packages.microsoft.com, Debian ${VERSION_ID})…"
  if ! dpkg -s packages-microsoft-prod >/dev/null 2>&1; then
    curl -fsSL "https://packages.microsoft.com/config/debian/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb >/dev/null
    rm -f /tmp/packages-microsoft-prod.deb
  fi
  apt-get update -qq
  apt-get install -y -qq "${packages[@]}" >/dev/null
}

# ---------------------------------------------------------------- build
unpack_sources() {
  rm -rf "$WORK/src"
  mkdir -p "$WORK/src"
  tar -xzf "$SOURCE_TARBALL" -C "$WORK/src"
}

build_panel() {
  local from="$PREBUILT_APP"
  if [[ -z "$from" ]]; then
    log "Building the panel…"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
    local revision=()
    [[ -n "${SOURCE_COMMIT:-}" ]] && revision=("-p:SourceRevisionId=${SOURCE_COMMIT}")
    [[ -n "$PANEL_VERSION" ]] && revision+=("-p:Version=${PANEL_VERSION}")
    dotnet publish "$WORK/src/src/MediaMtxPanel/MediaMtxPanel.csproj" -c Release -o "$WORK/publish" -v quiet -nologo "${revision[@]}"
    from="$WORK/publish"
  fi
  [[ -f "$from/MediaMtxPanel.dll" ]] || die "No panel build found in $from."
  mkdir -p "$APP_DIR"
  # Replace the application files but keep nothing else in APP_DIR.
  find "$APP_DIR" -mindepth 1 -delete
  cp -a "$from/." "$APP_DIR/"
  chown -R root:root "$APP_DIR"
}

# Helpers this machine keeps: the station command of the Build My TV Station contract (does nothing
# until someone runs it), this script and the MediaMTX configuration for the first boot of a template,
# and the console login banner.
install_helpers() {
  install -m 0755 "$WORK/src/deploy/mediamtx-panel-station.sh" /usr/local/bin/mediamtx-panel-station
  mkdir -p "$LIB_DIR"
  local file
  for file in "$WORK/src/deploy/proxmox/install-in-lxc.sh:$LIB_DIR/install-in-lxc.sh:0755" \
              "$WORK/src/deploy/mediamtx-panel-login-banner.sh:$LIB_DIR/login-banner.sh:0755" \
              "$WORK/src/deploy/mediamtx.yml:$LIB_DIR/mediamtx.yml:0644"; do
    IFS=: read -r src dst perms <<<"$file"
    install -m "$perms" "$src" "$dst.new"
    mv -f "$dst.new" "$dst"
  done
}

# "Update now" under Info: the panel (without root rights) creates a request file, a path unit starts
# the updater as root. The updater runs this machine's copy of deploy/debian/install.sh in update mode.
# New files are renamed into place, because the updater may be the one running this script.
install_updater() {
  local file
  mkdir -p "$LIB_DIR"
  for file in "$WORK/src/deploy/debian/install.sh:$LIB_DIR/install.sh" \
              "$WORK/src/deploy/mediamtx-panel-update.sh:/usr/local/sbin/mediamtx-panel-update"; do
    install -m 0755 "${file%%:*}" "${file#*:}.new"
    mv -f "${file#*:}.new" "${file#*:}"
  done
  install -d -m 0750 -o root -g mediamtx-panel /var/lib/mediamtx-panel-update

  cat > /etc/systemd/system/mediamtx-panel-update.path <<UNIT
[Unit]
Description=Start a MediaMTX Panel update when the panel asks for one

[Path]
PathExists=${DATA_DIR}/update-request
Unit=mediamtx-panel-update.service

[Install]
WantedBy=paths.target
UNIT

  cat > /etc/systemd/system/mediamtx-panel-update.service <<UNIT
[Unit]
Description=MediaMTX Panel update (started from Info in the panel)
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/mediamtx-panel-update
TimeoutStartSec=45min
UNIT
}

# ---------------------------------------------------------------- MediaMTX
install_mediamtx() {
  local arch
  case "$(dpkg --print-architecture 2>/dev/null || uname -m)" in
    amd64|x86_64) arch=amd64 ;;
    arm64|aarch64) arch=arm64 ;;
    armhf|armv7l) arch=armv7 ;;
    *) die "Unsupported architecture $(uname -m)" ;;
  esac
  log "Installing MediaMTX v${MEDIAMTX_VERSION} (${arch})…"
  local url="${MEDIAMTX_URL:-https://github.com/bluenviron/mediamtx/releases/download/v${MEDIAMTX_VERSION}/mediamtx_v${MEDIAMTX_VERSION}_linux_${arch}.tar.gz}"
  mkdir -p "$MTX_DIR" "$MTX_CONF_DIR" "$MTX_REC_DIR"
  curl -fsSL "$url" | tar -xz -C "$MTX_DIR" mediamtx
  chmod 0755 "$MTX_DIR/mediamtx"
  id -u mediamtx >/dev/null 2>&1 || useradd --system --home /var/lib/mediamtx --shell /usr/sbin/nologin mediamtx
  chown -R mediamtx:mediamtx /var/lib/mediamtx

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

# Starts from the project's mediamtx.yml and points it at the local panel.
configure_mediamtx() {
  sed -e "s#^authHTTPAddress:.*#authHTTPAddress: http://127.0.0.1:${PANEL_PORT}/api/mediamtx/auth?secret=${HOOK_SECRET}#" \
      -e "s#^webrtcAdditionalHosts:.*#webrtcAdditionalHosts: [${PUBLIC_HOST}]#" \
      -e "s#recordPath: .*#recordPath: ${MTX_REC_DIR}/%path/%Y-%m-%d_%H-%M-%S-%f#" \
      -e "s#^apiAddress:.*#apiAddress: 127.0.0.1:9997#" \
      -e "s#^playbackAddress:.*#playbackAddress: 127.0.0.1:9996#" \
      "$LIB_DIR/mediamtx.yml" > "$MTX_CONF_DIR/mediamtx.yml"
  chmod 0640 "$MTX_CONF_DIR/mediamtx.yml"
  chown root:mediamtx "$MTX_CONF_DIR/mediamtx.yml"
}

# ---------------------------------------------------------------- panel service + config
install_panel_service() {
  id -u mediamtx-panel >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin mediamtx-panel
  mkdir -p "$DATA_DIR/keys" "$CONF_DIR"
  chown -R mediamtx-panel:mediamtx-panel "$DATA_DIR"
  chmod 0750 "$DATA_DIR"

  cat > /etc/systemd/system/mediamtx-panel.service <<UNIT
[Unit]
Description=MediaMTX Panel
After=network-online.target
Wants=network-online.target
ConditionPathExists=${CONF_DIR}/panel.env

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

configure_panel() {
  # The panel keeps its release channel in panel-settings.json; Stable is its default.
  if [[ "$RELEASE_CHANNEL" == "test" && ! -f "$DATA_DIR/panel-settings.json" ]]; then
    echo '{ "ReleaseChannel": "Test" }' >"$DATA_DIR/panel-settings.json"
    chown mediamtx-panel:mediamtx-panel "$DATA_DIR/panel-settings.json"
  fi

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
      echo "# First server (the MediaMTX on this machine). Only used when the panel has no servers yet."
      echo "MediaMtx__ServerName=Local"
      echo "MediaMtx__ApiUrl=http://127.0.0.1:9997"
      echo "MediaMtx__ApiUser=panel"
      echo "MediaMtx__ApiPassword=${api_password}"
      echo "MediaMtx__AuthHookSecret=${HOOK_SECRET}"
      echo "MediaMtx__PublicHost=${PUBLIC_HOST}"
    fi
  } > "$CONF_DIR/panel.env.new"
  chmod 0640 "$CONF_DIR/panel.env.new"
  chown root:mediamtx-panel "$CONF_DIR/panel.env.new"
  mv -f "$CONF_DIR/panel.env.new" "$CONF_DIR/panel.env"
}

# First boot of a machine made from the CT template: configures it with fresh secrets.
install_firstboot_service() {
  cat > /etc/systemd/system/mediamtx-panel-firstboot.service <<UNIT
[Unit]
Description=MediaMTX Panel first-boot setup (passwords, secrets, addresses)
After=network-online.target
Wants=network-online.target
Before=mediamtx-panel.service mediamtx.service
ConditionPathExists=!${CONF_DIR}/panel.env

[Service]
Type=oneshot
RemainAfterExit=yes
EnvironmentFile=-${CONF_DIR}/firstboot.env
Environment=MODE=configure LOGIN_BANNER=yes LOG_TAG=first-boot
ExecStart=/bin/bash ${LIB_DIR}/install-in-lxc.sh
TimeoutStartSec=10min

[Install]
WantedBy=multi-user.target
UNIT
  {
    echo "# Settings for the first boot of this machine (mediamtx-panel-firstboot.service)."
    echo "# Everything else is configured in the web interface."
    echo "INSTALL_MEDIAMTX=${INSTALL_MEDIAMTX}"
    echo "PANEL_PORT=${PANEL_PORT}"
    echo "ADMIN_EMAIL=${ADMIN_EMAIL}"
    echo "RELEASE_CHANNEL=${RELEASE_CHANNEL}"
  } > "$CONF_DIR/firstboot.env"
}

# Shows the address, login and one-time password on the console (before and after logging in) until
# the administrator has chosen their own password; then only the address.
install_login_banner() {
  local file="$CONF_DIR/first-login"
  umask 077
  printf 'ADMIN_EMAIL=%q\nADMIN_PASSWORD=%q\nPANEL_PORT=%q\n' "$ADMIN_EMAIL" "$ADMIN_PASSWORD" "$PANEL_PORT" >"$file"
  umask 022
  cat > /etc/systemd/system/mediamtx-panel-login-banner.service <<UNIT
[Unit]
Description=Show the MediaMTX Panel address and first login on the console
After=mediamtx-panel.service

[Service]
Type=oneshot
ExecStart=${LIB_DIR}/login-banner.sh
UNIT
  cat > /etc/systemd/system/mediamtx-panel-login-banner.timer <<UNIT
[Unit]
Description=Hide the one-time password from the console once it has been changed

[Timer]
OnBootSec=20s
OnUnitActiveSec=30s

[Install]
WantedBy=timers.target
UNIT
  ln -sf "$LIB_DIR/login-banner.sh" /etc/update-motd.d/90-mediamtx-panel 2>/dev/null || true
}

wait_for_panel() {
  for _ in $(seq 1 90); do
    curl -fs -o /dev/null "http://127.0.0.1:${PANEL_PORT}/healthz" && return 0
    sleep 2
  done
  die "The panel did not start; see: journalctl -u mediamtx-panel"
}

# The first IPv4 address of this machine. At a first boot DHCP may still be busy: wait for it.
machine_ip() {
  local ip=""
  for _ in $(seq 1 30); do
    ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
    [[ -n "$ip" ]] && break
    sleep 2
  done
  echo "${ip:-$(hostname)}"
}

# The SDK is only needed to build; keep the machine small. 'update' installs it again.
# A SDK that was already installed before this script ran (e.g. for other work) is left alone.
remove_sdk() {
  [[ "$SDK_PREINSTALLED" == "yes" ]] && return 0
  dpkg-query -W -f='${Status}' dotnet-sdk-10.0 2>/dev/null | grep -q 'install ok installed' || return 0
  apt-get purge -y -qq dotnet-sdk-10.0 >/dev/null || true
  apt-get autoremove -y -qq >/dev/null || true
}

# Everything that does not depend on this particular machine: packages, program files, services.
prepare_machine() {
  install_packages
  unpack_sources
  build_panel
  install_panel_service
  install_helpers
  [[ "$INSTALL_MEDIAMTX" == "yes" ]] && install_mediamtx
  install_updater
}

# Everything that is specific to this machine: secrets, passwords, addresses. Then start.
configure_machine() {
  [[ -f "$APP_DIR/MediaMtxPanel.dll" ]] || die "The panel is not installed in $APP_DIR."
  [[ -n "$PANEL_HOST" ]] || PANEL_HOST="$(machine_ip)"
  [[ -n "$PUBLIC_HOST" ]] || PUBLIC_HOST="$PANEL_HOST"
  [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$(random_secret 16)Aa1"
  HOOK_SECRET="$(random_secret 32)"
  [[ -x "$MTX_DIR/mediamtx" ]] || INSTALL_MEDIAMTX=no
  configure_panel
  [[ "$INSTALL_MEDIAMTX" == "yes" ]] && configure_mediamtx
  [[ "$LOGIN_BANNER" == "yes" ]] && install_login_banner
  systemctl daemon-reload
  systemctl enable --now mediamtx-panel mediamtx-panel-update.path >/dev/null
  wait_for_panel
  # The administrator now exists; the one-time password does not need to stay in the configuration.
  sed -i '/^Panel__AdminPassword=/d' "$CONF_DIR/panel.env"
  [[ "$INSTALL_MEDIAMTX" == "yes" ]] && systemctl enable --now mediamtx >/dev/null
  if [[ "$LOGIN_BANNER" == "yes" ]]; then
    "$LIB_DIR/login-banner.sh" || true
    systemctl enable --now mediamtx-panel-login-banner.timer >/dev/null
  fi
  systemctl disable mediamtx-panel-firstboot.service >/dev/null 2>&1 || true
  rm -f "$CONF_DIR/firstboot.env"
  return 0
}

# ---------------------------------------------------------------- main
SDK_PREINSTALLED=no
if dpkg-query -W -f='${Status}' dotnet-sdk-10.0 2>/dev/null | grep -q 'install ok installed'; then
  [[ -f "$CONF_DIR/panel.env" ]] || SDK_PREINSTALLED=yes
fi

case "$MODE" in
  install)
    prepare_machine
    configure_machine
    remove_sdk
    ;;
  image)
    prepare_machine
    install_firstboot_service
    # 'systemctl enable' only creates links, so it also works in a chroot without a running systemd.
    systemctl enable mediamtx-panel-firstboot.service mediamtx-panel-update.path >/dev/null 2>&1
    remove_sdk
    ;;
  configure)
    [[ -f "$CONF_DIR/panel.env" ]] && die "This machine is already configured ($CONF_DIR/panel.env exists)."
    configure_machine
    log "MediaMTX Panel is ready at http://${PANEL_HOST}:${PANEL_PORT}"
    ;;
  update)
    install_packages
    unpack_sources
    build_panel
    install_panel_service
    install_helpers
    install_updater
    systemctl daemon-reload
    systemctl enable --now mediamtx-panel-update.path >/dev/null
    log "Restarting the panel…"
    systemctl restart mediamtx-panel
    PANEL_PORT="$(sed -n 's#^ASPNETCORE_URLS=.*:\([0-9]*\)$#\1#p' "$CONF_DIR/panel.env" | head -n1)"
    PANEL_PORT="${PANEL_PORT:-8080}"
    wait_for_panel
    remove_sdk
    ;;
  *)
    die "Unknown MODE '$MODE' (use install, update, image or configure)."
    ;;
esac

[[ "$MODE" == "configure" ]] || rm -rf "$WORK"
log "Done."
