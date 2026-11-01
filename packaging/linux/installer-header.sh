#!/usr/bin/env bash
# CherBot @VERSION@ offline installer (@RID@). The compressed release follows this script.
set -euo pipefail

usage() {
  cat <<'HELP'
CherBot @VERSION@ for @RID@

Install a user service:   bash @STEM@.run --user
Install a system service: sudo bash @STEM@.run
Extract without install:  bash @STEM@.run --extract ./cherbot-unpacked

The .NET runtime, Web UI, configuration template, and documentation are bundled.
Service installation requires systemd. Extraction does not install a service.
HELP
}

mode=system
destination=''
case "${1:-}" in
  --help|-h) [[ $# == 1 ]] || { usage >&2; exit 2; }; usage; exit 0 ;;
  --user) [[ $# == 1 ]] || { usage >&2; exit 2; }; mode=user ;;
  --extract) [[ $# == 2 && -n "$2" ]] || { usage >&2; exit 2; }; mode=extract; destination="$2" ;;
  '') [[ $# == 0 ]] || { usage >&2; exit 2; } ;;
  *) usage >&2; exit 2 ;;
esac

if [[ "$mode" != extract ]]; then
  [[ "$(uname -s)" == Linux ]] || { echo 'Service installation requires Linux.' >&2; exit 1; }
  case '@RID@':"$(uname -m)" in
    linux-x64:x86_64|linux-x64:amd64|linux-arm64:aarch64|linux-arm64:arm64) ;;
    *) echo 'This installer is for @RID@; download the build for your CPU.' >&2; exit 1 ;;
  esac
  if [[ "$mode" == system && $EUID != 0 ]]; then
    echo 'Use sudo for a system service, or pass --user for a user service.' >&2
    exit 1
  fi
  command -v systemctl >/dev/null || { echo 'Service installation requires systemd. Use --extract for console use.' >&2; exit 1; }
fi
for command_name in tar sha256sum tail mktemp; do
  command -v "$command_name" >/dev/null || { echo "Required command missing: $command_name" >&2; exit 1; }
done

temp_base="${TMPDIR:-/tmp}"
work_dir="$(mktemp -d "$temp_base/cherbot-installer.XXXXXXXX")"
cleanup() {
  case "$work_dir" in
    "$temp_base"/cherbot-installer.*) rm -rf -- "$work_dir" ;;
    *) echo 'Refusing cleanup outside the installer temporary directory.' >&2 ;;
  esac
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
archive="$work_dir/release.tar.gz"
tail -n +@PAYLOAD_LINE@ "$0" > "$archive"
actual_hash="$(sha256sum "$archive")"
actual_hash="${actual_hash%% *}"
if [[ "$actual_hash" != '@PAYLOAD_SHA256@' ]]; then
  echo 'Installer payload checksum failed. Download the installer again.' >&2
  exit 1
fi

if [[ "$mode" == extract ]]; then
  [[ ! -e "$destination" ]] || { echo 'Extraction destination already exists; choose a new directory.' >&2; exit 1; }
  mkdir -p -- "$destination"
  tar --extract --gzip --file="$archive" --directory="$destination" --no-same-owner
  printf 'Extracted CherBot to %s/@STEM@\n' "$destination"
else
  tar --extract --gzip --file="$archive" --directory="$work_dir" --no-same-owner
  if [[ "$mode" == user ]]; then
    bash "$work_dir/@STEM@/install.sh" --user
  else
    bash "$work_dir/@STEM@/install.sh"
  fi
fi
exit 0
__CHERBOT_ARCHIVE_BELOW__
