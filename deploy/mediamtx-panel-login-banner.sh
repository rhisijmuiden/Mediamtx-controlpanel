#!/usr/bin/env bash
# Shows where the MediaMTX Panel can be reached, and the first login, on the console of a machine
# made from the Proxmox CT template:
#   - before logging in (/etc/issue.d/mediamtx-panel.issue, shown by the console's login prompt),
#   - after logging in (linked as /etc/update-motd.d/90-mediamtx-panel).
# The one-time password is shown only until the administrator has chosen their own password in the
# panel; then it is deleted and only the address stays.
#
# Installed by install-in-lxc.sh as /usr/local/lib/mediamtx-panel/login-banner.sh and run by
# mediamtx-panel-login-banner.timer.
set -uo pipefail

CONF_DIR=/etc/mediamtx-panel
LOGIN_FILE="$CONF_DIR/first-login"
DB=/var/lib/mediamtx-panel/panel.db
ISSUE=/etc/issue.d/mediamtx-panel.issue
CLAIM='mediamtx-panel:must-change-password'

ADMIN_EMAIL=""
ADMIN_PASSWORD=""
PANEL_PORT=""
# shellcheck source=/dev/null
[[ -r "$LOGIN_FILE" ]] && . "$LOGIN_FILE"
if [[ -z "$PANEL_PORT" && -r "$CONF_DIR/panel.env" ]]; then
  PANEL_PORT="$(sed -n 's#^ASPNETCORE_URLS=.*:\([0-9]*\)$#\1#p' "$CONF_DIR/panel.env" | head -n1)"
fi
PANEL_PORT="${PANEL_PORT:-8080}"

# Is the one-time password still the administrator's password? Yes until the panel has removed the
# "must change password" mark. When the database cannot be read, assume it still is.
password_pending() {
  [[ -n "$ADMIN_PASSWORD" ]] || return 1
  [[ -r "$DB" ]] && command -v sqlite3 >/dev/null || return 0
  local left
  left="$(sqlite3 -readonly "$DB" "SELECT COUNT(*) FROM AspNetUserClaims WHERE ClaimType = '$CLAIM';" 2>/dev/null)" || return 0
  [[ "$left" != "0" ]]
}

if [[ -n "$ADMIN_PASSWORD" ]] && ! password_pending && [[ $EUID -eq 0 ]]; then
  ADMIN_PASSWORD=""
  printf 'PANEL_PORT=%q\n' "$PANEL_PORT" >"$LOGIN_FILE"
fi

# banner <address>
banner() {
  echo
  echo "  MediaMTX Panel"
  echo "    Address:   http://$1:${PANEL_PORT}"
  if [[ -n "$ADMIN_PASSWORD" ]]; then
    echo "    Login:     ${ADMIN_EMAIL}"
    echo "    Password:  ${ADMIN_PASSWORD}"
    echo "    (one-time password: the panel asks you to choose your own at the first login)"
  fi
  echo
}

if [[ "$(basename "$0")" == 90-mediamtx-panel || "${1:-}" == "--print" ]]; then
  banner "$(hostname -I 2>/dev/null | awk '{print $1}')"
  exit 0
fi

[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
mkdir -p /etc/issue.d
# \4 is replaced by the console's login prompt with this machine's IPv4 address.
banner '\4' >"$ISSUE.new"
chmod 0644 "$ISSUE.new"
mv -f "$ISSUE.new" "$ISSUE"
# Nothing left to hide: stop checking.
[[ -n "$ADMIN_PASSWORD" ]] || systemctl disable --now mediamtx-panel-login-banner.timer >/dev/null 2>&1 || true
exit 0
