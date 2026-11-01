#!/usr/bin/env bash
# Native Linux smoke check; no service installation, no linked accounts.
set -euo pipefail
release_dir="$(cd "${1:?Pass the release output directory}" && pwd -P)"
installers=("$release_dir"/*-linux-x64.run)
[[ ${#installers[@]} == 1 && -f "${installers[0]}" ]] || { echo 'Expected one Linux x64 installer' >&2; exit 1; }
installer="${installers[0]}"
stem="$(basename "$installer" .run)"
(cd "$release_dir" && sha256sum -c "$stem.sha256")
task_dir="$(mktemp -d "$release_dir/smoke.XXXXXXXX")"
engine_pid=''
cleanup() {
  if [[ -n "$engine_pid" ]]; then
    kill "$engine_pid" 2>/dev/null || true
    wait "$engine_pid" 2>/dev/null || true
  fi
  case "$task_dir" in
    "$release_dir"/smoke.*) rm -rf -- "$task_dir" ;;
    *) echo 'Refusing cleanup outside the smoke test directory' >&2 ;;
  esac
}
trap cleanup EXIT
bash "$installer" --help
bash "$installer" --extract "$task_dir/unpacked"
engine="$task_dir/unpacked/$stem/cherbot"
[[ -x "$engine" ]]
"$engine" --data-dir "$task_dir/data" --CherBot:Api:Port 18095 \
  --CherBot:WebSocketServer:Enabled false --CherBot:Engine:SoundMode none > "$task_dir/engine.log" 2>&1 &
engine_pid=$!
for attempt in {1..40}; do
  if curl --silent --fail http://127.0.0.1:18095/health > "$task_dir/health.json"; then break; fi
  if ! kill -0 "$engine_pid" 2>/dev/null; then cat "$task_dir/engine.log"; exit 1; fi
  sleep 0.5
done
curl --silent --show-error --fail http://127.0.0.1:18095/health
curl --silent --show-error --fail http://127.0.0.1:18095/assets/cherbot.svg > /dev/null
curl --silent --show-error --fail http://127.0.0.1:18095/ | grep -q 'brand-logo'
[[ -f "$task_dir/data/cherbot.json" ]]
echo 'Linux release starts headlessly and serves its embedded Web UI and logo.'
