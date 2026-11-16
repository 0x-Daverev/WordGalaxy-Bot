# Zonas preferidas de NPC Killer

Rectángulos del mapa donde quieres que NPC Killer busque sus objetivos.

## Qué hace el bot

Con o sin zonas, **NPC Killer sigue con el NPC que está atacando hasta matarlo** (o hasta que desaparece de la vista). No cambia de objetivo porque aparezca otro más cerca.

**Selección confirmada.** El servidor confirma cada selección con el paquete 11468, que trae la vida y el escudo del NPC; el juego solo da por seleccionado el objetivo cuando llega. Mientras no llega, el minimapa muestra "sin confirmar" junto al nombre del objetivo y el bot repite la selección cada 1,5 s. Si tras 5 intentos sigue sin confirmar, escribe en el log `[Combate] El servidor no confirma la seleccion de …`. Esto vale para todos los combates (NPC Killer y Galaxy Gates).

Excepción: **un NPC que se escapa se abandona.** Si está a más de 1000 unidades y la nave no consigue acercarse al menos 100 en 4 segundos, el bot lo ignora durante 60 segundos y va a por otro. Pasa con NPCs más rápidos que la nave, que no devuelven el ataque hasta recibir el primer disparo. Un NPC lejano pero quieto no se abandona, porque la nave se va acercando.

Con zonas en el mapa de NPC Killer:

- **Objetivo nuevo:** solo uno que esté dentro de una zona. Si hay varios, el más cercano.
- **Al terminar con un NPC fuera de las zonas:** vuelve a la zona más cercana y busca allí. Por el camino ignora los NPCs que están fuera de las zonas.
- **Dentro de una zona sin objetivos a la vista:** patrulla por puntos al azar dentro de las zonas (las grandes salen más a menudo) y evita las barreras del mapa.
- El NPC que está atacando puede llevarle fuera de la zona: lo sigue hasta matarlo y luego vuelve.

Sin zonas, elige el NPC más cercano de los que ve, como siempre.

Los cambios se aplican al momento, también con el bot en marcha.

## Crear y borrar zonas

Las zonas se guardan por mapa en el fichero de ajustes de la cuenta (`NpcKillerZonesByMap`), el mismo para Windows y el servidor. Como máximo hay 12 por mapa, y cada una mide al menos 300×300.

**Ventana de Windows y panel web** (panel de NPC Killer):

1. Marca "Dibujar zonas en el minimapa". Mientras está marcada, el minimapa no mueve la nave.
2. Arrastra en el minimapa para crear una zona (se ve en verde y numerada).
3. Haz clic (o toca, en el móvil) dentro de una zona para borrarla.
4. "Borrar zonas del mapa" borra todas.

Se dibujan sobre el mapa en el que está la nave en ese momento.

**Telegram** (zonas del mapa de NPC Killer):

- `/zonas N`: lista numerada.
- `/zona N borrar 2` o `/zona N borrar todas`.
