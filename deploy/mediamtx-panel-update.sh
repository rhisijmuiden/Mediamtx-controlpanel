#!/usr/bin/env bash
# Updates the MediaMTX Panel when an administrator clicks "Update now" under Info.
#
# Installed by install-in-lxc.sh as /usr/local/sbin/mediamtx-panel-update and started as root by
# mediamtx-panel-update.service, which mediamtx-panel-update.path starts when the panel creates
# /var/lib/mediamtx-panel/update-request. The panel itself runs without root rights; the request
# file only says "update" and its contents are never read.
#
# What it does:
#   1. keeps a local copy of the application, the database, the settings and the configuration
#      in /var/backups/mediamtx-panel (the newest three are kept);
#   2. runs the installed install.sh in update mode, which downloads the newest release of the
#      release channel chosen in the panel, builds it and restarts the panel;
#   3. when that fails and the panel no longer starts, puts the copy back and starts the old version.
# Progress goes to /var/lib/mediamtx-panel-update/{status.json,update.log}, which the panel shows.
#
# Can also be run by hand: sudo mediamtx-panel-update
set -uo pipefail

STATE_DIR=/var/lib/mediamtx-panel-update
REQUEST=/var/lib/mediamtx-panel/update-request
INSTALLER=/usr/local/lib/mediamtx-panel/install.sh
APP_DIR=/opt/mediamtx-panel
DATA_DIR=/var/lib/mediamtx-panel
CONF_DIR=/etc/mediamtx-panel
BACKUP_DIR=/var/backups/mediamtx-panel
KEEP_BACKUPS=3
STATUS="$STATE_DIR/status.json"
LOG="$STATE_DIR/update.log"

[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }

# The path unit starts this service again as long as the request exists.
rm -f "$REQUEST"

install -d -m 0750 -o root -g mediamtx-panel "$STATE_DIR"
exec 9>"$STATE_DIR/lock"
flock -n 9 || { echo "An update is already running." >&2; exit 0; }

: >"$LOG"
chmod 0640 "$LOG"
chgrp mediamtx-panel "$LOG" 2>/dev/null || true
exec >>"$LOG" 2>&1

STARTED="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
SNAPSHOT=""

log() { echo "[$(date +%H:%M:%S)] $*"; }

# status <state> <step> [message]: written to a temporary file first, so the panel never reads half a file.
# Every value is a fixed text from this script, a time or a path without quotes.
status() {
  local finished=null
  [[ "$1" == running ]] || finished="\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\""
  printf '{\n  "State": "%s",\n  "Step": "%s",\n  "Message": "%s",\n  "Started": "%s",\n  "Finished": %s,\n  "Snapshot": "%s"\n}\n' \
    "$1" "$2" "${3:-}" "$STARTED" "$finished" "$SNAPSHOT" >"$STATUS.tmp"
  chmod 0640 "$STATUS.tmp"
  chgrp mediamtx-panel "$STATUS.tmp" 2>/dev/null || true
  mv -f "$STATUS.tmp" "$STATUS"
}

fail() {
  log "ERROR: $1"
  status failed "${2:-failed}" "$1"
  exit 1
}

panel_port() {
  local url
  url="$(grep -E '^ASPNETCORE_URLS=' "$CONF_DIR/panel.env" 2>/dev/null | head -n1)"
  [[ "$url" =~ :([0-9]+) ]] && echo "${BASH_REMATCH[1]}" || echo 8080
}

panel_healthy() {
  local port
  port="$(panel_port)"
  for _ in $(seq 1 60); do
    curl -fs -o /dev/null "http://127.0.0.1:${port}/healthz" && return 0
    sleep 2
  done
  return 1
}

# ---------------------------------------------------------------- 1. local copy
take_snapshot() {
  status running snapshot
  log "Saving a copy of the current installation…"
  mkdir -p "$BACKUP_DIR"
  chmod 0700 "$BACKUP_DIR"
  local stamp work
  stamp="$(date -u +%Y%m%d-%H%M%S)"
  work="$(mktemp -d)"
  # A consistent copy of the database while the panel keeps running.
  if [[ -f "$DATA_DIR/panel.db" ]]; then
    sqlite3 "$DATA_DIR/panel.db" ".backup '$work/panel.db'" || { rm -rf "$work"; return 1; }
  fi
  tar -C / -czf "$work/files.tar.gz" \
    --exclude="${DATA_DIR#/}/panel.db" --exclude="${DATA_DIR#/}/panel.db-*" \
    --exclude="${DATA_DIR#/}/backup-work" --exclude="${DATA_DIR#/}/restore-pending" \
    "${APP_DIR#/}" "${DATA_DIR#/}" "${CONF_DIR#/}" || { rm -rf "$work"; return 1; }
  SNAPSHOT="$BACKUP_DIR/pre-update-${stamp}.tar"
  tar -C "$work" -cf "$SNAPSHOT" . || { rm -rf "$work"; return 1; }
  rm -rf "$work"
  chmod 0600 "$SNAPSHOT"
  log "Copy saved: $SNAPSHOT ($(du -h "$SNAPSHOT" | cut -f1))"
  # Only the newest copies stay.
  ls -1t "$BACKUP_DIR"/pre-update-*.tar 2>/dev/null | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm -f
}

# ---------------------------------------------------------------- 3. roll back
restore_snapshot() {
  log "Putting the previous version back from $SNAPSHOT…"
  local work
  work="$(mktemp -d)"
  tar -C "$work" -xf "$SNAPSHOT" || return 1
  systemctl stop mediamtx-panel || true
  find "$APP_DIR" -mindepth 1 -delete
  tar -C / -xzf "$work/files.tar.gz" || return 1
  if [[ -f "$work/panel.db" ]]; then
    rm -f "$DATA_DIR"/panel.db-*
    install -m 0640 -o mediamtx-panel -g mediamtx-panel "$work/panel.db" "$DATA_DIR/panel.db"
  fi
  rm -rf "$work"
  systemctl start mediamtx-panel
  panel_healthy
}

# ---------------------------------------------------------------- main
log "Update requested."
[[ -f "$INSTALLER" ]] || fail "The installer is missing ($INSTALLER). Update once from the command line." prepare
[[ -f "$CONF_DIR/panel.env" ]] || fail "No panel installation found ($CONF_DIR/panel.env is missing)." prepare

take_snapshot || fail "Could not save a copy of the current installation; nothing was changed." snapshot

status running install
log "Downloading, building and installing the new version (this takes a few minutes)…"
# FROM_GITHUB: always download, the installed copy of install.sh has no sources next to it.
if FROM_GITHUB=yes INTERACTIVE=no bash "$INSTALLER" update; then
  log "Update finished."
  status succeeded done
  exit 0
fi

log "The update failed."
if panel_healthy; then
  # The build failed before anything was replaced; the old version is still running.
  fail "The update failed; the current version keeps running. See the log for details." install
fi
if restore_snapshot; then
  log "The previous version runs again."
  status failed rollback "The new version did not start, so the previous version was put back."
  exit 1
fi
fail "The new version did not start and putting the previous version back failed too. Restore $SNAPSHOT by hand." rollback
