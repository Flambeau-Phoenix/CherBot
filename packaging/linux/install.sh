#!/usr/bin/env bash
# Installs CherBot from this folder (the published linux build) as a systemd service.
#
#   sudo bash ./install.sh    system service: /opt/cherbot, user "cherbot", data in /var/lib/cherbot
#   bash ./install.sh --user  user service:   ~/cherbot, data in ~/.local/share/cherbot (no root)
#
# Re-running upgrades the binary in place and restarts the service; data and config are kept.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd -P)"
MODE="system"
[[ "${1:-}" == "--user" ]] && MODE="user"

if [[ ! -x "$HERE/cherbot" ]]; then
  chmod +x "$HERE/cherbot" 2>/dev/null || { echo "cherbot binary not found next to install.sh" >&2; exit 1; }
fi

install_runtime() {
  local dest="$1"
  # A package may already be extracted into ~/cherbot or /opt/cherbot.
  if [[ "$HERE" != "$(cd "$dest" && pwd -P)" ]]; then
    install -m 0755 "$HERE/cherbot" "$dest/cherbot"
    install -m 0644 "$HERE/appsettings.json" "$dest/appsettings.json"
    for name in README.md README-install.md cherbot.example.json cherbot.service cherbot.user.service; do
      [[ ! -f "$HERE/$name" ]] || install -m 0644 "$HERE/$name" "$dest/$name"
    done
    [[ ! -f "$HERE/install.sh" ]] || install -m 0755 "$HERE/install.sh" "$dest/install.sh"
    if [[ -d "$HERE/docs" ]]; then
      mkdir -p "$dest/docs"
      cp -R "$HERE/docs/." "$dest/docs/"
    fi
  fi
}

if [[ "$MODE" == "user" ]]; then
  DEST="$HOME/cherbot"
  DATA="$HOME/.local/share/cherbot"
  mkdir -p "$DEST" "$DATA" "$HOME/.config/systemd/user"
  systemctl --user stop cherbot 2>/dev/null || true
  install_runtime "$DEST"
  [[ -f "$DATA/cherbot.json" ]] || install -m 0600 "$HERE/cherbot.example.json" "$DATA/cherbot.json"
  install -m 0644 "$HERE/cherbot.user.service" "$HOME/.config/systemd/user/cherbot.service"
  systemctl --user daemon-reload
  systemctl --user enable --now cherbot
  echo
  echo "CherBot is running as a user service."
  echo "  Config:  $DATA/cherbot.json   (then: systemctl --user restart cherbot)"
  echo "  Logs:    journalctl --user -u cherbot -f   or   $DATA/logs/"
  echo "  Token:   $DEST/cherbot token --data-dir $DATA"
  echo "  Keep running after logout: loginctl enable-linger $USER"
  exit 0
fi

if [[ $EUID -ne 0 ]]; then
  echo "System install needs root (sudo bash ./install.sh), or use bash ./install.sh --user" >&2
  exit 1
fi
id cherbot >/dev/null 2>&1 || useradd --system --home-dir /var/lib/cherbot --no-create-home --shell /usr/sbin/nologin cherbot
systemctl stop cherbot 2>/dev/null || true
mkdir -p /opt/cherbot /var/lib/cherbot
install_runtime /opt/cherbot
[[ -f /var/lib/cherbot/cherbot.json ]] || install -m 0600 -o cherbot -g cherbot "$HERE/cherbot.example.json" /var/lib/cherbot/cherbot.json
chown -R cherbot:cherbot /var/lib/cherbot
chmod 0700 /var/lib/cherbot
install -m 0644 "$HERE/cherbot.service" /etc/systemd/system/cherbot.service
systemctl daemon-reload
systemctl enable --now cherbot
echo
echo "CherBot is running as a system service."
echo "  Config:  /var/lib/cherbot/cherbot.json   (then: sudo systemctl restart cherbot)"
echo "  Logs:    journalctl -u cherbot -f   or   /var/lib/cherbot/logs/"
echo "  Token:   sudo -u cherbot DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/cherbot/.bundle /opt/cherbot/cherbot token --data-dir /var/lib/cherbot"
