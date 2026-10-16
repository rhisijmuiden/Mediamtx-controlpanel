#!/usr/bin/env bash
# Creates a Proxmox LXC container with the MediaMTX Panel (and optionally MediaMTX itself).
#
# Run on the Proxmox host, from the root of the mediamtx-panel project:
#     bash deploy/proxmox/create-lxc.sh
#
# Update the panel in an existing container with the code in this folder:
#     bash deploy/proxmox/create-lxc.sh update <CTID>
#
# Every setting below can be overridden with an environment variable, e.g.
#     CT_ID=150 CT_IP=192.168.1.50/24 CT_GW=192.168.1.1 bash deploy/proxmox/create-lxc.sh
#
# Debian 13 (trixie) instead of Ubuntu 24.04:
#     CT_OS=debian bash deploy/proxmox/create-lxc.sh
set -euo pipefail

# ---------------------------------------------------------------- settings
CT_ID="${CT_ID:-}"                                 # empty = next free ID
CT_HOSTNAME="${CT_HOSTNAME:-mediamtx-panel}"
CT_CORES="${CT_CORES:-2}"
CT_MEMORY="${CT_MEMORY:-2048}"                     # MB (building the panel needs ~1.5 GB)
CT_SWAP="${CT_SWAP:-512}"
CT_DISK="${CT_DISK:-8}"                            # GB
CT_STORAGE="${CT_STORAGE:-local-lvm}"              # storage for the root disk
TEMPLATE_STORAGE="${TEMPLATE_STORAGE:-local}"      # storage that holds container templates
CT_BRIDGE="${CT_BRIDGE:-vmbr0}"
CT_VLAN="${CT_VLAN:-}"                             # optional VLAN tag
CT_IP="${CT_IP:-dhcp}"                             # "dhcp" or e.g. 192.168.1.50/24
CT_GW="${CT_GW:-}"                                 # gateway when CT_IP is static
CT_DNS="${CT_DNS:-}"                               # optional DNS server
CT_PASSWORD="${CT_PASSWORD:-}"                     # root password; empty = random
CT_SSH_KEY="${CT_SSH_KEY:-}"                       # optional path to a public key file

# Panel / MediaMTX settings (passed into the container)
INSTALL_MEDIAMTX="${INSTALL_MEDIAMTX:-yes}"        # also install MediaMTX in the same container
MEDIAMTX_VERSION="${MEDIAMTX_VERSION:-1.21.2}"
PANEL_PORT="${PANEL_PORT:-8080}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@localhost}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-}"               # empty = random
PUBLIC_HOST="${PUBLIC_HOST:-}"                     # empty = IP of the container

# Container OS: "ubuntu" (24.04) or "debian" (13). TEMPLATE_NAME picks another template of that OS.
CT_OS="${CT_OS:-ubuntu}"
case "$CT_OS" in
  ubuntu) TEMPLATE_NAME="${TEMPLATE_NAME:-ubuntu-24.04-standard}"; NET_CHECK_HOST=archive.ubuntu.com ;;
  debian) TEMPLATE_NAME="${TEMPLATE_NAME:-debian-13-standard}";   NET_CHECK_HOST=deb.debian.org ;;
  *) echo "CT_OS must be 'ubuntu' or 'debian' (got '$CT_OS')." >&2; exit 1 ;;
esac

# ---------------------------------------------------------------- helpers
c_info="\e[1;34m"; c_ok="\e[1;32m"; c_err="\e[1;31m"; c_off="\e[0m"
info() { echo -e "${c_info}[i]${c_off} $*"; }
ok()   { echo -e "${c_ok}[✓]${c_off} $*"; }
die()  { echo -e "${c_err}[✗]${c_off} $*" >&2; exit 1; }

# '|| true': head closing the pipe makes tr exit with SIGPIPE, which pipefail would turn into a failure.
random_secret() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c "${1:-24}" || true; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

preflight() {
  [[ $EUID -eq 0 ]] || die "Run this script as root on the Proxmox host."
  command -v pct >/dev/null || die "pct not found: this script must run on a Proxmox VE host."
  [[ -f "$PROJECT_DIR/src/MediaMtxPanel/MediaMtxPanel.csproj" ]] \
    || die "Project not found in $PROJECT_DIR. Run the script from the extracted mediamtx-panel folder."
  [[ -f "$SCRIPT_DIR/install-in-lxc.sh" ]] || die "install-in-lxc.sh is missing next to this script."
}

# Packs the panel sources and the in-container installer into the container and runs it.
push_and_install() {
  local id="$1"; shift
  local tarball
  tarball="$(mktemp --suffix=.tar.gz)"
  tar -C "$PROJECT_DIR" --exclude='bin' --exclude='obj' --exclude='*.db' -czf "$tarball" src deploy
  pct exec "$id" -- mkdir -p /tmp/mediamtx-panel-install
  pct push "$id" "$tarball" /tmp/mediamtx-panel-install/source.tar.gz
  pct push "$id" "$SCRIPT_DIR/install-in-lxc.sh" /tmp/mediamtx-panel-install/install-in-lxc.sh --perms 0755
  rm -f "$tarball"
  pct exec "$id" -- env "$@" bash /tmp/mediamtx-panel-install/install-in-lxc.sh
}

