# Protocolo: qué lee el bot y qué le falta

Sirve para decidir qué implementar a continuación con datos, no a ciegas. Tiene dos partes.

## 1. Registro de lo que el bot no entiende

Mientras está conectado, el bot cuenta lo que manda el servidor y él no trata (`PacketBot.Core/Services/PacketTelemetry.cs`):

- **Paquetes binarios sin tratar:** ID, veces, tamaño mínimo y máximo, en qué mapas llegan y hasta 3 muestras en hexadecimal (512 bytes como máximo cada una).
- **Comandos de texto (4224):** solo la clave, por ejemplo `n|pvp` o `A|STD`, con las veces que llega. Nunca los valores, que pueden llevar nombres de jugadores.

No guarda el PHPSESSID, el login ni nada de lo que envía el bot. Se escribe como mucho una vez por minuto y al cortarse la conexión, en un fichero por día y cuenta (`<fecha>-<id de usuario>.json`) que se va completando:

| Dónde | Carpeta |
|---|---|
| Windows | `%LOCALAPPDATA%\WordGalaxyPacketBot\telemetry\` |
| Linux | `~/.local/share/WordGalaxyPacketBot/telemetry/` |
| Docker | `/data/WordGalaxyPacketBot/telemetry/` (volumen) |

Las muestras pueden incluir nombres de otros jugadores (por ejemplo, en paquetes de chat). Revisa un fichero antes de compartirlo fuera.

## 2. Catálogo y cobertura

`tools/protocol_catalog.py` cruza tres fuentes:

1. **La exportación del cliente** (repo `WordGalaxy_main.swf`, carpeta `scripts`): los 582 comandos con su ID y campos. Para los 269 que manda el servidor, incluye el manejador del cliente, con los nombres legibles que contiene, como pista de para qué sirve.
2. **Este repo:** qué paquetes lee `PacketManager`, cuáles se escriben en `Commands/Write` y qué textos se buscan.
3. **Los registros del punto 1:** cuántas veces llega de verdad cada paquete que el bot ignora, y en qué mapas.

```
python3 -I tools/protocol_catalog.py --client ../wordgalaxy_main.swf/scripts \
    --telemetry ~/.local/share/WordGalaxyPacketBot/telemetry
```

Genera `docs/protocol/COVERAGE.md` (resumen y lista de pendientes ordenada por lo que más llega) y `docs/protocol/catalog.json` (todo, para herramientas). Sin `--telemetry`, los pendientes salen ordenados por ID.

La prueba del script es `python3 -I tools/test_protocol_catalog.py`.

## Cómo se usa para añadir funciones

1. Juega una sesión con el bot en la situación que interesa (un mapa, una GG, una muerte…).
2. Vuelve a generar la cobertura con esos registros.
3. Elige un pendiente frecuente. Su fila indica la clase del cliente y las pistas del manejador. Las muestras del registro dan bytes reales para una prueba como `EnergyLeechPacketMatchesTheGameCapture`.
4. Impleméntalo en Core con su prueba y vuelve a generar `COVERAGE.md`.
