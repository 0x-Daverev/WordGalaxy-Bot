# PacketBot.Server en Docker

La imagen se construye con el `Dockerfile` de la raíz del repositorio y se configura solo con variables de entorno. Los ajustes de los módulos, el registro de compras de boosters y las claves de sesión del panel web se guardan en el volumen `/data`. El panel web (opcional) escucha en el puerto 8080 del contenedor.

## Variables de entorno

| Variable | Obligatoria | Contenido |
| --- | --- | --- |
| `PACKETBOT_PHPSESSID` | Sí | PHPSESSID de wordgalaxy.space. Varias cuentas separadas por comas. |
| `PACKETBOT_TELEGRAM_TOKEN` | No | Token de BotFather. |
| `PACKETBOT_TELEGRAM_CHAT_ID` | Con el token | Chat ID numérico de tu chat privado. |
| `PACKETBOT_START` | No | `collect`, `npc` o `gg`: módulo que arranca al iniciar sesión. Vacío: solo inicia sesión y esperas a `/startbot`. |
| `PACKETBOT_FAILURE_EXIT_DELAY` | No | Segundos de espera antes de salir si no se abre ninguna cuenta. Por defecto 300 en la imagen, para que los reinicios automáticos no repitan el aviso de Telegram cada pocos segundos. |
| `PACKETBOT_WEB_PASSWORD` | No | Activa el panel web con esta contraseña (mínimo 8 caracteres; usa una larga y única). Sin ella, no hay panel. |
| `PACKETBOT_WEB_PORT` | No | Puerto del panel dentro del contenedor. Por defecto 8080. |
| `TZ` | No | Zona horaria de los logs, p. ej. `Europe/Madrid`. Por defecto UTC. |

## Docker Compose (recomendado)

En la carpeta del repositorio, crea `.env` con los secretos (no lo subas nunca al repositorio; ya está en `.gitignore`):

```bash
cat > .env <<'EOF'
PACKETBOT_PHPSESSID=<tu PHPSESSID>
PACKETBOT_TELEGRAM_TOKEN=<token de BotFather>
PACKETBOT_TELEGRAM_CHAT_ID=<tu chat id>
PACKETBOT_WEB_PASSWORD=<contraseña larga del panel>
EOF
chmod 600 .env
docker compose up -d --build
docker compose logs -f
```

El [`docker-compose.yml`](../docker-compose.yml) crea el volumen `packetbot-data` para `/data`, reinicia el contenedor si se cae y publica el panel solo en `127.0.0.1:8080`. Para arrancar un módulo al iniciar, descomenta `PACKETBOT_START`.

Para actualizar: `git pull && docker compose up -d --build`. El volumen conserva los ajustes.

## Docker sin Compose

```bash
docker build -t packetbot .
docker volume create packetbot-data
docker run -d --name packetbot --restart unless-stopped \
  --env-file .env -e TZ=Europe/Madrid \
  -v packetbot-data:/data -p 127.0.0.1:8080:8080 packetbot
docker logs -f packetbot
```

## Panel web

Con `PACKETBOT_WEB_PASSWORD`, el panel queda en `http://127.0.0.1:8080` de la máquina. Para usarlo desde fuera, publícalo con un proxy inverso con HTTPS (Caddy, nginx…) apuntando a ese puerto; no expongas el 8080 directamente a Internet. Ejemplo con Caddy, que obtiene el certificado solo:

```
packetbot.tudominio.com {
    reverse_proxy 127.0.0.1:8080
}
```

Muestra cada instancia (pestañas) con su estado, el minimapa con la nave (como en Windows: destino, NPCs, cajas, aliados, portales, barreras, objetivo y barras de vida y escudo, actualizado cada segundo), los botones Iniciar / Parar / Reiniciar TCP / Reiniciar instancia y los mismos ajustes que la ventana de Windows: módulo, mapas, cajas, objetivos, municiones, Energy Leech, zonas preferidas de NPC Killer (dibujadas en el minimapa), GG, formaciones, autocompra e invitaciones. Cada cambio se guarda al momento con las mismas reglas que `/ajuste` y avisa con un mensaje abajo. Funciona en el móvil.

Seguridad: la sesión dura 7 días (cookie `HttpOnly`, `SameSite=Strict` y `Secure` cuando el proxy indica HTTPS con `X-Forwarded-Proto`); tras 5 contraseñas erróneas en un minuto se bloquea el acceso un minuto; el panel nunca muestra el PHPSESSID ni el token. `GET /healthz` responde `ok` sin sesión, para comprobaciones de salud.

## Configurar el bot sin Windows (Telegram)

No necesitas el fichero de Windows: todo se puede configurar desde Telegram y se guarda en el volumen.

1. `/ajustes 1`: muestra los ajustes actuales, las formaciones que tiene tu nave y los códigos de booster.
2. `/ajuste 1 <clave> <valor>` cambia uno. Ejemplos:

   | Para | Comando |
   | --- | --- |
   | Collect en 1-1 | `/ajuste 1 modulo collect` · `/ajuste 1 mapa_collect 1-1` |
   | NPC Killer en 5-2 con MCB-50 | `/ajuste 1 modulo npc` · `/ajuste 1 mapa_npc 5-2` · `/ajuste 1 municion_npc mcb-50` |
   | Energy Leech en NPC Killer (opcional) | `/ajuste 1 leech si` (o `no`) |
   | GG Beta, 10 spins, solo láser | `/ajuste 1 modulo gg` · `/ajuste 1 gg beta` · `/ajuste 1 spins 10` · `/ajuste 1 ataque laser` · `/ajuste 1 municion_gg ucb-100` |
   | Formaciones | `/ajuste 1 formacion_mover <id>` · `/ajuste 1 formacion_atacar <id>` · `/ajuste 1 formacion_final <id>` (o `ninguna`) |
   | Autocompra (opcional) | `/ajuste 1 autocompra DMG_B01 si` (o `no`) |
   | Invitaciones de grupo | `/ajuste 1 invitaciones si` (o `no`) |

