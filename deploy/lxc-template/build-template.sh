#!/usr/bin/env bash
# Builds the Proxmox VE container template (CT template) of the MediaMTX Panel:
#     mediamtx-panel-<version>-debian-13-amd64.tar.zst
#
# It starts from Proxmox's own Debian 13 template (download.proxmox.com), installs the panel,
# .NET 10, ffmpeg and MediaMTX into it in a chroot, and packs it again. Nothing in the template is
# specific to one machine: at its first boot the container makes its own secrets and one-time admin
# password (mediamtx-panel-firstboot.service) and shows the address and login on its console.
#
# Runs as root on a Debian or Ubuntu machine with internet access (the release workflow runs it on
# GitHub Actions):
#     sudo deploy/lxc-template/build-template.sh --app <publish dir> --version 1.5.0 --out dist
# <publish dir> is the output of:
#     dotnet publish src/MediaMtxPanel/MediaMtxPanel.csproj -c Release -o <publish dir> -p:Version=1.5.0
set -euo pipefail

ARCH=amd64
PVE_IMAGES="${PVE_IMAGES:-http://download.proxmox.com/images/system}"
BASE_TEMPLATE="${BASE_TEMPLATE:-}"                 # empty = newest debian-13-standard of Proxmox
MEDIAMTX_VERSION="${MEDIAMTX_VERSION:-1.21.2}"
RELEASE_CHANNEL="${RELEASE_CHANNEL:-stable}"
APP_DIR=""
VERSION=""
OUT_DIR="$PWD/dist"
SOURCE_COMMIT="${SOURCE_COMMIT:-}"

usage() { echo "Usage: $0 --app <publish dir> --version <x.y.z> [--out <dir>] [--channel stable|test]"; }
die() { echo -e "\e[1;31m[✗]\e[0m $*" >&2; exit 1; }
info() { echo -e "\e[1;34m[i]\e[0m $*"; }

while (( $# )); do
  case "$1" in
    --app) APP_DIR="$(cd "$2" && pwd)"; shift ;;
    --version) VERSION="$2"; shift ;;
    --out) OUT_DIR="$2"; shift ;;
    --channel) RELEASE_CHANNEL="$2"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; die "Unknown argument '$1'." ;;
  esac
  shift
done

[[ $EUID -eq 0 ]] || die "Run as root (the template's files must keep their owners)."
[[ -f "$APP_DIR/MediaMtxPanel.dll" ]] || { usage >&2; die "--app must point to a 'dotnet publish' of the panel."; }
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "--version must look like 1.5.0 or 1.5.0-test.1."
for tool in curl tar zstd chroot; do command -v "$tool" >/dev/null || die "$tool is needed."; done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
[[ -n "$SOURCE_COMMIT" ]] || SOURCE_COMMIT="$(git -C "$PROJECT_DIR" rev-parse HEAD 2>/dev/null || true)"
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
NAME="mediamtx-panel-${VERSION}-debian-13-${ARCH}.tar.zst"

