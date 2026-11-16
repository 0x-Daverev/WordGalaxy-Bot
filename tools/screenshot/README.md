# Captura del README

`PacketBot.Screenshot` abre la **ventana real** de PacketBot (`UserWindow`) con una partida de ejemplo, sin conectarse al juego:

- mapa 2BL con sus 24 barreras, tal como las manda el servidor;
- NPCs de BL y cajas;
- dos zonas preferidas;
- la nave atacando a un Kristallon.

La guarda como PNG. No forma parte de `PacketBot.sln`, así que la CI no la compila.

## Windows

```
dotnet run --project tools/screenshot -- docs/images/packetbot-windows.png minimap-307-700.jpg
```

El segundo argumento es la imagen del minimapa de 2BL (`https://wordgalaxy.space/spacemap/graphics/minimaps/minimap-307-700.jpg`). Sin él, el fondo sale negro.

## Linux (Wine)

Así se hizo la imagen actual:

```
apt-get install wine64 xvfb
dotnet publish tools/screenshot -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true -o /tmp/pbshot
Xvfb :99 -screen 0 1400x900x24 &
DISPLAY=:99 wine /tmp/pbshot/PacketBot.Screenshot.exe 'Z:\tmp\shot.png' 'Z:\tmp\minimap-307-700.jpg'
```

Con Wine, las fuentes son DejaVu en lugar de Segoe UI, así que el texto se ve un poco distinto que en Windows.

## Cabecera

`docs/images/packetbot-banner.jpg` es la captura al doble de tamaño sobre un fondo con el nombre, una descripción y la versión. Se hizo con Pillow (Python). Al cambiar de versión, hay que regenerarla para que el número coincida.
