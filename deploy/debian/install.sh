#!/usr/bin/env bash
# Installs the MediaMTX Panel directly on a Debian 13 machine (bare metal or a VM), without Proxmox.
# Installs everything it needs itself: .NET 10, the panel as a systemd service and, if you want,
# MediaMTX on the same machine.
#
# Run as root. Straight from GitHub:
#     bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)"
# On a minimal Debian without curl:
#     bash -c "$(wget -qO- https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)"
# or from a checkout of the repository:
#     bash deploy/debian/install.sh
# Without a checkout next to it, the script downloads the sources of $REPO_BRANCH from GitHub.
#
# In a terminal the script asks what to install: the panel together with a local MediaMTX, or only
# the panel (you then add MediaMTX servers that run elsewhere under Servers in the panel).
# Every setting below can also be set with an environment variable or an option; with
# INTERACTIVE=no (or --yes) nothing is asked, e.g.
#     bash deploy/debian/install.sh --panel-only --yes
#     INTERACTIVE=no PUBLIC_HOST=stream.example.com bash deploy/debian/install.sh
#
# Update an existing installation to the latest code (keeps configuration and database):
#     bash -c "$(curl -fsSL https://raw.githubusercontent.com/rhisijmuiden/Mediamtx-controlpanel/main/deploy/debian/install.sh)" _ update
set -euo pipefail

# ---------------------------------------------------------------- settings
INSTALL_MEDIAMTX="${INSTALL_MEDIAMTX:-yes}"        # "no" = only the control panel, no local MediaMTX
MEDIAMTX_VERSION="${MEDIAMTX_VERSION:-1.21.2}"
PANEL_PORT="${PANEL_PORT:-8080}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@localhost}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-}"               # empty = random
PANEL_HOST="${PANEL_HOST:-}"                       # empty = first IP of this machine
PUBLIC_HOST="${PUBLIC_HOST:-}"                     # empty = PANEL_HOST
INTERACTIVE="${INTERACTIVE:-yes}"                  # "no" = never ask, use the settings above

# Where the sources come from when the script does not run from a checkout
REPO="${REPO:-rhisijmuiden/Mediamtx-controlpanel}"
REPO_BRANCH="${REPO_BRANCH:-main}"

MODE=install
MIN_MEMORY_MB=1536                                 # building the panel needs ~1.5 GB (RAM + swap)
CONF_FILE=/etc/mediamtx-panel/panel.env
WORK=/tmp/mediamtx-panel-install

# ---------------------------------------------------------------- helpers
c_info="\e[1;34m"; c_ok="\e[1;32m"; c_warn="\e[1;33m"; c_err="\e[1;31m"; c_off="\e[0m"
info() { echo -e "${c_info}[i]${c_off} $*"; }
ok()   { echo -e "${c_ok}[✓]${c_off} $*"; }
warn() { echo -e "${c_warn}[!]${c_off} $*"; }
die()  { echo -e "${c_err}[✗]${c_off} $*" >&2; exit 1; }

# '|| true': head closing the pipe makes tr exit with SIGPIPE, which pipefail would turn into a failure.
random_secret() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom 2>/dev/null | head -c "${1:-24}" || true; }

usage() {
  cat <<USAGE
Usage: install.sh [install|update] [options]

  install           install the panel (default)
  update            rebuild and restart an existing installation, keep its data

Options for install:
  --panel-only      only the control panel, no local MediaMTX (same as INSTALL_MEDIAMTX=no)
  --with-mediamtx   the panel and MediaMTX on this machine (default)
  --yes, -y         ask nothing, use the defaults and environment variables (INTERACTIVE=no)
  --help, -h        show this help
USAGE
}

while (( $# )); do
  case "$1" in
    install|update) MODE="$1" ;;
    --panel-only) INSTALL_MEDIAMTX=no ;;
    --with-mediamtx) INSTALL_MEDIAMTX=yes ;;
    --yes|-y) INTERACTIVE=no ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; die "Unknown argument '$1'." ;;
  esac
  shift
done

# Run from a file: the checkout around it. Run through 'bash -c "$(curl …)"': nothing yet.
SCRIPT_DIR=""
PROJECT_DIR=""
if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
fi

