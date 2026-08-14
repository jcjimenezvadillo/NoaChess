# Threat features: qué son y qué haría falta para tenerlas

Leído del código de la referencia el 2026-08-12. Este documento es el trabajo
previo, no la implementación: describe la codificación exacta para que quien la
escriba no tenga que volver a descifrarla.

## Por qué esto y no otra cosa

Tres ejes están medidos y los tres dicen lo mismo:

| eje | medido |
|---|---|
| mejores etiquetas | **+22,1** [+6,8, +35,9] |
| más datos a igual cómputo | **+182** |
| **más capacidad** (ancho 256) | **−30,3**, H0 |

Ensanchar la red **pierde**. Lo que falta no es sitio para la misma información:
es información nueva. Las amenazas son el único eje pendiente que añade
**contenido de evaluación** en vez de capacidad.

| | Noa (fq60) | referencia |
|---|---|---|
| HalfKA | 22.528 dims, 32 activas | **idéntico** |
| amenazas | **nada** | **60.720 dims, 128 activas** |

Nuestro HalfKA coincide exactamente con el suyo, así que esto no es corregir lo
que hay: es una entrada entera que la red nunca ha tenido.

## Qué codifica una característica

Una tupla **(pieza atacante, casilla origen, casilla destino, pieza atacada)**,
orientada por el rey de la perspectiva. Es decir: *qué pieza ataca a cuál, desde
dónde y hacia dónde*. La red hoy tiene que deducir eso de las posiciones sueltas.

## La orientación, idéntica a la de HalfKA

```cpp
const i8 orientation = OrientTBL[ksq] ^ (56 * perspective);
unsigned from_oriented = u8(from) ^ orientation;
unsigned to_oriented   = u8(to)   ^ orientation;
```

`OrientTBL` vale `SQ_A1` en los ficheros a-d y `SQ_H1` en los e-h, o sea **espeja
en horizontal según el fichero del rey**. El `^ 56 * perspective` voltea el
tablero para las negras. Es el mismo esquema que ya tenemos en HalfKAv2_hm, así
que esta parte se reutiliza tal cual.

Las piezas también se orientan: `attacker ^ (8 * perspective)`, que intercambia
los colores para la perspectiva negra.

## Qué pares (atacante, atacado) se registran

No todos. `map[6][6]`, con −1 = no se registra:

|  | P | N | B | R | Q | K |
|---|---|---|---|---|---|---|
| **P** | 0 | 1 | − | 2 | − | − |
| **N** | 0 | 1 | 2 | 3 | 4 | − |
| **B** | 0 | 1 | 2 | 3 | − | − |
| **R** | 0 | 1 | 2 | 3 | − | − |
| **Q** | 0 | 1 | 2 | 3 | 4 | − |
| **K** | − | − | − | − | − | − |

Léase: un **peón** solo registra amenazas contra peón, caballo y torre. Un
**alfil** no registra contra dama. El **rey** no registra nada.

Y `numValidTargets = {0, 6, 10, 8, 8, 10, 0, 0}` cuadra exactamente con el doble
del número de entradas válidas de cada fila: peón 3 pares × 2 = 6, caballo 5 × 2
= 10, alfil 4 × 2 = 8, torre 4 × 2 = 8, dama 5 × 2 = 10, rey 0.

**El factor 2 es el bit de dirección** `from_oriented < to_oriented`, que
distingue amenazar "hacia arriba" de "hacia abajo" del tablero.

## El índice

```cpp
return index_lut1[attacker][attacked][from < to]   // base del par + direccion
     + offsets[attacker][from]                     // desplazamiento por origen
     + index_lut2[attacker][from][to];             // cual de los destinos posibles
```

Los `offsets` se construyen acumulando, para cada pieza y cada casilla de origen,
**cuántas casillas ataca desde ahí**:

- piezas que no son peón: `popcount(PseudoAttacks[tipo][from])`
- peones: `popcount(PawnPushOrAttacks[color][from])`, y **solo desde las filas 2
  a 7**

