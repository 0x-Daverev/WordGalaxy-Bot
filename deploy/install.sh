#!/usr/bin/env bash
# Installs (or updates) a published PacketBot.Server as packetbot@<usuario>.
# Run from the extracted folder: sudo ./install.sh <usuario>
set -euo pipefail
user="${1:-${SUDO_USER:-}}"
if [ -z "$user" ] || [ "$user" = root ]; then echo "Uso: sudo ./install.sh <usuario> (no root)" >&2; exit 2; fi
[ "$(id -u)" -eq 0 ] || { echo "Ejecuta con sudo." >&2; exit 2; }
home="$(getent passwd "$user" | cut -d: -f6)"
[ -n "$home" ] || { echo "No existe el usuario $user." >&2; exit 2; }
if [ "$home" != "/home/$user" ]; then
  echo "Aviso: el servicio espera /home/$user; edita packetbot@.service si el home es $home." >&2
fi
here="$(cd "$(dirname "$0")" && pwd)"
systemctl stop "packetbot@$user" 2>/dev/null || true
install -d -m 755 /opt/packetbot /opt/packetbot/Navigation
install -m 755 "$here/PacketBot.Server" /opt/packetbot/
install -m 644 "$here/Navigation/maps.json" /opt/packetbot/Navigation/
install -m 644 "$here/packetbot@.service" /etc/systemd/system/
sudo -u "$user" mkdir -p "$home/.config/packetbot" "$home/.local/share/WordGalaxyPacketBot/settings"
if [ ! -f "$home/.config/packetbot/env" ]; then
  sudo -u "$user" install -m 600 "$here/env.example" "$home/.config/packetbot/env"
  echo "Creado $home/.config/packetbot/env: rellenalo y ejecuta: sudo systemctl enable --now packetbot@$user"
else
  chmod 600 "$home/.config/packetbot/env"
fi
systemctl daemon-reload
if systemctl is-enabled --quiet "packetbot@$user" 2>/dev/null; then
  systemctl start "packetbot@$user"
  echo "Actualizado y reiniciado packetbot@$user."
fi
echo "Logs: journalctl -u packetbot@$user -f"
