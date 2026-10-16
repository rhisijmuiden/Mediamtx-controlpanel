#!/usr/bin/env bash
# Creates a Proxmox LXC container with the MediaMTX Panel (and optionally MediaMTX itself).
#
# Run in the shell of the Proxmox host (as root). Straight from GitHub:
#     bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)"
# or from a checkout of the repository:
#     bash deploy/proxmox/create-lxc.sh
# Without a checkout next to it, the script downloads the sources of $REPO_BRANCH from GitHub.
#
# Update the panel in an existing container to the latest code:
#     bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/proxmox/create-lxc.sh)" _ update <CTID>
#
# In a terminal the script asks how to create the container (like the community-scripts.org
# scripts): "Standaard" uses the defaults below, "Geavanceerd" asks for every setting.
# Every setting below can also be set with an environment variable; with INTERACTIVE=no
# nothing is asked, e.g.
#     INTERACTIVE=no CT_ID=150 CT_IP=192.168.1.50/24 CT_GW=192.168.1.1 bash deploy/proxmox/create-lxc.sh
#
# The container runs Debian 13 (trixie); Proxmox VE 8.4 or newer has its template.
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

INTERACTIVE="${INTERACTIVE:-yes}"                 # "no" = never ask, use the settings above

# Where the sources come from when the script does not run from a checkout
REPO="${REPO:-rhisijmuiden/Mediamtx-controlpanel}"
REPO_BRANCH="${REPO_BRANCH:-main}"

# Container OS: Debian 13. TEMPLATE_NAME picks a specific debian-13 template build.
TEMPLATE_NAME="${TEMPLATE_NAME:-debian-13-standard}"
NET_CHECK_HOST=deb.debian.org

# ---------------------------------------------------------------- helpers
c_info="\e[1;34m"; c_ok="\e[1;32m"; c_err="\e[1;31m"; c_off="\e[0m"
info() { echo -e "${c_info}[i]${c_off} $*"; }
ok()   { echo -e "${c_ok}[✓]${c_off} $*"; }
die()  { echo -e "${c_err}[✗]${c_off} $*" >&2; exit 1; }

# '|| true': head closing the pipe makes tr exit with SIGPIPE, which pipefail would turn into a failure.
random_secret() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c "${1:-24}" || true; }

# Run from a file: the checkout around it. Run through 'bash -c "$(curl …)"': nothing yet.
SCRIPT_DIR=""
PROJECT_DIR=""
if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
fi

# Downloads the sources from GitHub when there is no checkout next to the script.
fetch_sources() {
  [[ -n "$PROJECT_DIR" && -f "$PROJECT_DIR/src/MediaMtxPanel/MediaMtxPanel.csproj" ]] && return 0
  command -v curl >/dev/null || die "curl is needed to download the sources."
  SOURCE_TMP="$(mktemp -d)"
  trap 'rm -rf "$SOURCE_TMP"' EXIT
  info "Downloading the panel from github.com/${REPO} (${REPO_BRANCH})…"
  curl -fsSL "https://codeload.github.com/${REPO}/tar.gz/refs/heads/${REPO_BRANCH}" | tar -xz -C "$SOURCE_TMP" --strip-components=1 \
    || die "Could not download https://github.com/${REPO} (branch ${REPO_BRANCH})."
  PROJECT_DIR="$SOURCE_TMP"
  SCRIPT_DIR="$SOURCE_TMP/deploy/proxmox"
}

preflight() {
  [[ $EUID -eq 0 ]] || die "Run this script as root on the Proxmox host."
  command -v pct >/dev/null || die "pct not found: this script must run on a Proxmox VE host."
}