Nótese `PawnPushOrAttacks`: para los peones se cuentan **los avances además de
las capturas**, no solo las capturas.

## Coste real de implementarlo

**No es escribirlo, es verificarlo.** El historial del proyecto dice que los
fallos recurrentes están en la paridad C#↔Python, no en el algoritmo.
Presupuestar la verificación como la mitad del trabajo.

1. **Cálculo incremental en el camino caliente de C#.** Son 128 características
   activas contra las 32 de ahora. El acumulador perezoso de v4.5.0 hay que
   rehacerlo: una amenaza cambia cuando se mueve el atacante, cuando se mueve el
   atacado, y cuando una pieza cualquiera bloquea o desbloquea una línea. Eso
   último es lo caro y lo que no tiene equivalente en HalfKA, donde una
   característica solo depende de dónde está una pieza.
2. **Codificador en `model.py` / `dataset.py`.**
3. **Esquema de fichero nuevo** con su número de versión y su hash.
4. **Verificación de paridad**, al estilo de la que cerró el contrato del bloque
   6 y la de arch 3: la misma posición evaluada en C# y en Python tiene que dar
   el mismo entero, no un valor parecido.
5. **Volver a derivar las características**, que NO es regenerar el corpus.
   Corregido el 2026-08-14, porque este punto decía lo contrario y habría
   costado tres días para nada: el registro de 40 bytes de `.noadata` guarda la
   POSICIÓN (ocupación, códigos de pieza, turno, enroques, al paso, reloj, ply,
   puntuación, resultado y mejor jugada), no las características. Esas se
   calculan al entrenar y se guardan aparte en `.features.npz`, que se
   invalidan solas por fecha. Un esquema nuevo obliga a redecodificar, que son
   horas, y el `featureSchemaId` de la cabecera se sube y ya está.

Semanas, no una noche, pero sin los tres días de datagen. Y por lo mismo NO
había ninguna ventana que perder antes de que terminara la regeneración: el
corpus de 324M sirve igual para el esquema nuevo.

## Riesgo que conviene tener presente

La referencia tiene esas amenazas con un transformador de **1.024** de ancho. El
nuestro es de 128, y el ancho ya se midió y pierde. Meter una entrada cuatro
veces más rica en un transformador ocho veces más estrecho puede no dar lo
mismo, y el resultado no distinguiría entre "las amenazas no aportan" y "no
caben". Esa ambigüedad hay que resolverla por diseño antes de gastar semanas.

## Estado: la sonda ya esta escrita y probada (2026-08-14)

Antes de escribir una linea de C#, el riesgo de arriba se contesta en Python:

- `tools/training/nnue/threats.py` codifica las amenazas. **Verificado contra
  python-chess** sobre 300 posiciones reales: ataques deslizantes, saltos, rango
  del indice, unicidad y simetria de la orientacion, todo correcto.
- `tools/training/nnue/verify_threats.py` es esa verificacion.
- `tools/training/nnue/probe_threats.py` entrena el 2x2 (entrada x anchura) y
  saca perdida de validacion y correlacion.
- `F:\Works\_______________CHESSTEST\probe_threats.bat` lo lanza.

**Dos hallazgos de escribirla:**

1. **La aritmetica de esta especificacion no cierra.** Sumar `numValidTargets`
   contra las cuentas de pseudo-ataques da **30.360** dimensiones, exactamente
   la mitad de las 60.720 citadas. Falta un factor 2 por explicar. Para la sonda
   da igual (solo tiene que ser consistente e informativa), pero **antes de
   fijar el esquema hay que resolverlo contra la fuente**.
2. **Codificar cuesta 1,85 ms por posicion**, o sea una hora larga por cada 2 M.
   Vale para una sonda porque se codifica una vez y lo comparten los cuatro
   brazos; para produccion habria que vectorizarlo como se vectorizo el decode.
