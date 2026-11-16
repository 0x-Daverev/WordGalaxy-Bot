# Energy Leech en NPC Killer

Opción del módulo NPC Killer, desactivada por defecto. Con ella activada, el bot compra Energy Leech mientras NPC Killer está en marcha. El Energy Leech dura 30 minutos y luego tiene 5 minutos de enfriamiento.

Se activa con la casilla "Comprar Energy Leech" (ventana de Windows y panel web) o con `/ajuste N leech si` en Telegram. Se aplica al momento, sin reiniciar el bot.

## Paquete de compra

Es la acción de la barra de objetos (ID 18889) con `tech_energy-leech`, el mismo paquete que el juego envía al pulsarlo:

```
00 1b  49 c9  00 01  00 01  00 11 "tech_energy-leech"  55 a2
largo  18889                UTF                         21922
```

## Cómo sabe el bot cuánto queda

El servidor envía el temporizador de cada objeto de la barra en el paquete **31208**: id del objeto, estado (0 listo, 1 activo, 2 enfriamiento), milisegundos que quedan, máximo y si se puede activar. Es el mismo paquete que el bot ya usaba para el kamikaze del PET. Ahora se guardan todos en `User.ItemTimers`.

Para decidir cuándo comprar:

1. Si llegó un temporizador de `tech_energy-leech` después del último intento, manda él:
   - activo → listo cuando termine más 5 min de enfriamiento;
   - enfriamiento → listo cuando termine;
   - listo → compra si se puede activar; si no se puede, no hace nada.
2. Si no, se estima: 35 minutos (30 + 5) después del último intento.
3. Si nunca se ha comprado, compra al empezar.

El momento del intento se guarda en `EnergyLeechAttemptUtc` antes de enviar el paquete. Así, ni un reinicio ni una reconexión pueden comprarlo dos veces en el mismo ciclo. Nunca hay más de un intento por minuto.

Cada compra escribe en el log `[NPC Killer] Energy Leech comprado.`.

## Pendiente de comprobar en el juego

- Si el servidor envía el 31208 de `tech_energy-leech` al comprarlo y al iniciar sesión. Si no lo envía, el bot sigue con la estimación de 35 minutos.
- Que el paquete compra (y no solo activa uno ya comprado).