prepare_sources() {
  fetch_sources
  [[ -f "$PROJECT_DIR/src/MediaMtxPanel/MediaMtxPanel.csproj" ]] || die "Panel sources not found in $PROJECT_DIR."
  [[ -f "$SCRIPT_DIR/install-in-lxc.sh" ]] || die "install-in-lxc.sh is missing in $SCRIPT_DIR."
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
  # Start from a clean environment: pct exec passes on the host shell's variables, such as a
  # LANG the container has no locale for and an XDG_RUNTIME_DIR (/run/user/0) that does not exist there.
  pct exec "$id" -- env -i PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    HOME=/root LANG=C.UTF-8 LC_ALL=C.UTF-8 TERM="${TERM:-xterm}" "$@" bash /tmp/mediamtx-panel-install/install-in-lxc.sh
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

# ---------------------------------------------------------------- questions
TITLE="MediaMTX Panel LXC"

header() {
  clear
  cat <<'BANNER'
    __  ___         ___       __  ___________  __    ____                   __
   /  |/  /__  ____/ (_)___ _/  |/  /_  __/ |/ /   / __ \____ _____  ___  / /
  / /|_/ / _ \/ __  / / __ `/ /|_/ / / /  |   /   / /_/ / __ `/ __ \/ _ \/ /
 / /  / /  __/ /_/ / / /_/ / /  / / / /  /   |   / ____/ /_/ / / / /  __/ /
/_/  /_/\___/\__,_/_/\__,_/_/  /_/ /_/  /_/|_|  /_/    \__,_/_/ /_/\___/_/

BANNER
}

can_ask() { [[ "$INTERACTIVE" == "yes" && -t 0 && -t 1 ]] && command -v whiptail >/dev/null; }

cancelled() { clear; echo -e "${c_err}[✗]${c_off} Afgebroken, er is niets aangemaakt."; exit 0; }

# ask_input <var> <question> [--password]: shows an input box prefilled with the current value.
ask_input() {
  local var="$1" question="$2" kind="${3:---inputbox}" value
  value="$(whiptail --backtitle "Proxmox VE" --title "$TITLE" "$kind" "$question" 10 72 "${!var}" 3>&1 1>&2 2>&3)" || cancelled
  printf -v "$var" '%s' "$value"
}

# ask_menu <var> <question> <tag> <description> ...: picks one of the options; the current value is preselected.
ask_menu() {
  local var="$1" question="$2"; shift 2
  local value
  value="$(whiptail --backtitle "Proxmox VE" --title "$TITLE" --default-item "${!var}" --menu "$question" 18 72 8 "$@" 3>&1 1>&2 2>&3)" || cancelled
  printf -v "$var" '%s' "$value"
}

ask_yesno() {
  local var="$1" question="$2" default=()
  [[ "${!var}" == "yes" ]] || default=(--defaultno)
  if whiptail --backtitle "Proxmox VE" --title "$TITLE" "${default[@]}" --yesno "$question" 10 72; then
    printf -v "$var" yes
  else
    printf -v "$var" no
  fi
}

# Storages that can hold the given content type, as menu items "<name> <type, free>".
storage_items() {
  pvesm status -content "$1" 2>/dev/null | awk 'NR>1 && $3=="active" {printf "%s\n%s, %.1f GB vrij\n", $1, $2, $6/1024/1024}'
}

bridge_items() {
  local b
  for b in $(ip -o link show type bridge 2>/dev/null | awk -F': ' '{print $2}' | cut -d@ -f1 | sort); do
    printf '%s\nnetwerkbrug\n' "$b"
  done
}

pick_storage() {
  local var="$1" content="$2" question="$3" items=()
  mapfile -t items < <(storage_items "$content")
  case "${#items[@]}" in
    0) die "Geen opslag gevonden die '$content' ondersteunt." ;;
    2) printf -v "$var" '%s' "${items[0]}" ;;      # only one storage: nothing to choose
    *) ask_menu "$var" "$question" "${items[@]}" ;;
  esac
}

summary_text() {
  local net="$CT_IP"
  [[ "$CT_IP" != "dhcp" && -n "$CT_GW" ]] && net+=" via $CT_GW"
  [[ -n "$CT_VLAN" ]] && net+=", VLAN $CT_VLAN"
  cat <<TEXT
Container ID:   $CT_ID
Hostnaam:       $CT_HOSTNAME
Besturing:      Debian 13 (unprivileged)
CPU / RAM:      $CT_CORES cores / $CT_MEMORY MB (swap $CT_SWAP MB)
Schijf:         $CT_DISK GB op $CT_STORAGE
Netwerk:        $CT_BRIDGE, $net
MediaMTX:       $( [[ $INSTALL_MEDIAMTX == yes ]] && echo "ook installeren ($MEDIAMTX_VERSION)" || echo "niet installeren" )
Paneel:         poort $PANEL_PORT, beheerder $ADMIN_EMAIL
TEXT
}