preflight() {
  [[ $EUID -eq 0 ]] || die "Run this script as root (e.g. with 'sudo -i' first)."
  [[ -f /etc/os-release ]] || die "Cannot determine the operating system (/etc/os-release is missing)."
  # shellcheck source=/dev/null
  . /etc/os-release
  [[ "${ID:-}" == "debian" ]] || die "This installer is made for Debian 13; this machine runs '${PRETTY_NAME:-$ID}'."
  [[ "${VERSION_ID:-}" == "13" ]] || die "This installer is made for Debian 13 (trixie); this machine runs ${PRETTY_NAME:-Debian $VERSION_ID}."
  [[ -d /run/systemd/system ]] || die "systemd is not running; the panel and MediaMTX are installed as systemd services."
  command -v pct >/dev/null && warn "This looks like a Proxmox VE host. To run the panel in its own container, use deploy/proxmox/create-lxc.sh instead."
  return 0
}

# Packages the questions and the download need. Debian's netinst may come without curl.
install_bootstrap_packages() {
  local missing=()
  command -v curl >/dev/null || missing+=(curl)
  dpkg-query -W -f='${Status}' ca-certificates 2>/dev/null | grep -q 'install ok installed' || missing+=(ca-certificates)
  command -v whiptail >/dev/null || missing+=(whiptail)
  command -v ss >/dev/null || missing+=(iproute2)
  (( ${#missing[@]} )) || return 0
  info "Installing ${missing[*]}…"
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq "${missing[@]}" >/dev/null
}

# Downloads the sources from GitHub when there is no checkout next to the script.
fetch_sources() {
  [[ -n "$PROJECT_DIR" && -f "$PROJECT_DIR/src/MediaMtxPanel/MediaMtxPanel.csproj" ]] && return 0
  SOURCE_TMP="$(mktemp -d)"
  trap 'rm -rf "$SOURCE_TMP"' EXIT
  info "Downloading the panel from github.com/${REPO} (${REPO_BRANCH})…"
  curl -fsSL "https://codeload.github.com/${REPO}/tar.gz/refs/heads/${REPO_BRANCH}" | tar -xz -C "$SOURCE_TMP" --strip-components=1 \
    || die "Could not download https://github.com/${REPO} (branch ${REPO_BRANCH})."
  PROJECT_DIR="$SOURCE_TMP"
}

# Packs the sources where install-in-lxc.sh expects them and runs it. That script does the actual
# work for both the Proxmox container and this standalone install, so both end up identical.
run_installer() {
  local installer="$PROJECT_DIR/deploy/proxmox/install-in-lxc.sh"
  [[ -f "$PROJECT_DIR/src/MediaMtxPanel/MediaMtxPanel.csproj" ]] || die "Panel sources not found in $PROJECT_DIR."
  [[ -f "$installer" ]] || die "install-in-lxc.sh is missing in $PROJECT_DIR/deploy/proxmox."
  rm -rf "$WORK"
  mkdir -p "$WORK"
  chmod 0700 "$WORK"
  tar -C "$PROJECT_DIR" --exclude='bin' --exclude='obj' --exclude='*.db' -czf "$WORK/source.tar.gz" src deploy
  # SET_LOCALE=no: unlike in a fresh container, this machine's language is the owner's choice.
  env "$@" SET_LOCALE=no LOG_TAG=install bash "$installer"
}

machine_ip() { hostname -I 2>/dev/null | awk '{print $1}'; }

memory_mb() { awk '/^(MemTotal|SwapTotal):/ {kb += $2} END {print int(kb / 1024)}' /proc/meminfo; }

# Ports that must be free on this machine, as "<port>/<tcp|udp> <description>".
needed_ports() {
  echo "${PANEL_PORT}/tcp panel"
  [[ "$INSTALL_MEDIAMTX" == "yes" ]] || return 0
  printf '%s\n' "8554/tcp RTSP" "1935/tcp RTMP" "8890/udp SRT" "8888/tcp HLS" "8889/tcp WebRTC" "8189/udp WebRTC" "9997/tcp MediaMTX-API"
}

check_ports() {
  local port proto what busy=()
  while read -r port what; do
    proto="${port#*/}"; port="${port%/*}"
    if [[ "$proto" == tcp ]]; then
      ss -Hltn "sport = :$port" 2>/dev/null | grep -q . && busy+=("$port/$proto ($what)")
    else
      ss -Hlun "sport = :$port" 2>/dev/null | grep -q . && busy+=("$port/$proto ($what)")
    fi
  done < <(needed_ports)
  (( ${#busy[@]} == 0 )) || die "These ports are already in use on this machine: ${busy[*]}.
    Stop the program that uses them, choose another panel port (PANEL_PORT=…), or install with --panel-only."
}

validate_settings() {
  [[ "$INSTALL_MEDIAMTX" == "yes" || "$INSTALL_MEDIAMTX" == "no" ]] || die "INSTALL_MEDIAMTX must be 'yes' or 'no'."
  { [[ "$PANEL_PORT" =~ ^[0-9]+$ ]] && (( PANEL_PORT > 0 && PANEL_PORT < 65536 )); } || die "Port must be a number between 1 and 65535."
  [[ "$MEDIAMTX_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "MediaMTX version must look like 1.21.2."
  [[ "$ADMIN_EMAIL" == *@* ]] || die "Administrator email address must contain an @."
  [[ -n "$PANEL_HOST" ]] || die "Could not determine the IP address of this machine; set PANEL_HOST=…"
  check_ports
}

# ---------------------------------------------------------------- questions
TITLE="MediaMTX Panel"
BACKTITLE="MediaMTX Panel installer for Debian 13"

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

cancelled() { clear; echo -e "${c_err}[✗]${c_off} Cancelled, nothing was installed."; exit 0; }

# ask_input <var> <question>: shows an input box prefilled with the current value.
ask_input() {
  local var="$1" question="$2" value
  value="$(whiptail --backtitle "$BACKTITLE" --title "$TITLE" --inputbox "$question" 10 72 "${!var}" 3>&1 1>&2 2>&3)" || cancelled
  printf -v "$var" '%s' "$value"
}

# ask_menu <var> <question> <tag> <description> ...: picks one of the options; the current value is preselected.
ask_menu() {
  local var="$1" question="$2"; shift 2
  local value
  value="$(whiptail --backtitle "$BACKTITLE" --title "$TITLE" --default-item "${!var}" --menu "$question" 16 76 4 "$@" 3>&1 1>&2 2>&3)" || cancelled
  printf -v "$var" '%s' "$value"
}

summary_text() {
  cat <<TEXT
Machine:        ${PRETTY_NAME}, ${PANEL_HOST}
Components:     $( [[ $INSTALL_MEDIAMTX == yes ]] && echo "panel + MediaMTX $MEDIAMTX_VERSION" || echo "panel only (no local MediaMTX)" )
Panel:          http://${PANEL_HOST}:${PANEL_PORT}, admin ${ADMIN_EMAIL}
TEXT
  [[ $INSTALL_MEDIAMTX == yes ]] && echo "Stream address: ${PUBLIC_HOST}"
  return 0
}

ask_settings() {
  header
  whiptail --backtitle "$BACKTITLE" --title "$TITLE" --yesno \
    "This installs the MediaMTX Panel on this machine ($(hostname), ${PANEL_HOST}).\n\nIt adds .NET 10 from packages.microsoft.com and systemd services.\n\nContinue?" 13 72 || cancelled

  local components
  components="$( [[ $INSTALL_MEDIAMTX == yes ]] && echo full || echo panel )"
  ask_menu components "What do you want to install on this machine?" \
    full  "Panel + MediaMTX (streams are served from this machine)" \
    panel "Panel only (connect MediaMTX servers that run elsewhere)"
  if [[ "$components" == "full" ]]; then INSTALL_MEDIAMTX=yes; else INSTALL_MEDIAMTX=no; fi

  local mode=default
  ask_menu mode "How do you want to install?" \
    default "Default settings (port $PANEL_PORT, admin $ADMIN_EMAIL)" \
    advanced "Configure yourself (port, admin, addresses$( [[ $INSTALL_MEDIAMTX == yes ]] && echo ", MediaMTX version"))"
  if [[ "$mode" == "advanced" ]]; then
    ask_input PANEL_PORT "Panel port"
    ask_input ADMIN_EMAIL "Administrator email address (login)"
    ask_input PANEL_HOST "Address at which browsers and MediaMTX servers reach this panel"
    if [[ "$INSTALL_MEDIAMTX" == "yes" ]]; then
      ask_input PUBLIC_HOST "Public address of the stream server for broadcasters and viewers (IP or host name)"
      ask_input MEDIAMTX_VERSION "MediaMTX version (from github.com/bluenviron/mediamtx/releases)"
    fi
  fi
  validate_settings

  whiptail --backtitle "$BACKTITLE" --title "$TITLE" --yesno \
    "Install with these settings?\n\n$(summary_text)" 15 76 || cancelled
  clear
  header
}

# ---------------------------------------------------------------- update mode
preflight

if [[ "$MODE" == "update" ]]; then
  [[ -f "$CONF_FILE" ]] || die "No installation found ($CONF_FILE is missing). Run the script without 'update' to install."
  install_bootstrap_packages
  fetch_sources
  info "Updating the panel (this takes a few minutes)…"
  run_installer MODE=update
  ok "Panel updated."
  exit 0
fi

# ---------------------------------------------------------------- install
if [[ -f "$CONF_FILE" ]]; then
  die "The panel is already installed on this machine ($CONF_FILE exists).
    To update it, run this script with 'update'. To install again from scratch, first remove the old installation (see the README)."
fi

install_bootstrap_packages
[[ -n "$PANEL_HOST" ]] || PANEL_HOST="$(machine_ip)"
[[ -n "$PUBLIC_HOST" ]] || PUBLIC_HOST="$PANEL_HOST"

if can_ask; then
  ask_settings
else
  validate_settings
fi

if (( $(memory_mb) < MIN_MEMORY_MB )); then
  warn "This machine has $(memory_mb) MB of memory and swap; building the panel needs about ${MIN_MEMORY_MB} MB. Add swap if the build fails."
fi

fetch_sources
[[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$(random_secret 16)Aa1"

info "Installing the panel$( [[ $INSTALL_MEDIAMTX == yes ]] && echo " and MediaMTX $MEDIAMTX_VERSION") (this takes a few minutes)…"
run_installer \
  MODE=install \
  INSTALL_MEDIAMTX="$INSTALL_MEDIAMTX" \
  MEDIAMTX_VERSION="$MEDIAMTX_VERSION" \
  PANEL_PORT="$PANEL_PORT" \
  ADMIN_EMAIL="$ADMIN_EMAIL" \
  ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  PUBLIC_HOST="$PUBLIC_HOST" \
  PANEL_HOST="$PANEL_HOST"

if [[ $INSTALL_MEDIAMTX == yes ]]; then
  services="
  MediaMTX on this machine: RTSP 8554, RTMP 1935, SRT 8890/udp, HLS 8888, WebRTC 8889 + 8189/udp
  Public address in the stream URLs: ${PUBLIC_HOST} (change it under Servers in the panel)
  MediaMTX logs: journalctl -u mediamtx -f
"
else
  services="
  Only the panel is installed. Add your MediaMTX servers under Servers in the panel;
  the panel shows the mediamtx.yml settings each server needs.
"
fi

cat <<SUMMARY

$(ok "MediaMTX Panel is installed on $(hostname) (${PRETTY_NAME})")

  ┌──────────────────────────────────────────────────────────────
  │  Address:      http://${PANEL_HOST}:${PANEL_PORT}
  │  Login:        ${ADMIN_EMAIL}
  │  Password:     ${ADMIN_PASSWORD}
  │
  │  This is a one-time password: after your first login
  │  the panel immediately asks you to choose your own password.
  └──────────────────────────────────────────────────────────────
${services}
  Settings: ${CONF_FILE}
  Logs:     journalctl -u mediamtx-panel -f
  Update:   bash -c "\$(curl -fsSL https://raw.githubusercontent.com/${REPO}/${REPO_BRANCH}/deploy/debian/install.sh)" _ update

This password is not stored anywhere and will not be shown again.
SUMMARY
