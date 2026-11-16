# Versiones y compilación automática

## Versión

La versión de todos los proyectos está en **`Directory.Build.props`** (`VersionPrefix`, formato `MAYOR.MENOR.PARCHE`). La primera es la **1.0.0**.

Se ve en:

- el título de la ventana de Windows (`WordGalaxy PacketBot 1.0.0`);
- la primera línea del log del servidor (`[Server] PacketBot 1.0.0`);
- el final de `/help` en Telegram.

Qué número subir:

- **PARCHE** (1.0.**1**): correcciones.
- **MENOR** (1.**1**.0): funciones nuevas.
- **MAYOR** (**2**.0.0): cambios que rompen algo, como el formato de los ajustes.

## Compilación en cada push

El workflow `build` (`.github/workflows/build.yml`) se ejecuta con cada push a `main` y en cada pull request:

- compila y prueba en Windows y en Linux, y construye la imagen de Docker;
- genera el **`.exe` de Windows**: un único `PacketBot.Windows.exe` autocontenido, que no necesita .NET instalado. Se publica como artefacto `packetbot-windows` (zip `PacketBot-Windows-<versión>-ci.<n>-win-x64.zip`) y se guarda 14 días;
- genera el servidor de Linux (artefacto `packetbot-linux`).

Los artefactos se descargan en GitHub: **Actions → build → la ejecución → Artifacts**.

El zip de Windows lleva `PacketBot.Windows.exe`, `Navigation/maps.json` y la licencia. Hay que descomprimirlo entero: el exe necesita `Navigation/maps.json` a su lado.

## Publicar una versión

Cambia `VersionPrefix` en `Directory.Build.props` y sube el commit a `main`. Nada más.

El workflow `release` (`.github/workflows/release.yml`) se ejecuta con cada push a `main`. Si la versión de `Directory.Build.props` todavía no tiene release, compila, prueba y crea la **release** de GitHub, con su etiqueta `vX.Y.Z` sobre ese commit. Si ya existe, no hace nada.

La release incluye:

- `PacketBot-Windows-<versión>-win-x64.zip`
- `PacketBot-Server-<versión>-linux-x64.tar.gz` y `…-linux-arm64.tar.gz`

Las notas se generan con los commits desde la versión anterior. Una versión con guion (por ejemplo `1.1.0-beta.1`) se marca como pre-release.

También se puede lanzar:

- **a mano**, desde **Actions → release → Run workflow**, por ejemplo si una publicación falló a medias (borra antes la release incompleta);
- **con una etiqueta** `vX.Y.Z` que coincida con `Directory.Build.props`. Si no coincide, falla.

## En local

- Windows: `pwsh deploy/publish-windows.ps1` → `artifacts/PacketBot-Windows-<versión>-win-x64.zip`
- Linux: `deploy/publish.sh linux-x64` → `artifacts/packetbot-linux-x64.tar.gz`
