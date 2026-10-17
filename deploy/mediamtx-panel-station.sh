#!/usr/bin/env bash
# mediamtx-panel-station: the station command of the Build My TV Station contract (version 1),
# https://github.com/rhisijmuiden/BulldMyTVStation/blob/main/docs/station-contract.md
# Installed as /usr/local/bin/mediamtx-panel-station by deploy/proxmox/install-in-lxc.sh (which
# deploy/debian/install.sh runs as well). A standalone install never needs it.
#
#     mediamtx-panel-station [--reset-admin]
#
# Reads these environment variables. Not set: the setting stays as it is. Set to "": it is removed.
#     STATION_SERVICE_TOKEN    Panel__ServiceToken    Bearer token for /api/v1 (at least 32 characters)
#     STATION_BASE_PATH        Panel__PathBase        e.g. /channel1; "/" means the root
#     STATION_PUBLIC_URL       Panel__PublicUrl       e.g. https://tv.example.com/channel1
#     STATION_TRUSTED_PROXIES  Panel__TrustedProxies  comma-separated IPs or CIDRs of the reverse proxy
#
# Stores them in /etc/mediamtx-panel/panel.env, gives the administrator a new one-time password with
# --reset-admin, restarts the panel when something changed, waits for /healthz, writes
# /var/lib/mediamtx-panel/station.json and prints it on stdout (with "initialPassword" after
# --reset-admin). Everything else goes to stderr.
set -euo pipefail

CONF_FILE=/etc/mediamtx-panel/panel.env
DATA_DIR=/var/lib/mediamtx-panel
APP_DIR=/opt/mediamtx-panel
SERVICE=mediamtx-panel
SERVICE_USER=mediamtx-panel
STATION_FILE="$DATA_DIR/station.json"
HEALTH_TIMEOUT=60

log() { echo "[mediamtx-panel-station] $*" >&2; }
die() { echo "[mediamtx-panel-station] error: $*" >&2; exit 1; }

usage() {
  cat >&2 <<USAGE
Usage: mediamtx-panel-station [--reset-admin]

  --reset-admin   give the administrator a new one-time password (printed as "initialPassword")
  --help, -h      show this help

Settings come from STATION_SERVICE_TOKEN, STATION_BASE_PATH, STATION_PUBLIC_URL and
STATION_TRUSTED_PROXIES; see the top of this script.
USAGE
}

RESET_ADMIN=no
while (( $# )); do
  case "$1" in
    --reset-admin) RESET_ADMIN=yes ;;
    --help|-h) usage; exit 0 ;;
    *) usage; die "Unknown argument '$1'." ;;
  esac
  shift
done

[[ $EUID -eq 0 ]] || die "Run as root."
[[ -f "$CONF_FILE" ]] || die "The panel is not installed ($CONF_FILE is missing)."
[[ -f "$APP_DIR/MediaMtxPanel.dll" ]] || die "The panel is not installed ($APP_DIR/MediaMtxPanel.dll is missing)."

# ---------------------------------------------------------------- panel.env
CHANGED=no

conf_get() { awk -v key="$1" 'index($0, key "=") == 1 { print substr($0, length(key) + 2); exit }' "$CONF_FILE"; }
conf_has() { grep -q "^$1=" "$CONF_FILE"; }

# conf_set <key> <value>: an empty value removes the line. Keeps the file's owner and mode.
conf_set() {
  local key="$1" value="$2" tmp
  if [[ -z "$value" ]]; then
    conf_has "$key" || return 0
  elif conf_has "$key" && [[ "$(conf_get "$key")" == "$value" ]]; then
    return 0
  fi
  tmp="$(mktemp "$CONF_FILE.XXXXXX")"
  KEY="$key" VALUE="$value" awk '
    index($0, ENVIRON["KEY"] "=") == 1 { if (!done && ENVIRON["VALUE"] != "") print ENVIRON["KEY"] "=" ENVIRON["VALUE"]; done = 1; next }
    { print }
    END { if (!done && ENVIRON["VALUE"] != "") print ENVIRON["KEY"] "=" ENVIRON["VALUE"] }
  ' "$CONF_FILE" > "$tmp"
  chmod 0640 "$tmp"
  chown "root:$SERVICE_USER" "$tmp"
  mv -f "$tmp" "$CONF_FILE"
  CHANGED=yes
  if [[ -z "$value" ]]; then log "Removed $key."; else log "Set $key."; fi
}

