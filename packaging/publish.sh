#!/usr/bin/env bash
# Publishes a self-contained single-file CherBot engine for Linux (default) or any runtime id.
#   ./packaging/publish.sh                 -> artifacts/cherbot-linux-x64
#   ./packaging/publish.sh linux-arm64
set -euo pipefail
RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${OUT:-$ROOT/artifacts/cherbot-$RID}"

dotnet publish "$ROOT/src/CherBot.Host/CherBot.Host.csproj" -c Release -r "$RID" -o "$OUT" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:DebugType=none

cp "$ROOT/packaging/README-install.md" "$ROOT/packaging/cherbot.example.json" "$OUT/"
mkdir -p "$OUT/docs"
cp -R "$ROOT/docs/." "$OUT/docs/"
case "$RID" in
  linux-*) cp "$ROOT"/packaging/linux/* "$OUT/" ;;
  win-*)   cp "$ROOT"/packaging/windows/* "$OUT/" ;;
esac
chmod +x "$OUT/cherbot" "$OUT/install.sh" 2>/dev/null || true
echo "published to $OUT"
