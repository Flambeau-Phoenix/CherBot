#!/usr/bin/env bash
# Publishes a self-contained single-file CherBot engine for Linux (default) or any runtime id.
#   ./packaging/publish.sh                 -> artifacts/cherbot-linux-x64
#   ./packaging/publish.sh linux-arm64
set -euo pipefail
RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${OUT:-$ROOT/artifacts/cherbot-$RID}"

assert_public_package() {
  if [[ -d "$OUT" ]] && [[ -n "$(find "$OUT" \( -name api-token -o -name cherbot.json -o -name keys -o -name logs -o -name profiles.json -o -name '.env' -o -name '.env.*' -o -iname '*.db' -o -iname '*.db-shm' -o -iname '*.db-wal' -o -iname '*.sqlite*' -o -iname '*.pem' -o -iname '*.key' -o -iname '*.pfx' -o -iname '*.p12' \) -print -quit)" ]]; then
    echo 'Refusing to package runtime data or credentials. Use a fresh OUT directory.' >&2
    exit 1
  fi
}
assert_public_package

dotnet publish "$ROOT/src/CherBot.Host/CherBot.Host.csproj" -c Release -r "$RID" -o "$OUT" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:DebugType=none

cp "$ROOT/packaging/README-install.md" "$ROOT/packaging/cherbot.example.json" "$ROOT/CHANGELOG.md" "$OUT/"
mkdir -p "$OUT/docs"
cp -R "$ROOT/docs/." "$OUT/docs/"
case "$RID" in
  linux-*) for name in install.sh cherbot.service cherbot.user.service; do cp "$ROOT/packaging/linux/$name" "$OUT/"; done ;;
  win-*)   cp "$ROOT"/packaging/windows/* "$OUT/" ;;
esac
chmod +x "$OUT/cherbot" "$OUT/install.sh" 2>/dev/null || true
assert_public_package
echo "published to $OUT"