if [[ -v STATION_SERVICE_TOKEN ]]; then
  if [[ -n "$STATION_SERVICE_TOKEN" ]]; then
    (( ${#STATION_SERVICE_TOKEN} >= 32 )) || die "STATION_SERVICE_TOKEN must be at least 32 characters."
    [[ "$STATION_SERVICE_TOKEN" =~ ^[A-Za-z0-9._~+/=-]+$ ]] || die "STATION_SERVICE_TOKEN may only contain letters, digits and . _ ~ + / = -"
  fi
  conf_set Panel__ServiceToken "$STATION_SERVICE_TOKEN"
fi

if [[ -v STATION_BASE_PATH ]]; then
  base="$STATION_BASE_PATH"
  base="${base#"${base%%[!/]*}"}"   # strip leading slashes
  base="${base%"${base##*[!/]}"}"   # strip trailing slashes
  [[ -z "$base" || "$base" =~ ^[A-Za-z0-9._~-]+(/[A-Za-z0-9._~-]+)*$ ]] || die "STATION_BASE_PATH must look like /channel1."
  conf_set Panel__PathBase "${base:+/$base}"
fi

if [[ -v STATION_PUBLIC_URL ]]; then
  [[ -z "$STATION_PUBLIC_URL" || "$STATION_PUBLIC_URL" =~ ^https?://[^[:space:]\"\'\\]+$ ]] || die "STATION_PUBLIC_URL must be an http:// or https:// address."
  conf_set Panel__PublicUrl "${STATION_PUBLIC_URL%/}"
fi

if [[ -v STATION_TRUSTED_PROXIES ]]; then
  proxies="${STATION_TRUSTED_PROXIES//[[:space:]]/}"
  if [[ -n "$proxies" ]]; then
    IFS=',' read -r -a entries <<< "$proxies"
    for entry in "${entries[@]}"; do
      [[ "$entry" =~ ^[0-9A-Fa-f.:]+(/[0-9]{1,3})?$ ]] || die "STATION_TRUSTED_PROXIES: '$entry' is not an IP address or CIDR."
    done
  fi
  conf_set Panel__TrustedProxies "$proxies"
fi

# ---------------------------------------------------------------- the panel's own commands
# Runs the panel with a command (see Services/StationCommands.cs) as the service user, with the
# service's configuration. Lines of panel.env are passed one by one: values such as the connection
# string contain ';', so the file cannot be sourced.
run_panel() {
  local env_args=() line key value
  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ "$line" =~ ^([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]] || continue
    key="${BASH_REMATCH[1]}" value="${BASH_REMATCH[2]}"
    [[ "$value" =~ ^\"(.*)\"$ ]] && value="${BASH_REMATCH[1]}"
    env_args+=("$key=$value")
  done < "$CONF_FILE"
  (cd "$APP_DIR" && runuser -u "$SERVICE_USER" -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME="$DATA_DIR" \
    LANG=C.UTF-8 ASPNETCORE_ENVIRONMENT=Production DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    "${env_args[@]}" /usr/bin/dotnet "$APP_DIR/MediaMtxPanel.dll" "$@")
}

PASSWORD=""
if [[ "$RESET_ADMIN" == "yes" ]]; then
  log "Setting a new one-time password for the administrator…"
  PASSWORD="$(run_panel admin-reset)" || die "Could not reset the administrator's password."
  [[ -n "$PASSWORD" ]] || die "Could not reset the administrator's password."
fi

# ---------------------------------------------------------------- restart + health
# The port of ASPNETCORE_URLS always stays open on 127.0.0.1 (also when another port was chosen under
# Settings), and /healthz answers at the root as well as under the base path.
url="$(conf_get ASPNETCORE_URLS)"
port="$(sed -nE 's#^[a-z]+://[^;/]*:([0-9]+).*#\1#p' <<< "${url%%;*}")"
port="${port:-8080}"

if [[ "$CHANGED" == "yes" ]]; then
  log "Restarting $SERVICE…"
  systemctl restart "$SERVICE"
elif ! systemctl is-active --quiet "$SERVICE"; then
  log "Starting $SERVICE…"
  systemctl start "$SERVICE"
fi

log "Waiting for http://127.0.0.1:${port}/healthz…"
healthy=no
for _ in $(seq 1 "$HEALTH_TIMEOUT"); do
  if curl -fsS -o /dev/null --max-time 2 "http://127.0.0.1:${port}/healthz" 2>/dev/null; then
    healthy=yes
    break
  fi
  sleep 1
done
[[ "$healthy" == "yes" ]] || die "The panel did not answer /healthz within ${HEALTH_TIMEOUT} seconds; see: journalctl -u $SERVICE"

# ---------------------------------------------------------------- station.json
info="$(run_panel station-info)" || die "Could not read the station information from the panel."
[[ "$info" == "{"*"}" ]] || die "The panel returned no station information."
(umask 077 && printf '%s\n' "$info" > "$STATION_FILE.tmp")
chmod 0600 "$STATION_FILE.tmp"
mv -f "$STATION_FILE.tmp" "$STATION_FILE"
log "Wrote $STATION_FILE."

if [[ -n "$PASSWORD" ]]; then
  # The password alphabet needs no JSON escaping (see CredentialGenerator).
  body="${info%\}}"
  body="${body%$'\n'}"
  printf '%s,\n  "initialPassword": "%s"\n}\n' "$body" "$PASSWORD"
else
  printf '%s\n' "$info"
fi