BUILD="$(mktemp -d /var/tmp/mediamtx-panel-template.XXXXXX)"
ROOT="$BUILD/rootfs"
mounted=()
cleanup() {
  local i
  for (( i=${#mounted[@]}-1; i>=0; i-- )); do umount -l "${mounted[i]}" 2>/dev/null || true; done
  rm -rf "$BUILD"
}
trap cleanup EXIT

# ---------------------------------------------------------------- base template
if [[ -z "$BASE_TEMPLATE" ]]; then
  BASE_TEMPLATE="$(curl -fsSL "$PVE_IMAGES/" | grep -oE "debian-13-standard_[0-9.]+-[0-9]+_${ARCH}\.tar\.zst" | sort -uV | tail -n1)"
  [[ -n "$BASE_TEMPLATE" ]] || die "No debian-13-standard template found at $PVE_IMAGES."
fi
info "Base: $BASE_TEMPLATE"
curl -fSL --progress-bar "$PVE_IMAGES/$BASE_TEMPLATE" -o "$BUILD/base.tar.zst"
mkdir -p "$ROOT"
tar --zstd --numeric-owner -xpf "$BUILD/base.tar.zst" -C "$ROOT"
rm -f "$BUILD/base.tar.zst"
[[ -f "$ROOT/etc/debian_version" ]] || die "$BASE_TEMPLATE does not look like a Debian root file system."

# ---------------------------------------------------------------- chroot
bind() { mkdir -p "$ROOT$1"; mount --bind "$1" "$ROOT$1"; mounted+=("$ROOT$1"); }
mount -t proc proc "$ROOT/proc"; mounted+=("$ROOT/proc")
mount -t sysfs sys "$ROOT/sys"; mounted+=("$ROOT/sys")
bind /dev
bind /dev/pts

# Name resolution while building; Proxmox writes the container's own resolv.conf when it starts.
resolv_backup=""
if [[ -e "$ROOT/etc/resolv.conf" || -L "$ROOT/etc/resolv.conf" ]]; then
  resolv_backup="$BUILD/resolv.conf"
  cp -a "$ROOT/etc/resolv.conf" "$resolv_backup"
fi
rm -f "$ROOT/etc/resolv.conf"
cp -L /etc/resolv.conf "$ROOT/etc/resolv.conf"
# Packages must not start services in the chroot.
printf '#!/bin/sh\nexit 101\n' >"$ROOT/usr/sbin/policy-rc.d"
chmod 0755 "$ROOT/usr/sbin/policy-rc.d"

install_dir="$ROOT/tmp/mediamtx-panel-install"
mkdir -p "$install_dir/app"
parts=(src deploy)
[[ -f "$PROJECT_DIR/CHANGELOG.md" ]] && parts+=(CHANGELOG.md)
tar -C "$PROJECT_DIR" --exclude='bin' --exclude='obj' --exclude='*.db' -czf "$install_dir/source.tar.gz" "${parts[@]}"
cp -a "$APP_DIR/." "$install_dir/app/"

cp "$PROJECT_DIR/deploy/proxmox/install-in-lxc.sh" "$install_dir/install-in-lxc.sh"
# Behind a proxy that inspects TLS: BUILD_CA_CERT=<pem file> is trusted while building, then removed.
proxy_env=()
for var in http_proxy https_proxy no_proxy HTTP_PROXY HTTPS_PROXY NO_PROXY; do
  [[ -n "${!var:-}" ]] && proxy_env+=("$var=${!var}")
done
if [[ -n "${BUILD_CA_CERT:-}" ]]; then
  cp "$BUILD_CA_CERT" "$ROOT/usr/local/share/ca-certificates/mediamtx-panel-build.crt"
  chroot "$ROOT" update-ca-certificates >/dev/null
  proxy_env+=("CURL_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt")
fi

info "Installing the panel $VERSION into the template…"
chroot "$ROOT" /usr/bin/env -i PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/root "${proxy_env[@]}" \
  MODE=image LOG_TAG=template PREBUILT_APP=/tmp/mediamtx-panel-install/app \
  MEDIAMTX_VERSION="$MEDIAMTX_VERSION" MEDIAMTX_URL="${MEDIAMTX_URL:-}" RELEASE_CHANNEL="$RELEASE_CHANNEL" \
  PANEL_VERSION="$VERSION" SOURCE_COMMIT="$SOURCE_COMMIT" \
  bash /tmp/mediamtx-panel-install/install-in-lxc.sh

# ---------------------------------------------------------------- clean up
info "Cleaning up…"
chroot "$ROOT" apt-get clean
rm -rf "$ROOT/tmp/"* "$ROOT/tmp/".[!.]* "$ROOT/var/tmp/"* 2>/dev/null || true
rm -rf "$ROOT/var/lib/apt/lists/"* "$ROOT/root/.dotnet" "$ROOT/root/.nuget" "$ROOT/root/.local"
rm -f "$ROOT/usr/sbin/policy-rc.d" "$ROOT/root/.bash_history"
if [[ -n "${BUILD_CA_CERT:-}" ]]; then
  rm -f "$ROOT/usr/local/share/ca-certificates/mediamtx-panel-build.crt"
  chroot "$ROOT" update-ca-certificates --fresh >/dev/null
fi
find "$ROOT/var/log" -type f -exec truncate -s 0 {} +
# Every container must get its own machine ID and SSH host keys (Proxmox makes new ones).
: >"$ROOT/etc/machine-id"
rm -f "$ROOT/var/lib/dbus/machine-id" "$ROOT/etc/ssh/ssh_host_"*
rm -f "$ROOT/etc/resolv.conf"
[[ -n "$resolv_backup" ]] && cp -a "$resolv_backup" "$ROOT/etc/resolv.conf"
cat >"$ROOT/etc/mediamtx-panel/template-version" <<VERSION
MediaMTX Panel ${VERSION} (${SOURCE_COMMIT:-unknown commit}), MediaMTX ${MEDIAMTX_VERSION}, base ${BASE_TEMPLATE}, built $(date -u +%Y-%m-%dT%H:%M:%SZ)
VERSION

for (( i=${#mounted[@]}-1; i>=0; i-- )); do umount -l "${mounted[i]}"; done
mounted=()

# ---------------------------------------------------------------- pack
info "Packing $NAME…"
tar --numeric-owner --xattrs --acls -C "$ROOT" -cf - . | zstd -q -T0 -19 -o "$OUT_DIR/$NAME" -f
( cd "$OUT_DIR" && sha256sum "$NAME" >"$NAME.sha256" )
info "Done: $OUT_DIR/$NAME ($(du -h "$OUT_DIR/$NAME" | cut -f1))"
