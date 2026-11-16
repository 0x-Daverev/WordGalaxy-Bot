# Normas del proyecto PacketBot

## Estructura

- `PacketBot.Core` (`net8.0`): toda la lógica del bot. No puede depender de `System.Windows.Forms`, `System.Drawing` ni APIs exclusivas de Windows (lo comprueba `CorePortabilityTests`).
- `PacketBot.Windows`: solo interfaz (vista y configuración). Cualquier lógica nueva va en Core.
- `PacketBot.Server`: servidor sin interfaz para Linux y Docker (Telegram y panel web).
- Una función se implementa una sola vez en Core y la usan Windows y el servidor. No duplicar lógica por plataforma.

## Comportamientos que no se pueden romper

- Reconexión TCP automática **solo con el bot activo**; si se inicia con TCP caído, reconectar antes de trabajar; al parar, cesar los reintentos.
- Módulos Collect, NPC Killer y Galaxy Gates con sus ajustes, comandos y navegación, iguales en Windows y en el servidor.
- Compras de boosters siempre opt-in y sin compras duplicadas por reintentos o reinicios (el intento se guarda antes del POST).
- Telegram solo responde al chat privado autorizado; los comandos son los mismos en Windows y en el servidor.
- El fichero de ajustes de cada cuenta es el mismo en Windows y en el servidor (`WordGalaxyPacketBot/settings/<hash>.json`).

## Seguridad

- No escribir PHPSESSID, tokens de Telegram ni contraseñas en logs, pruebas, documentación ni respuestas del panel web.
- No pedir nunca al usuario que pegue su PHPSESSID en un chat.
- El panel web va siempre detrás de un proxy HTTPS y con contraseña.

## Protocolo y funciones nuevas

- Todo paquete nuevo se basa en la exportación del cliente (`WordGalaxy_main.swf`) y lleva una prueba. No se envía al juego ningún paquete nuevo sin una captura real que lo respalde, con una prueba byte a byte.
- Las funciones nuevas que actúan en el juego (compras, combate, movimiento nuevo) van desactivadas por defecto.
- Tras leer o escribir un paquete nuevo, regenerar `docs/protocol/` con `tools/protocol_catalog.py` (ver `docs/PROTOCOL.md`) y ejecutar `python3 -I tools/test_protocol_catalog.py` si se toca el script.
- El registro de paquetes (`PacketTelemetry`) nunca guarda valores de los comandos de texto, ni el login, ni lo que envía el bot.

## Cambios

- Commits pequeños y descriptivos en `main`.
- La versión está solo en `Directory.Build.props`. Subir a `main` un número de versión sin release **la publica** (workflow `release`, ver `docs/RELEASES.md`). No cambiar la versión, ni crear etiquetas o releases, sin que lo pida el usuario.
- Compilar y ejecutar las pruebas antes de dar algo por terminado (`dotnet build PacketBot.sln`, `dotnet test PacketBot.sln`; en Linux, `-p:EnableWindowsTargeting=true`). Lo que no se haya probado se indica como **No ejecutada**.
- No afirmar que algo funciona en Linux sin compilarlo y probarlo en Linux.
- Mantener la documentación de `docs/` al día con el comportamiento real.