3. Cajas de Collect: `/cajas 1` lista los tipos (BONUS_BOX siempre; el resto aparecen al verlos en el mapa) y `/caja 1 1 3` marca o desmarca por número (`todos`, `ninguno`).
4. Objetivos de NPC Killer: `/npcs 1` y `/npc 1 1 3`. Los NPCs aparecen cuando la nave está en ese mapa: inicia NPC Killer una vez, mira `/npcs 1` y elige.
   Zonas preferidas: se dibujan en el minimapa del panel web; `/zonas 1` las lista y `/zona 1 borrar 2` (o `todas`) las borra. Ver [NPC_ZONES.md](NPC_ZONES.md).
5. `/startbot 1`.

Las formaciones, la autocompra y el Energy Leech se aplican al momento. Las cajas, los objetivos y la munición de NPC Killer cambian también con el bot en marcha. El resto se aplica en el próximo `/startbot`.

## Editar el fichero a mano (opcional)

Ejemplo con todos los campos: [`settings.example.json`](settings.example.json).

| Campo | Valores |
| --- | --- |
| `SelectedModule` | `Collect`, `NPC Killer` o `GGs` |
| `CollectMapId`, `NpcKillerMapId` | ID del mapa: 1-1 = 1, 1-2 = 2, 1-3 = 3, 1-4 = 4, 2-1 = 5 … 4-4 = 16, 1-5 = 17 … 1-8 = 20, 2-5 = 21 … 2-8 = 24, 3-5 = 25 … 3-8 = 28, 4-5 = 29, 5-1 = 91 … 5-4 = 94, 1BL = 306, 2BL = 307, 3BL = 308 |
| `CollectTypesByMap` | `{ "<id mapa>": [ "BONUS_BOX", ... ] }` |
| `NpcKillerTargetsByMap` | `{ "<id mapa>": [ "-=[ Streuner ]=-", ... ] }` (nombre exacto del juego) |
| `NpcKillerLaser`, `Laser` (GG) | `lcb-10`, `mcb-25`, `mcb-50`, `ucb-100` |
| `GalaxyGateName` | `Alpha`, `Beta`, `Gamma`, `Delta`, `Epsilon`, `Zeta`, `Kappa`, `Lambda`, `Kronos`, `Hades` |
| `Spins` | 1, 5, 10 o 100 |
| `AttackMode` | `laser`, `kami` o `both` |
| `RunFormation`, `AttackFormation`, `GgFinalNpcFormation` | ID de formación (ver `/ajustes`) o `""` |
| `AutoBuyBoosters` | `{ "2051": true }` = DMG_B01. Vacío = no compra nada. Códigos: DMG_B01 2051, DMG_B02 2052, DMG_B03 3124, HON_B01 2053, HON_B02 2054, HON50 3123, EP_B01 2055, EP_B02 2056, EP50 3122 |
| `BoosterPurchaseAttemptUtc` | Lo escribe el bot (protege contra compras repetidas). Déjalo `{}`. |
| `NpcKillerZonesByMap` | `{ "<id mapa>": [ { "X": 2000, "Y": 2000, "Width": 6000, "Height": 4000 } ] }`: zonas preferidas de NPC Killer (coordenadas del juego) |
| `NpcKillerEnergyLeech` | `true` = compra Energy Leech con NPC Killer en marcha. Ver [ENERGY_LEECH.md](ENERGY_LEECH.md) |
| `EnergyLeechAttemptUtc` | Lo escribe el bot (protege contra compras repetidas). Déjalo `null`. |
| `AutoAcceptInvites` / `AllowedInviterId` | Aceptar invitaciones de grupo / solo de ese ID (`null` = de cualquiera) |

El nombre del fichero lo muestran los logs al iniciar sesión (`Ajustes: /data/WordGalaxyPacketBot/settings/<hash>.json`). Para escribirlo en el volumen:

```bash
docker compose exec -T packetbot sh -c 'cat > /data/WordGalaxyPacketBot/settings/<hash>.json' < mis-ajustes.json
```

No hace falta reiniciar: el bot relee el fichero cuando cambia.

## Comportamiento

- **Reinicios:** con `restart: unless-stopped`, Docker vuelve a arrancar el contenedor si se para. Un error de red al iniciar sesión se reintenta durante unos 5 minutos sin reiniciar. Un PHPSESSID caducado avisa por Telegram y el contenedor sale a los 5 minutos (`PACKETBOT_FAILURE_EXIT_DELAY`).
- **Parada:** `docker compose down` o `docker stop` envían SIGTERM. El bot se para y cierra TCP en orden, en menos de 10 s.
- **Logs:** `docker compose logs -f`. Cada línea lleva la hora y la cuenta; el PHPSESSID y el token aparecen como `****`. Cada 15 minutos hay un resumen `[Estado]`.
- **Recursos:** unos 80–150 MB de RAM por proceso. Un núcleo compartido es suficiente.
