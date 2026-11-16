# Estaciones del mapa y lo que tiene la nave

## Estaciones (paquete 13718)

Al entrar en un mapa, el servidor envía sus estaciones: la base (`HQ`), el muelle de reparación (`RepairDock`), el hangar (`Hangar`)… Cada una trae su nombre, posición, facción y tipo.

- Se dibujan en el minimapa (Windows y panel web) como un cuadrado azul con la inicial (`HQ`, `R`, `H`…).
- **Se registran** por mapa en `WordGalaxyPacketBot/maps/stations.json`, en la misma carpeta que los ajustes. El fichero es común a todas las cuentas y a Windows y el servidor. El bot conoce la base de un mapa en el que ya ha estado, aunque se haya reiniciado.

Verificado con las de 1-1: HQ en (2000, 2000), RepairDock en (2000, 920) y Hangar en (3080, 2000).

## Lo que tiene la nave (paquetes 30106 y 12754)

- **30106**, al iniciar sesión: la barra de objetos completa. Por cada objeto (municiones, cohetes, minas, extras, CPU, techs, formaciones…) trae su id, la cantidad y el máximo.
- **12754**: un objeto cuando cambia, por ejemplo al disparar o al recargar el lanzacohetes.

Se ve en:

- **Windows:** pestaña **Nave**, junto a Info, con los objetos agrupados por categoría.
- **Panel web:** tarjeta **Nave**.
- **Telegram:** `/nave N`.

El máximo solo se muestra cuando es un límite real: no se muestra si es 0, si no se pudo leer o si es 99999, que el juego usa como "sin límite".

Verificado con las actualizaciones (12754) de una sesión real: CPU de camuflaje 12, lanzacohetes HST-2 1 y 2 de 5. **Sin verificar con el 30106 real:** el registro solo guarda sus primeros 512 bytes. Si la lista de la nave no coincide con el juego al iniciar sesión, avisa.

## Lectura

El código del cliente está ofuscado, así que el orden de los campos se fijó con capturas reales y las rotaciones de bits con el `write()` del cliente. Los detalles están en los comentarios de `Commands/Read/MapStation.cs` y `Commands/Read/ItemStatus.cs`.
