#!/usr/bin/env bash
# Builds a self-contained PacketBot.Server for Linux: the VPS needs no .NET
# installed, only libicu. Usage: deploy/publish.sh [linux-x64|linux-arm64]
# Result: artifacts/packetbot-<rid>.tar.gz
set -euo pipefail
rid="${1:-linux-x64}"
case "$rid" in linux-x64|linux-arm64) ;; *) echo "RID no soportado: $rid (linux-x64 o linux-arm64)" >&2; exit 2;; esac
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/packetbot-$rid"
rm -rf "$out"
dotnet publish "$root/PacketBot.Server/PacketBot.Server.csproj" -c Release -r "$rid" \
  --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o "$out"
cp "$root/deploy/packetbot@.service" "$root/deploy/install.sh" "$root/deploy/env.example" "$out/"
chmod +x "$out/PacketBot.Server" "$out/install.sh"
tar -C "$root/artifacts" -czf "$root/artifacts/packetbot-$rid.tar.gz" "packetbot-$rid"
echo "Listo: artifacts/packetbot-$rid.tar.gz"
