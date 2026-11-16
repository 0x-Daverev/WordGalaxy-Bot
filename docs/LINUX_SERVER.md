# PacketBot.Server — uso en Linux

Servidor sin interfaz gráfica que comparte el motor (`PacketBot.Core`) con la versión Windows: Collect, NPC Killer, Galaxy Gates, formaciones, boosters, Telegram y panel web.

## Requisitos

- .NET 8 SDK solo para compilar. El paquete publicado (ver «Despliegue») no necesita .NET.
- `libicu` instalada (formato de números en español de `/status` y `/rank`).

## Configuración

Los secretos van en variables de entorno, nunca en la línea de comandos ni en el repositorio:

| Variable | Obligatoria | Contenido |
| --- | --- | --- |
| `PACKETBOT_PHPSESSID` | Sí | PHPSESSID de wordgalaxy.space. Varias cuentas separadas por comas. |
| `PACKETBOT_TELEGRAM_TOKEN` | No | Token de BotFather. |
| `PACKETBOT_TELEGRAM_CHAT_ID` | Con el token | Chat ID numérico del chat **privado** autorizado. |
| `PACKETBOT_WEB_PASSWORD` | No | Activa el panel web (puerto `PACKETBOT_WEB_PORT`, 8080). HTTP sin cifrar: publícalo solo detrás de un proxy HTTPS. Ver [`DOCKER.md`](DOCKER.md#panel-web). |

Fichero recomendado, solo legible por tu usuario:

```bash
mkdir -p ~/.config/packetbot
cat > ~/.config/packetbot/env <<'EOF'
PACKETBOT_PHPSESSID=<tu PHPSESSID>
PACKETBOT_TELEGRAM_TOKEN=<token de BotFather>
PACKETBOT_TELEGRAM_CHAT_ID=<tu chat id>
EOF
chmod 600 ~/.config/packetbot/env
```

## Ajustes de los módulos

`/startbot N` vuelve a arrancar el último módulo usado en la sesión (por ejemplo, Collect si se inició con `--start collect`). Si todavía no se ha iniciado ninguno, usa el módulo guardado en los ajustes de la cuenta (`SelectedModule`). El mapa, los tipos de caja / NPC y la munición de NPC Killer (`NpcKillerLaser`: `lcb-10`, `mcb-25`, `mcb-50` o `ucb-100`; independiente de la de Galaxy Gates, `Laser`) salen siempre de esos ajustes. Galaxy Gates usa `GalaxyGateName` (p. ej. `Beta`), `Spins` (1, 5, 10 o 100), `Laser`, `AttackMode` (`laser`, `kami` o `both`) y `GgFinalNpcFormation`. Es el mismo fichero que escribe Windows, con el mismo nombre:

- Windows: `%LOCALAPPDATA%\WordGalaxyPacketBot\settings\<hash>.json`
- Linux: `~/.local/share/WordGalaxyPacketBot/settings/<hash>.json`

Formaciones (mismos ajustes que Windows): `RunFormation` al moverse y `AttackFormation` al atacar, solo con el bot en marcha y si la nave tiene esa formación; en la última oleada de GG manda `GgFinalNpcFormation`. Boosters: el servidor consulta su estado cada minuto; la autocompra solo actúa con los boosters marcados en `AutoBuyBoosters` (ninguno por defecto), cuando les queda menos de 6 h, como mucho una vez cada 6 h − 5 min por booster, y guarda el intento en `BoosterPurchaseAttemptUtc` antes de comprar para no repetir la compra tras errores o reinicios.

Configura el módulo en Windows y copia el fichero. El servidor imprime la ruta exacta al iniciar sesión. Se vuelve a leer en cada `/startbot`, así que no hace falta reiniciar tras cambiarlo.

## Ejecutar

```bash
set -a; . ~/.config/packetbot/env; set +a
dotnet run --project PacketBot.Server -c Release                    # Telegram controla el bot
dotnet run --project PacketBot.Server -c Release -- --start collect # además arranca Collect
dotnet run --project PacketBot.Server -c Release -- --start npc     # además arranca NPC Killer
dotnet run --project PacketBot.Server -c Release -- --start gg      # además arranca Galaxy Gates
dotnet run --project PacketBot.Server -c Release -- --check         # solo comprueba el login
```

Ctrl+C o `SIGTERM` paran los módulos y cierran las sesiones en orden.

Si al arrancar falla la red (VPS recién encendido, servidor del juego reiniciándose), cada cuenta se reintenta 8 veces durante unos 5 minutos. Un PHPSESSID rechazado no se reintenta. Si una cuenta no se puede abrir, se avisa por Telegram. Si no se abre ninguna, el proceso sale con código 1.

## Despliegue en un VPS con systemd

¿Prefieres Docker? Ver [`DOCKER.md`](DOCKER.md).

En tu PC (o descarga el artefacto `packetbot-linux` de la última ejecución de GitHub Actions):

```bash
deploy/publish.sh linux-x64        # o linux-arm64 (p. ej. Oracle Ampere, Raspberry Pi 64 bits)
scp artifacts/packetbot-linux-x64.tar.gz usuario@vps:
```

En el VPS (Debian/Ubuntu):

```bash
sudo apt install -y libicu-dev     # formato de números en español
tar xzf packetbot-linux-x64.tar.gz && cd packetbot-linux-x64
sudo ./install.sh "$USER"          # copia a /opt/packetbot y crea ~/.config/packetbot/env (600)
nano ~/.config/packetbot/env       # PHPSESSID, Telegram y, si quieres, PACKETBOT_ARGS=--start collect
sudo systemctl enable --now packetbot@"$USER"
```

Copia también tus ajustes de Windows a `~/.local/share/WordGalaxyPacketBot/settings/`. El servicio corre con tu usuario, no como root, y solo puede escribir en esa carpeta.

Uso diario:

| Acción | Comando |
| --- | --- |
| Estado | `systemctl status packetbot@$USER` |
| Logs en directo | `journalctl -u packetbot@$USER -f` |
| Solo errores | `journalctl -u packetbot@$USER -p err` |
| Reiniciar (p. ej. tras cambiar el env) | `sudo systemctl restart packetbot@$USER` |
| Parar / no arrancar al encender | `sudo systemctl disable --now packetbot@$USER` |
| Actualizar | publicar de nuevo, copiar y `sudo ./install.sh "$USER"` |

Si el proceso cae, `systemd` lo vuelve a arrancar a los 30 s, como mucho 5 veces en 10 minutos. Un error de configuración (código 2, p. ej. falta el PHPSESSID) no se reintenta. Al reiniciar el VPS arranca solo. El bot no se pone en marcha salvo que `PACKETBOT_ARGS` incluya `--start`.

## Logs y supervisión

- Cada línea lleva la hora y la cuenta (`[123456]`). journald recibe su prioridad, así que `-p err` muestra solo errores.
- El PHPSESSID y el token de Telegram nunca aparecen: se sustituyen por `****`.
- Cada 15 minutos se escribe un resumen por cuenta: `[Estado] TCP conectado | Bot Collect en marcha | Ultimo paquete: hace 1 s | Reconexiones TCP: 2 | ...`.
- `/status` muestra lo mismo: último paquete recibido, reconexiones TCP, última desconexión y último error.
- Si Telegram no responde, se registra el primer error, uno de cada diez y la recuperación. Las esperas crecen hasta 60 s.

## Telegram

Los mismos comandos que en Windows: `/status`, `/startbot N`, `/stopbot N`, `/tcp N`, `/restart N`, `/phpsessid N`, `/rank N`, `/npcs N` (mapa, munición y lista numerada de NPCs de NPC Killer), `/npc N <numeros|todos|ninguno>` (marca o desmarca objetivos; se guarda en los ajustes y se aplica al bot en marcha), `/nave N` (municiones, extras y techs de la nave, ver [`SHIP_AND_STATIONS.md`](SHIP_AND_STATIONS.md)), `/zonas N` y `/zona N borrar <numero|todas>` (zonas preferidas de NPC Killer, ver [`NPC_ZONES.md`](NPC_ZONES.md)), `/help`. Solo en el servidor: `/ajustes N`, `/ajuste N <clave> <valor>`, `/cajas N` y `/caja N <numeros|todos|ninguno>` para configurarlo todo sin Windows (ver [`DOCKER.md`](DOCKER.md#configurar-el-bot-sin-windows-telegram); ejemplo de fichero: [`settings.example.json`](settings.example.json)). Solo responde al chat privado configurado. Solo avisa cuando hace falta intervenir: si el bot se detiene sin que nadie lo haya pedido (el módulo terminó, falló o no se pudo recuperar TCP al iniciar) o si TCP sigue caído más de 1 minuto con el bot activo; en ese caso avisa también cuando se recupera. Las reconexiones cortas y `/stopbot` no generan avisos.

