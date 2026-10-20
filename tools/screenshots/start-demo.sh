#!/usr/bin/env bash
# Starts MediaMTX and the panel with demo settings, for the README screenshots (CI) or a local try-out.
#   tools/screenshots/start-demo.sh <published panel folder>
# Environment: MEDIAMTX_BIN (use this binary instead of downloading), MEDIAMTX_VERSION, DEMO_DIR.
# Leaves the panel on http://127.0.0.1:5080 (admin@example.com / Admin12345).
set -euo pipefail

PUBLISH_DIR="$(cd "${1:?Usage: start-demo.sh <published panel folder>}" && pwd)"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DEMO_DIR="${DEMO_DIR:-$(mktemp -d)}"
MEDIAMTX_VERSION="${MEDIAMTX_VERSION:-1.21.2}"
SECRET=demo-hook-secret
mkdir -p "$DEMO_DIR"

if [[ -z "${MEDIAMTX_BIN:-}" ]]; then
  curl -fsSL "https://github.com/bluenviron/mediamtx/releases/download/v${MEDIAMTX_VERSION}/mediamtx_v${MEDIAMTX_VERSION}_linux_amd64.tar.gz" \
    | tar -xz -C "$DEMO_DIR" mediamtx
  MEDIAMTX_BIN="$DEMO_DIR/mediamtx"
fi

sed -e "s#^authHTTPAddress:.*#authHTTPAddress: http://127.0.0.1:5080/api/mediamtx/auth?secret=${SECRET}#" \
    -e "s#recordPath: .*#recordPath: ${DEMO_DIR}/recordings/%path/%Y-%m-%d_%H-%M-%S-%f#" \
    -e "s#^apiAddress:.*#apiAddress: 127.0.0.1:9997#" \
    -e "s#^playbackAddress:.*#playbackAddress: 127.0.0.1:9996#" \
    "$ROOT/deploy/mediamtx.yml" > "$DEMO_DIR/mediamtx.yml"

export ASPNETCORE_URLS=http://127.0.0.1:5080
export ConnectionStrings__DefaultConnection="DataSource=${DEMO_DIR}/panel.db;Cache=Shared"
export Panel__AdminEmail=admin@example.com Panel__AdminPassword=Admin12345 Panel__AdminMustChangePassword=false
export Panel__EnableTestPayments=true Panel__UpdateRepository=""
export MediaMtx__ServerName="Amsterdam" MediaMtx__ApiUrl=http://127.0.0.1:9997 MediaMtx__ApiPassword=demo-api
export MediaMtx__AuthHookSecret="$SECRET" MediaMtx__PublicHost=localhost

(cd "$PUBLISH_DIR" && nohup dotnet MediaMtxPanel.dll > "$DEMO_DIR/panel.log" 2>&1 &)
for _ in $(seq 60); do curl -fs -o /dev/null http://127.0.0.1:5080/Account/Login && break; sleep 1; done
curl -fsS -o /dev/null http://127.0.0.1:5080/Account/Login || { cat "$DEMO_DIR/panel.log"; exit 1; }
(cd "$DEMO_DIR" && nohup "$MEDIAMTX_BIN" "$DEMO_DIR/mediamtx.yml" > "$DEMO_DIR/mediamtx.log" 2>&1 &)

echo "Panel: http://127.0.0.1:5080  (logs in $DEMO_DIR)"
