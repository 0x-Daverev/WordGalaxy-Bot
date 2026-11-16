# Barreras de mapa (zonas POI)

Algunos mapas (por ejemplo 2BL) tienen barreras que la nave no puede atravesar. No están en el cliente: el servidor las envía al entrar en el mapa como zonas "POI".

## Paquetes

| ID | Qué hace | Clase |
|---|---|---|
| 7240 | Añade una zona | `Commands/Read/MapAddPoi.cs` |
| 27912 | Borra la zona con ese id | `MapRemovePoi` (mismo fichero) |

El 7240 lleva: forma (0 círculo, 1 polígono, 2 rectángulo), tipo (subpaquete 29441), diseño (subpaquete 20767), activa, invertida, typeKey, id, coordenadas y un short de relleno. Cada coordenada viene rotada (el cliente escribe `v >>> 7 | v << 25`). En los círculos las coordenadas son `x, y, radio`; en el resto, pares `x, y`.

El código de lectura del cliente está ofuscado, así que solo es seguro que la forma va primero. El resto del orden lo encuentra el decodificador, que prueba los campos en cada posición y se queda con la combinación que consume el paquete entero con valores válidos. Si ninguna encaja, el paquete se ignora.

## Qué bloquea

Solo bloquean las zonas de tipo *No Access* (tipos 5 y 6). El resto (radiación, daño, curación, zona segura…) no afecta al movimiento.

Igual que el cliente:

- Un polígono o rectángulo choca como su caja envolvente (rectángulo alineado con los ejes).
- Zona normal: no se puede entrar.
- Zona invertida: lo bloqueado es una franja exterior de 512 unidades (384 en círculos). Si la zona es grande, la nave queda encerrada dentro. Si es una línea o un punto (caja `600x0`, `0x0`…), es un muro de 1024 de grosor o un bloque de 1024×1024: así llegan las barreras de 2BL (mapa 307).
- Si la nave ya está dentro de una zona, puede salir.
- Un movimiento que choca se detiene 40 unidades antes del borde.

Este servidor envía las zonas sin id (`''`) y las vuelve a enviar todas cada vez que la nave reaparece en el mismo mapa. Una zona sin id se identifica por su tipo, forma y puntos: si llega otra vez, sustituye a la que había en lugar de acumularse. Un borrado (27912) con id vacío se ignora, porque no se sabe a cuál se refiere.

Las zonas se borran al cambiar de mapa. En el mismo mapa (reconexión, nueva oleada de GG) el servidor las vuelve a enviar y sustituyen a las anteriores por id.

## Movimiento del bot

Todos los movimientos (módulos y clic en el minimapa) pasan por `MapZones.PlanMove`:

1. Si el destino cae dentro de una barrera, se mueve justo fuera.
2. Si el camino recto está libre, se va directo.
3. Si no, se busca el camino más corto por las esquinas de las barreras (de su franja, si es invertida, con 80 unidades de margen y dentro del mapa) y se vuela a la primera esquina. Al llegar, el módulo vuelve a pedir el destino y se sigue a la siguiente.
4. Si no hay forma de rodearla, el bot se detiene donde lo haría el juego.

Las barreras se dibujan en rojo en el minimapa de Windows y en el panel web; en las invertidas se rellena la franja bloqueada.

## Comprobar en el juego

Las zonas no se escriben en el log. Para comprobarlas, compara el minimapa (Windows o panel web) con el juego. Verificado en 2BL (mapa 307): 24 zonas sin id, todas líneas o puntos invertidos.
