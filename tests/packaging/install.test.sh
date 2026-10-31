#!/usr/bin/env bash
# Exercise the real copy helper without invoking systemd or touching an install.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
source <(sed -n '/^install_runtime()/,/^}/p' "$ROOT/packaging/linux/install.sh")
mkdir -p "$ROOT/artifacts"
task_dir="$(mktemp -d "$ROOT/artifacts/install-test.XXXXXX")"
task_dir="$(cd "$task_dir" && pwd -P)"
cleanup() {
  case "$task_dir" in
    "$ROOT"/artifacts/install-test.*) rm -rf -- "$task_dir" ;;
    *) echo 'Refusing cleanup outside the test directory' >&2; exit 1 ;;
  esac
}
trap cleanup EXIT
mkdir "$task_dir/source" "$task_dir/destination"
printf '#!/bin/sh\nexit 0\n' > "$task_dir/source/cherbot"
printf '{"test":true}\n' > "$task_dir/source/appsettings.json"
HERE="$task_dir/source"
install_runtime "$task_dir/destination"
cmp "$HERE/cherbot" "$task_dir/destination/cherbot"
cmp "$HERE/appsettings.json" "$task_dir/destination/appsettings.json"
[[ -x "$task_dir/destination/cherbot" ]]
HERE="$task_dir/destination"
install_runtime "$HERE"
cmp "$task_dir/source/cherbot" "$HERE/cherbot"
# Canonical paths must also work when the package directory is a symlink.
ln -s "$HERE" "$task_dir/link"
HERE="$(cd "$task_dir/link" && pwd -P)"
install_runtime "$task_dir/link"
echo 'Installer copy tests passed (separate, in-place, and symlink paths).'