wait_for_network() {
  local id="$1"
  info "Waiting for the network in container $id…"
  for _ in $(seq 1 60); do
    if pct exec "$id" -- getent hosts "$NET_CHECK_HOST" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  die "Container $id has no working network/DNS. Check bridge, IP and gateway settings."
}

# ---------------------------------------------------------------- update mode
if [[ "${1:-}" == "update" ]]; then
  preflight
  id="${2:-}"
  [[ -n "$id" ]] || die "Usage: $0 update <CTID>"
  pct status "$id" | grep -q running || pct start "$id"
  wait_for_network "$id"
  info "Updating the panel in container $id…"
  push_and_install "$id" MODE=update
  ok "Panel updated in container $id."
  exit 0
fi

# ---------------------------------------------------------------- create
preflight

if [[ -z "$CT_ID" ]]; then
  CT_ID="$(pvesh get /cluster/nextid)"
fi
pct status "$CT_ID" >/dev/null 2>&1 && die "Container $CT_ID already exists. Set CT_ID to a free ID."

[[ -n "$CT_PASSWORD" ]] || CT_PASSWORD="$(random_secret 20)"
[[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$(random_secret 16)Aa1"

info "Looking up the $TEMPLATE_NAME template…"
pveam update >/dev/null
template="$(pveam available --section system | awk '{print $2}' | grep "^${TEMPLATE_NAME}" | sort -V | tail -n1)"
[[ -n "$template" ]] || die "Template $TEMPLATE_NAME not found in 'pveam available'. Debian 13 templates need Proxmox VE 8.4 or newer."
if ! pveam list "$TEMPLATE_STORAGE" | grep -q "$template"; then
  info "Downloading $template to $TEMPLATE_STORAGE…"
  pveam download "$TEMPLATE_STORAGE" "$template"
fi

net0="name=eth0,bridge=${CT_BRIDGE}"
if [[ "$CT_IP" == "dhcp" ]]; then
  net0+=",ip=dhcp"
else
  net0+=",ip=${CT_IP}"
  [[ -n "$CT_GW" ]] && net0+=",gw=${CT_GW}"
fi
[[ -n "$CT_VLAN" ]] && net0+=",tag=${CT_VLAN}"

create_args=(
  "$CT_ID" "${TEMPLATE_STORAGE}:vztmpl/${template}"
  --hostname "$CT_HOSTNAME"
  --cores "$CT_CORES"
  --memory "$CT_MEMORY"
  --swap "$CT_SWAP"
  --rootfs "${CT_STORAGE}:${CT_DISK}"
  --net0 "$net0"
  --unprivileged 1
  --features nesting=1
  --onboot 1
  --ostype "$CT_OS"
  --password "$CT_PASSWORD"
  --description "MediaMTX Panel"
)
[[ -n "$CT_DNS" ]] && create_args+=(--nameserver "$CT_DNS")
[[ -n "$CT_SSH_KEY" ]] && create_args+=(--ssh-public-keys "$CT_SSH_KEY")

info "Creating container $CT_ID ($CT_HOSTNAME, $template)…"
pct create "${create_args[@]}" >/dev/null
pct start "$CT_ID"
ok "Container $CT_ID started."

wait_for_network "$CT_ID"

ct_ip="$(pct exec "$CT_ID" -- bash -c "ip -4 -o addr show dev eth0 | awk '{print \$4}' | cut -d/ -f1 | head -n1")"
[[ -n "$PUBLIC_HOST" ]] || PUBLIC_HOST="$ct_ip"

info "Installing the panel$( [[ $INSTALL_MEDIAMTX == yes ]] && echo " and MediaMTX $MEDIAMTX_VERSION") (this takes a few minutes)…"
push_and_install "$CT_ID" \
  MODE=install \
  INSTALL_MEDIAMTX="$INSTALL_MEDIAMTX" \
  MEDIAMTX_VERSION="$MEDIAMTX_VERSION" \
  PANEL_PORT="$PANEL_PORT" \
  ADMIN_EMAIL="$ADMIN_EMAIL" \
  ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  PUBLIC_HOST="$PUBLIC_HOST" \
  PANEL_HOST="$ct_ip"

cat <<SUMMARY

$(ok "MediaMTX Panel is installed in container $CT_ID")

  Panel:            http://${ct_ip}:${PANEL_PORT}
  Login:            ${ADMIN_EMAIL}
  Password:         ${ADMIN_PASSWORD}
  Root password CT: ${CT_PASSWORD}
$( [[ $INSTALL_MEDIAMTX == yes ]] && echo "
  MediaMTX in the container: RTSP 8554, RTMP 1935, SRT 8890/udp, HLS 8888, WebRTC 8889 + 8189/udp
  Public address in the URLs: ${PUBLIC_HOST} (change under Servers in the panel)")

  Configuration in the container: /etc/mediamtx-panel/panel.env
  Logs: pct exec ${CT_ID} -- journalctl -u mediamtx-panel -f

Store these passwords somewhere safe; they are not shown again.
SUMMARY