advanced_settings() {
  ask_input CT_ID "Container ID"
  ask_input CT_HOSTNAME "Hostnaam"
  ask_input CT_CORES "Aantal CPU-cores"
  ask_input CT_MEMORY "Geheugen in MB (minimaal 2048: het paneel wordt in de container gebouwd)"
  ask_input CT_SWAP "Swap in MB"
  ask_input CT_DISK "Schijfgrootte in GB"
  pick_storage CT_STORAGE rootdir "Op welke opslag komt de schijf van de container?"
  pick_storage TEMPLATE_STORAGE vztmpl "Op welke opslag staat (of komt) de Debian 13-template?"

  local bridges=()
  mapfile -t bridges < <(bridge_items)
  if (( ${#bridges[@]} > 2 )); then
    ask_menu CT_BRIDGE "Netwerkbrug" "${bridges[@]}"
  else
    ask_input CT_BRIDGE "Netwerkbrug"
  fi
  ask_input CT_IP "IPv4-adres: 'dhcp' of een vast adres met prefix, bijv. 192.168.1.50/24"
  if [[ "$CT_IP" != "dhcp" ]]; then
    ask_input CT_GW "Gateway, bijv. 192.168.1.1"
  fi
  ask_input CT_VLAN "VLAN-tag (leeg = geen)"
  ask_input CT_DNS "DNS-server (leeg = die van de Proxmox-host)"
  ask_input CT_PASSWORD "Root-wachtwoord van de container (leeg = willekeurig)" --passwordbox
  ask_input CT_SSH_KEY "Pad naar een publieke SSH-sleutel voor root (leeg = geen)"

  ask_yesno INSTALL_MEDIAMTX "MediaMTX ook in deze container installeren?\n\nKies 'Nee' als je alleen het paneel wilt en MediaMTX-servers later koppelt."
  ask_input PANEL_PORT "Poort van het paneel"
  ask_input ADMIN_EMAIL "E-mailadres (login) van de beheerder"
  ask_input PUBLIC_HOST "Publiek adres van de streamserver voor zenders en kijkers (leeg = IP van de container)"
}

validate_settings() {
  [[ "$CT_ID" =~ ^[0-9]+$ ]] || die "Container ID moet een getal zijn (kreeg '$CT_ID')."
  [[ "$CT_CORES" =~ ^[0-9]+$ && "$CT_MEMORY" =~ ^[0-9]+$ && "$CT_SWAP" =~ ^[0-9]+$ && "$CT_DISK" =~ ^[0-9]+$ ]] \
    || die "Cores, geheugen, swap en schijf moeten getallen zijn."
  (( CT_MEMORY >= 1536 )) || die "Geef de container minimaal 1536 MB geheugen; het paneel wordt in de container gebouwd."
  [[ "$PANEL_PORT" =~ ^[0-9]+$ ]] || die "Poort moet een getal zijn."
  [[ "$CT_IP" == "dhcp" || "$CT_IP" =~ ^[0-9.]+/[0-9]+$ ]] || die "IP-adres moet 'dhcp' zijn of bijv. 192.168.1.50/24."
  [[ -z "$CT_VLAN" || "$CT_VLAN" =~ ^[0-9]+$ ]] || die "VLAN-tag moet een getal zijn."
  [[ -z "$CT_SSH_KEY" || -f "$CT_SSH_KEY" ]] || die "SSH-sleutel $CT_SSH_KEY bestaat niet."
  pct status "$CT_ID" >/dev/null 2>&1 && die "Container $CT_ID bestaat al. Kies een vrij ID."
  return 0
}

ask_settings() {
  header
  whiptail --backtitle "Proxmox VE" --title "$TITLE" --yesno \
    "Dit maakt een nieuwe Debian 13-container met het MediaMTX Panel.\n\nDoorgaan?" 10 72 || cancelled

  local mode=standaard
  ask_menu mode "Hoe wil je de container aanmaken?" \
    standaard "Standaardinstellingen ($CT_CORES cores, $CT_MEMORY MB, $CT_DISK GB, DHCP)" \
    geavanceerd "Zelf instellen (resources, opslag, netwerk, paneel)"
  if [[ "$mode" == "geavanceerd" ]]; then
    advanced_settings
  else
    # Defaults, except that the storages must exist on this host.
    pvesm status -content rootdir 2>/dev/null | awk 'NR>1{print $1}' | grep -qx "$CT_STORAGE" \
      || pick_storage CT_STORAGE rootdir "Opslag '$CT_STORAGE' bestaat niet. Waar komt de schijf van de container?"
    pvesm status -content vztmpl 2>/dev/null | awk 'NR>1{print $1}' | grep -qx "$TEMPLATE_STORAGE" \
      || pick_storage TEMPLATE_STORAGE vztmpl "Opslag '$TEMPLATE_STORAGE' bestaat niet. Waar komt de template?"
  fi
  validate_settings

  whiptail --backtitle "Proxmox VE" --title "$TITLE" --yesno \
    "Container aanmaken met deze instellingen?\n\n$(summary_text)" 20 72 || cancelled
  clear
  header
}

# ---------------------------------------------------------------- update mode
if [[ "${1:-}" == "update" ]]; then
  preflight
  prepare_sources
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
if can_ask; then
  ask_settings
else
  validate_settings
fi
prepare_sources

[[ -n "$CT_PASSWORD" ]] || CT_PASSWORD="$(random_secret 20)"
[[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$(random_secret 16)Aa1"

info "Looking up the $TEMPLATE_NAME template…"
pveam update >/dev/null
# The template list holds builds for several architectures (amd64, arm64); take the one for this host.
host_arch="$(dpkg --print-architecture 2>/dev/null || echo amd64)"
template="$(pveam available --section system | awk '{print $2}' | grep "^${TEMPLATE_NAME}_.*_${host_arch}\.tar" | sort -V | tail -n1)"
[[ -n "$template" ]] || die "Template $TEMPLATE_NAME ($host_arch) not found in 'pveam available'. Debian 13 templates need Proxmox VE 8.4 or newer."
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
  --ostype debian
  --arch "$host_arch"
  --password "$CT_PASSWORD"
  --description "MediaMTX Panel"
)
[[ -n "$CT_DNS" ]] && create_args+=(--nameserver "$CT_DNS")
[[ -n "$CT_SSH_KEY" ]] && create_args+=(--ssh-public-keys "$CT_SSH_KEY")

info "Creating container $CT_ID ($CT_HOSTNAME, $template)…"
pct create "${create_args[@]}" >/dev/null
if ! pct start "$CT_ID"; then
  pct destroy "$CT_ID" --purge >/dev/null 2>&1 || true
  die "Container $CT_ID could not start and has been removed again. Details: journalctl -u pve-container@${CT_ID}"
fi
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

services=""
if [[ $INSTALL_MEDIAMTX == yes ]]; then
  services="
  MediaMTX in de container: RTSP 8554, RTMP 1935, SRT 8890/udp, HLS 8888, WebRTC 8889 + 8189/udp
  Publiek adres in de stream-URL's: ${PUBLIC_HOST} (aan te passen onder Servers in het paneel)
"
fi

cat <<SUMMARY

$(ok "MediaMTX Panel is geïnstalleerd in container $CT_ID (Debian 13)")

  ┌──────────────────────────────────────────────────────────────
  │  Adres:        http://${ct_ip}:${PANEL_PORT}
  │  Login:        ${ADMIN_EMAIL}
  │  Wachtwoord:   ${ADMIN_PASSWORD}
  │
  │  Dit is een eenmalig wachtwoord: na de eerste keer inloggen
  │  vraagt het paneel je meteen een eigen wachtwoord te kiezen.
  └──────────────────────────────────────────────────────────────

  Root-wachtwoord van de container: ${CT_PASSWORD}
${services}
  Instellingen in de container: /etc/mediamtx-panel/panel.env
  Logs:      pct exec ${CT_ID} -- journalctl -u mediamtx-panel -f
  Bijwerken: bash -c "\$(curl -fsSL https://raw.githubusercontent.com/${REPO}/${REPO_BRANCH}/deploy/proxmox/create-lxc.sh)" _ update ${CT_ID}

Deze wachtwoorden worden nergens bewaard en niet opnieuw getoond.
SUMMARY
