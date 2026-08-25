# NNUE Training History

Generational self-play pipeline. Each generation's datagen uses the previously
promoted net as teacher; the training data accumulates across generations.

---

## Estado a 2026-08-24: amenazas CERRADAS, anchura ABIERTA, y la velocidad por fin calibrada

Dos veredictos y una constante. La constante vale mas que los dos veredictos.

### Las amenazas pierden a reloj y no se publican

    723 partidas a 180+2 (131W 162L 430D, 59,5% tablas)
    Elo   -14,9   95% [-30,1, +0,1]
    LLR   -3,371  contra [-2,94, +2,94]   H0 ACEPTADA

    tendencia por bloques de 100:
      -17,4  -8,7  -18,5  -14,8  -9,7  -10,4  -12,9

Estable en los siete bloques, sin deriva. Los dos brazos con el mismo binario y
el motor ya acelerado un 15,9% ese dia. **Evaluan mejor - eso quedo probado con
+47,8 a nodos fijos - pero cuestan tanta velocidad que el paquete completo
pierde.**

### La anchura 512 gana, y su entierro no valia

    637 partidas a nodos fijos (226W 156L 255D)
    Elo   +38,4   95% [+18,7, +58,3]
    LLR   +3,338   H1 ACEPTADA

El veredicto anterior era -30,3 y con el se dio por muerta la anchura durante
semanas. **Aquel brazo se corto en la EPOCA 5 DE 60** para no gastar 13,5 horas.
Comparar un brazo truncado contra uno convergido mide el truncamiento.

**Y el 256 tampoco se ha medido bien:** su -31,9 sale de un log que termina en
epoch 5, exactamente igual. La aparente contradiccion - 256 pierde 32 y 512 gana
38 - desaparece en cuanto se mira el log.

### LA CONSTANTE: ~65 Elo por duplicar la velocidad

Cruzar los dos veredictos de las amenazas da algo que este proyecto no tenia:

    a nodos fijos   +47,8      (solo evaluacion)
    a reloj         -14,9      (evaluacion + velocidad)
    ratio de NPS     0,510x
    ------------------------------------------------
    la velocidad cuesta  47,8 + 14,9 = 62,7 Elo
    log2(0,510) = -0,971
    =>  ~64,6 Elo POR DUPLICAR, a 180+2

Hasta hoy esto se estimaba con una constante prestada y el punto de equilibrio
salia entre 0,58x y 0,70x segun quien lo escribiera. **Usar el 65.**

Y reordena la pregunta de capacidad: ya no es "mas ancho?" sino **"capacidad
BARATA?"**. El 512 corre a 0,655x contra un umbral de 0,664x, o sea justo en el
filo, y por eso su SPRT a reloj no se puede sustituir por una cuenta. Los dos
candidatos que suben al primer puesto son el **256 convergido** (menos ganancia,
bastante mas rapido) y los **buckets de salida**, que dan capacidad sin coste por
evaluacion porque solo se evalua un bucket por llamada.

---

## Estado a 2026-08-23 (noche): las tres vias de MOTOR para las amenazas, medidas y cerradas

Las amenazas valen +47,8 Elo a nodos fijos y lo unico que las bloquea es que no
caben en el reloj. Se atacaron las tres palancas de motor y ninguna abre la
puerta del todo:

    delta sin perspectiva   +15,9%   HECHO, 0,446x -> 0,510x del NPS de fq60
    filas perezosas         NULO     estan calientes en cache
    tabla finny             ~1%      solo ahorra filas
    podar relaciones        NO       señal proporcional al coste

### 1. El delta no dependia de la perspectiva, y se hacia dos veces

Que pieza ataca a cual es un hecho del TABLERO. La perspectiva no cambia el
hecho, solo el NUMERO con que se numera. El delta tomaba la perspectiva como
parametro, asi que por nodo repetia la geometria identica - busquedas magicas y
bucles de objetivos - para producir dos copias del mismo hecho, y el diff O(n*n)
tambien corria dos veces.

               antes   ahora
    AffectedAttackers    3       2
    CollectPairs/From    4       2
    diff                 2       1

**+15,87% [+12,0%, +15,3%]**, emparejado, 4 pares alternados, 208/238
posiciones, signo p = 0,00000, con nodos BYTE-IDENTICOS (1.109.671 exactos).

Lo que habia que comprobar antes: la pareja->indice es INYECTIVA, y `Map` y
`symmetric` dependen solo de TIPOS, asi que "esta relacion se registra" no
depende de la perspectiva. Lo unico que si depende es que direccion sobrevive en
las simetricas, y eso lo resuelve el indexado final.

Las mascaras de objetivo son las que salvan el diseño: las listas de parejas
llevan las DOS direcciones de las relaciones simetricas y el diff es cuadratico,
asi que un 1,4x de longitud habria cancelado la mitad entera. Con las mascaras
salen 1,043x mas largas y el diff cae un 45,1%.

### 2. Aplazar las filas hasta materializar: NULO

Con una red de amenazas el acumulador era ansioso, mientras que uno HalfKA
difiere y solo aplica el 54%. Se guardo la diferencia por nivel y se aplico al
materializar:

    filas de delta   8,00 -> 5,28 por nodo   (-34%)
    copias           ansiosas -> 36,9%
    reloj            +2,37%, signo p = 0,139  ->  NULO

**Quitar un tercio del trabajo de filas movio el reloj cero.** El micro-banco de
fila al azar las precia a 109 ns y en el 12-20% del reloj; en busqueda real
estan casi siempre calientes en cache porque nodos hermanos tocan relaciones
parecidas. Tercera vez en este proyecto que un coste aislado exagera el cuello.

Y encontro un fallo real que los 376 tests NO vieron: `PushNull` no llama a
`CompleteThreatDelta`, asi que un nivel nulo heredaba el diff RANCIO de un
hermano. Con el codigo ansioso era inofensivo; con el perezoso era corrupcion
silenciosa. Lo cazo el comprobador de nodos identicos, no la suite.

### 3. Podar relaciones: no hay nada que podar

Comando **`threatbands`**, que cruza el peso medio por fila con las veces que
aparece cada tipo de relacion:

    objetivo    % de señal   relaciones/pos
    Pawn           63,4%         36,32
    Knight         16,1%          9,76
    Rook           11,1%          7,62
    Bishop          8,1%          4,78
    Queen           1,0%          0,86

Proporcional en todos. La mitad mas floja de las 84 bandas son el 5,8% de la
señal y el 9,7% de las relaciones: una permuta, no un salto.

### Donde queda

El coste que sobra es CALCULO - generar las listas y diferenciarlas - y **no se
puede diferir**, porque el diff necesita el tablero de ANTES, que desaparece al
hacer la jugada. Hay que pagarlo en cada nodo aunque nadie evalue ese nodo.

Ya no queda modelo que hacer: la pregunta es empirica y la contesta un SPRT a
reloj de la red de amenazas contra fq60, con los dos brazos en el mismo binario
acelerado.

Instrumentos que quedan en el motor: **`threatfinny`** (cuanto ahorraria una
cache de acumulador para amenazas) y **`threatbands`**.

---

## Estado a 2026-08-23: las amenazas GANAN a la red que juega, y no se pueden publicar

**+47.8 Elo [+21.6, +74.5] a nodos fijos, 345 partidas, LLR +4.29, H1 aceptada.**
Es lo primero que supera a `fq60` desde el 10 de agosto. El primer bloque de 100
partidas ya daba +45.4, o sea que no es deriva del final.

    entrenamiento   60/60 epocas, 125.5 h, la epoca 60 fue la MEJOR (val 0.005856)
    exportada       --arch 4 explicito, verificada: arch 4, 21.3 MB
    linea base      noa-fq60 (hash 7f18eade), que es la red EMBEBIDA en el
                    ejecutable que juegan los bots

**Cuidado con la linea base**, porque el bat que existia comparaba contra
`noa-fqc60`, que es otra red. Comprobado cargando las tres candidatas y leyendo
el hash que reporta el motor. Contra la red equivocada el resultado no habria
significado nada.

### Por que no se publica: cuesta dos tercios de la velocidad

    NPS con fq60        ~746.000
    NPS con amthreat    ~245.000     -> 0.33x
    profundidad a 3+2   19.1 contra 17.5   -> 1.6 plies de handicap

A reloj sale con +48 de evaluacion y del orden de -55 de velocidad. Eso no es un
empate del que se pueda sacar una version: es una red mejor que no cabe en el
tiempo que tiene.

**Donde se va el tiempo, medido con el perfilador de la busqueda:**

    ThreatDelta.CollectFrom        16.4%
    CompleteThreatDelta            19.9%
    NnueAccumulator.Refresh        10.5%
    ThreatDelta.AddPawn             3.8%
                                   -----
    maquinaria de amenazas          ~50% del tiempo de busqueda

Es el coste inherente de rastrear ~37 features por posicion con un delta, no algo
que quite una micro-optimizacion. **La via para publicarla es rediseñar el
refresco, no ajustar lo que hay.**

### Dos intentos de acelerarlo, los dos medidos, uno revertido

**Buffers preasignados en vez de `stackalloc`.** El perfil ponia
`Buffer.ZeroMemoryInternal` en el **96.6%** del tiempo: `MaxActiveFeatures` paso
de 128 a 512 al arreglar un desbordamiento que mataba partidas, y ese numero
dimensiona tres `stackalloc` que C# ZEREA, en un metodo que corre por nodo.
Quitado, el zerado bajo a 1.62%.

**Y el A/B cronometrado dice que valia ~3%, no 96%.** Mismo binario antes y
despues, misma red, pasadas alternas: 241.788 contra 249.692 nps, con 10% de
dispersion dentro de cada brazo. La aritmetica lo confirma: 2 KB de memset son
~50 ns, por 805.073 nodos son 40 ms de 3.200, el 1.25%. **Un perfil de muestreo
dice DONDE mirar; solo un cronometro dice CUANTO vale.** El cambio se queda por
ser identico en nodos y mas limpio, no por su Elo.

**Fusion ordenada en vez del barrido O(n*n): REVERTIDA por medirse peor.**

    barrido lineal original      19.87% de self
    fusion + IntroSort           21.61%  (5.47% solo ordenando)
    fusion + insercion           22.51%

Las listas tienen ~37 entradas y **el 83% de las features sobreviven al
movimiento**, asi que `Contains` sale antes casi siempre mientras el orden paga
por todos los elementos. El comentario original decia que un barrido lineal gana
a cualquier estructura, y tenia razon.

## Estado a 2026-08-23: la arquitectura 5 se midio y PIERDE

Cabeza reconstruida al estilo de la referencia - lectura pareada del
transformer, activacion al cuadrado junto a la recortada, segunda capa oculta que
la salida lee de largo, y puente lineal. Dos brazos de 3 epocas, identicos salvo
`--dual`, a nodos fijos:

    575 partidas   -32.8 Elo [-56.5, -9.3]   LLR -6.01   H0 aceptada

Las dos redes verificadas: `noa-a5dual` es arch 5 y `noa-a5ctrl` arch 2.

**La causa mas probable es de diseño y no de implementacion: la lectura pareada
HALVA la entrada de L1**, de 256 a 128, dejando 64 valores por perspectiva. La
referencia se lo permite porque su transformer es de 1024 y al parear le quedan
512. Copiamos la tecnica sin la anchura que la sostiene.

**Lo que las pruebas de paridad no podian ver.** Salieron exactas - motor contra
numpy, y float con QAT contra entero por debajo de 1 cp - y la arquitectura sigue
siendo mala. **Verifican que las dos partes calculan LO MISMO, no que lo que
calculan sea BUENO.**

El experimento que separaria las causas, si se retoma: arch 5 SIN el pareado. Si
eso gana, el culpable es parear a esta anchura y lo correcto es ensanchar
primero.

---

## Estado a 2026-08-11: la red que juega es `fq60`, y mide 3271 ±40 CCRL

**+128 sobre los 3143 de v4.5.0**, y el primer salto del proyecto que sale
limpiamente fuera de la barra de error de su versión vecina. No vino de la
arquitectura ni de más generaciones: vino de **arreglar el entrenador**.

| cambio | medido |
|---|---|
| factorización de características | **+195,4 ±57,5** SPRT, H1 en 102 partidas &middot; **+128** en el campo |
| entrenamiento consciente de la cuantización | **+23,5 ±15,5** encima de lo anterior |

**El defecto se midió antes de arreglarlo:** el **85,6%** del transformador de
características se cuantizaba a exactamente cero, 2.221 de 22.528 características
estaban muertas, y atribuyendo el error por etapas salían **38,77 cp del
transformador contra 4,9 de la cabeza** sobre una evaluación media de 231 cp. El
motor jugaba un 16,6% lejos de la red que se había entrenado. Tras factorizar:
ceros 85,6% → 21,3%, error 38,79 → 17,63 cp, y las características muertas caen
a **exactamente 1.024**, que son las estructuralmente imposibles (peones en las
filas 1 y 8), o sea que ninguna característica legal se ignora.

**Conversión SPRT propio → campo: +195,4 se quedó en +128.** Anotarlo antes de
prometer nada a partir de un SPRT contra uno mismo.

### Los ejes, remedidos el 2026-08-14: son los DATOS, no el profesor

| eje | medido | cómo |
|---|---|---|
| más DATOS (20M a 324M) | **+104,6** [+68,8, +142,8] | `told` vs `fq60`, H1 en 171 partidas |
| más DATOS a igual cómputo | **+182** ±16,6 | calibración de escala, LOS 100% |
| mejores ETIQUETAS a 20M | **+21,2** [+6,8, +35,7] | prueba del profesor, 1.295 partidas |
| mejores ETIQUETAS a 324M | **+10,7** [−3,4, +24,9] | `fqc60` vs `fq60`, 1.100 partidas, sin concluir |
| más CAPACIDAD (ancho 256) | **−30,3** [−52,4, −8,5] | `fqw256`, H0 en 494 partidas |

**El cuello de botella son las POSICIONES, y el profesor importa poco.** Las dos
filas de etiquetas son la misma pregunta a dos escalas de corpus: **+21,2 con 20M
posiciones por brazo se quedó en +10,7 con las 324M reales**. Es la lección cara de
esta campaña, y está escrita aparte porque se repite: **un efecto medido con un
corpus pequeño no predice el mismo efecto con el corpus completo**. Las 59 h de
datagen que regeneraron las 324.299.195 posiciones con `fq60` como profesor
compraron una línea base algo mejor, no un salto.

Dos salvedades sobre la primera fila, que es la que manda:

- `told` (20M) y `fq60` (324M) se entrenaron **60 épocas cada una**, así que la de
  324M recibió también 16 veces más pasos de gradiente. El +104,6 **mezcla más
  posiciones únicas con más cómputo de entrenamiento** y esta medida no los separa.
- El **+182** de la calibración no es una medida pura de volumen: comparaba 20M a
  6.000 nodos contra 4,3M a 28.000, o sea volumen contra profundidad a cómputo
  igual. La única medida limpia de volumen es la de arriba.

Por duplicación del corpus eso da **+82 Elo abajo** (4,3M a 20M) y **+26 aquí
arriba** (20M a 324M). Decae rápido, y son dos puntos: no sirven para extrapolar
la siguiente duplicación.

### Auditoría de los negativos: cuáles de estos entierros valen

Escrita el 2026-08-14 después de estar a punto de enterrar las features de amenazas
con dos defectos de diseño dentro de la prueba. La regla que faltaba, y que ahora se
aplica a todo lo que se declare muerto:

**Antes de aceptar un negativo hay que responder cuatro preguntas.** 1) ¿Convergieron
todos los brazos? 2) ¿Está la configuración en el régimen donde la cosa se sabe que
funciona? 3) ¿Hay **control positivo**, un brazo que mida algo ya medido como
ganancia? 4) ¿Qué diferencia queda con la referencia? Si falta cualquiera, el
veredicto es "sin veredicto", no "no funciona".

Pasando el listado por esas cuatro preguntas:

| conclusión enterrada | estado tras la auditoría |
|---|---|
| "el eje de datos está cerrado" | **ERA FALSA**. Nunca se midió. Medida el 2026-08-14: **+104,6** |
| `fqw512`, ancho 512, perdedor | **INVÁLIDA**: cortada en la **época 5 de 60** para ahorrar 13,5 h. Es exactamente el brazo truncado que la pregunta 1 prohíbe |
| `fqw256`, ancho 256, −30,3 | **EN DUDA**: 494 partidas, convergida, pero medida sobre la entrada POBRE. Si entrada y capacidad van acopladas, esto mide el acoplamiento, no la anchura |
| `ds1b8`, buckets, −15,2 | ya se sabía inválida: mezcla buckets con cuantización arch 1 contra arch 3 |
| "el self-play está agotado" | ya se sabía falsa: medida con el entrenador roto |
| King safety fase B | **VÁLIDA**: tres medidas independientes, eval clásica, sin dependencia de escala |

Cinco de seis entierros no aguantan la auditoría. El patrón no es que las ideas
fueran malas: es que **el listón para decir "no" estaba mucho más bajo que el listón
para decir "sí"**, y eso sesga una campaña entera hacia abandonar cosas que
funcionaban.

### El eje de épocas, MEDIDO: +6,4 y sin concluir

`fqc120` (mismo corpus, misma receta, 120 épocas en vez de 60) contra `fq60`:

    2.920 partidas a 10+0.1   score 0.5092
    +6,4 Elo  95% [-2,1, +14,9]   LLR +0,756 de +-2,94, 26% del camino a H1

**No concluye.** La lectura honesta es "positivo pequeño, por debajo de lo que
3.000 partidas resuelven". No basta para publicar - el intervalo toca el cero -
pero tampoco cierra el eje: doblar las épocas vale **algo**, del orden de +6.

**Coste: 19 h de entrenamiento más 14 h de SPRT para un número que no concluye.**
Ese es el dato que importa para planificar: 33 horas de máquina por un efecto que
no se puede resolver con el presupuesto que tenemos.

#### Mi predicción falló, y por confiar en la validación

Predije **plano**, por escrito y antes del resultado, apoyado en que `fqc120`
terminó con validación **0,10% PEOR** que `fqc60` (0,005860 contra 0,005854), y en
que a igual fracción de recocido iba +0,42% peor en los dos puntos comparables.

Las dos redes entrenaron el mismo corpus con la misma partición de validación, así
que era la comparación más limpia posible entre validaciones. **Y aun así apuntó
al signo contrario.**

Es la tercera vez en la misma semana que un proxy barato da el signo equivocado:

| proxy | dijo | midió |
|---|---|---|
| profesor a 20M | +21,2 | +10,7 a 324M |
| sonda de amenazas v1 | −5,43% | +3,96% con sus defectos arreglados |
| validación de fqc120 | −0,10% (peor) | **+6,4 Elo (mejor)** |

**La pérdida de validación orienta; no decide.** Estaba escrito como advertencia
en este mismo fichero antes de que yo la ignorara en una predicción propia.

### La red está infra-entrenada, no saturada

La curva de validación de `fqc60` seguía bajando **5,59% en sus últimas diez
épocas** y solo se aplanó en la 60 porque el coseno del learning rate tocó fondo en
1,07e-05. Su pérdida de entrenamiento (0,005545) sigue **por debajo** de la de
validación (0,005866) con la distancia cerrándose, que es lo contrario del
sobreajuste. En curso: `fqc120`, la misma receta con el coseno estirado a 120
épocas (`T_max=args.epochs`, comprobado antes de lanzarlo), unas 19 h.

### Dos conclusiones de este fichero quedan ANULADAS

**1. "Do not re-propose network capacity" sigue en pie, pero por otro motivo.**
Se escribió cuando el ancho 512 midió −76/−93 con el entrenador roto. Reabrí el
eje el 2026-08-11 argumentando que esa medida estaba viciada, porque ensanchar
empeora justo el defecto que la factorización arregló: la misma señal repartida
entre más neuronas da pesos más pequeños, y los pesos pequeños son los que la
cuantización borra. **El argumento era razonable y estaba equivocado**: con
factorización y QAT, el ancho 256 sigue perdiendo 30 Elo. Ahora el eje está
cerrado con evidencia válida.

**2. "El self-play está agotado" era FALSO, pero se quedó a medias.** Cinco
generaciones planas dieron esa conclusión, medidas con el entrenador que cuantizaba
a cero el 85,6% del transformador: una generación podía salir plana porque la red
no podía aprovechar mejores etiquetas, no porque no las hubiera. Repetido con el
entrenador arreglado, el profesor nuevo gana, pero **+10,7 a escala real, no los
+22 que prometía la prueba a 20M**. La conclusión correcta no es "el profesor
importa": es que **cambiar de profesor sobre las mismas posiciones da poco, y
añadir posiciones da mucho**.

### Lo que viene, reordenado el 2026-08-14

El orden lo fija el coste por Elo, no el interés de la idea:

1. **`fqc120`**, 120 épocas sobre el corpus nuevo. Ataca la mitad del +104,6 que es
   cómputo, no requiere generar nada ni tocar el motor, y cuesta 19 h. EN CURSO.
2. **Más corpus.** Unas 60 h por duplicación, del orden de +26 esperado según la
   pendiente actual, y conviene medir antes cuánto del +104,6 era cómputo.
3. **Características de amenazas**: la referencia añade a HalfKA un juego entero de
   60.720 dimensiones con 128 activas que codifica qué pieza ataca a cuál, y
   nosotros no tenemos nada de eso. Sigue siendo el ataque estructural, pero son
   semanas de C# y hay dos ejes más baratos por delante. La sonda que decide si
   merece la pena ya está escrita y verificada (`probe_threats.py`).

**De la cola vieja sobrevive solo `fqb1`/`fqb8`** (buckets de salida con su control
int8 - un net con buckets solo se exporta como arch 3, que es int8 con QA=127, así
que medirlo contra `fq60` que es arch 1 movería dos variables). Sobrevive porque
resuelve una contradicción real (+20,1 con LOS 99,8% en v4.2.0 contra −15,2 en
`ds1b8`), no porque ajuste un número. **`fqwd0`, `fqloss` y `fqlam` quedan
descartados**: son búsqueda de hiperparámetros, viven en la banda ±10-20, y a
10+0.1 resolver **+10 Elo pide unas 8.700 partidas (45 h)** y **+5 Elo pide unas
35.000 (181 h)**. No se prueba nada cuyo efecto esperado sea más pequeño que el
instrumento de medida.

---

## Historia anterior (generaciones gen2-gen9, hasta v4.5.0)

> Todo lo que sigue describe la era generacional y **termina en gen9 / v4.5.0**.
> Se conserva porque documenta como se llego hasta aqui y que se descarto por el
> camino, pero las cifras vigentes son las de arriba. Donde una conclusion de
> esta seccion haya quedado anulada, hay una nota citando la medida que la anulo.

**Key finding of that era:** the dominant lever looked like datagen label depth
(`--nodes`), not the generational loop itself. gen2-gen4 all used 14000-node
labels and made small steps (+2 to +6 Elo); gen5 raised labels to 20000 nodes and
jumped +34. **Superseded on 2026-08-01:** at equal total search work, 20M
positions at 6,000 nodes beat 4.3M at 28,000 by **+182.2 ±16.6, LOS 100%**. The
network was starved of DATA and label depth was never the binding constraint.

Internal SPRTs run at TC 10+0.1. Note that vs-classical comparisons at that fast
TC are speed-sensitive (the NNUE eval is ~66% the speed of classical), so the
absolute CCRL placement of a net comes from `gauntlet_nnue.bat` (vs the 12-engine
CCRL field), not from the internal SPRT. Classical baseline (2.8.4-equivalent,
NNUE off) ≈ 3020-3035 CCRL.

**MAS RECIENTE ARRIBA.** La tabla iba en orden ascendente y lo que se consulta
es siempre la ultima red, no la primera.

**Y a partir de `fact60` el eje deja de ser generacional.** Las filas de gen2 a
gen9 se distinguen por QUIEN etiqueto los datos y a cuantos nodos; las de abajo
se distinguen por COMO SE ENTRENA la red sobre datos que no cambian. Por eso la
columna de nodos se queda fija en 6000 y aparece una de "que cambia".

| Red | Motor | Que cambia | Paso medido | CCRL (gauntlet) |
|---|---|---|---|---|
| **NNUE-1.1 `fq60`** | v4.6.2, v4.7.0 | factorizacion **+ entrenamiento consciente de la cuantizacion** | **+23,5 ±15,5 vs fact60**, H1 | **3271 ±40** (600 partidas, 75,7%) |
| **NNUE-1.0 `fact60`** | v4.6.2 (no publicada sola) | **factorizacion de caracteristicas**: 704 caracteristicas virtuales (pieza, casilla) plegadas EXACTAMENTE en sus 32 copias al exportar | **+195,4 ±57,5 vs ds1e60**, LOS 100%, H1 en 102 partidas | - (la midio fq60) |
| `ds1e60` | v4.4.0-v4.5.0 base | 60 epocas sobre el corpus completo de 324M | base de comparacion de la campana | - |
| **NNUE-0.9 `gen9`** | v4.3.1, v4.4.0, v4.5.0 | mismo corpus que gen8, solo epocas 6 &rarr; 60 | **+18 vs gen7** (1178 partidas, H1, LLR 2.97) | **~3114** (v4.4.0, 600 partidas) |
| - `gen8` | - | 6000 nodos, primer corpus a escala | **NO promovida** (H0 a 198 partidas; los errores en partida real se TRIPLICARON) | gauntlet empezado y abandonado |
| NNUE-0.7 `gen7` | v3.2.0, v4.3.1 | 28000 nodos | +3,7 ±10,2 vs gen5 (paridad, no H1 formal) | **~3080 ±40** |
| NNUE-0.6 `gen6` | - | 24000 nodos | **NO promovida** (cayo a 0,494 a las 800 partidas) | - |
| NNUE-0.5 `gen5` | v3.1.x | 20000 nodos | +34,0 ±14,4 vs gen4, LOS 100% | **~3050 ±40** |
| NNUE-0.4 `gen4` | - | 14000 nodos | +3,5 ±9,9 vs gen3 | - |
| NNUE-0.3 `gen3` | - | 14000 nodos | +6,2 ±11,3 vs clasico | - |
| NNUE-0.2 `gen2` | v3.0.0 | 14000 nodos | +1,9 vs clasico, H1 | - |

**Lo que la tabla enseña de un vistazo:** siete generaciones de self-play
llevaron la red de ~3050 a ~3114, es decir **+64 en siete pasos**. Los dos
cambios siguientes no tocaron ni un dato y valieron **+157** (de 3114 a 3271).
El problema nunca estuvo en de donde salian las partidas.

**Nets descartadas por el camino** (mismo corpus, mismos hiperparametros, un
solo eje distinto): `ds1w512` ancho 512 **-76 / -93**, `ds1b8` 8 buckets
**-15,2** (comparacion NO limpia, ver la nota del final), `ds2` **-108,6**
(movia tres variables a la vez), `fqw256` ancho 256 **-30,3, H0** ya con
factorizacion y QAT puestos.

**v4.5.0 changes no net, but it changes what serving one costs.** gen9 is still
the shipped network; the runtime around it got about 10% faster, and roughly a
third of that is NNUE-side. The accumulator stack was **eager** - it copied both
perspectives and did the feature math on every `MakeMove` whether or not the
position was ever evaluated - and now records the update and materialises it on
demand from the nearest computed ancestor (+3.6%, node-identical). Anyone
re-reading the speed notes above should treat "the NNUE eval is ~66% the speed of
classical" as the pre-4.5.0 figure; the gap is narrower now and was never
re-measured, because with NNUE shipped in every build the comparison stopped
mattering.

**gen5 CCRL calibration (2026-07-28):** field gauntlet vs the 12 CCRL engines
(2862-3281, 20 games each, 240 total) at TC 60+0.6, single-threaded. **51.0%
overall; ML performance rating ≈ 3050 CCRL** against a field averaging 3043.
gen5 beats every opponent ≤3010 (Colossus 2862: 92.5%, Bit-Genie 3010: 57.5%)
and loses to ≥3120 (Winter 3120: 37.5%, Patricia 3281: 17.5%), crossover ~3050.
This is the first CCRL number for the NNUE line. Note it lands only ~+15 over the
classical estimate (~3035), NOT the +42 the internal SPRT chain suggested - the
expected shrink of self-play gains against a diverse external field. It is the
FLOOR: Lazy SMP (v3.1.0, measured +253 Elo Threads=30 vs Threads=1 at 20+0.2, LOS 100%; CCRL field gauntlet pending); cold-start fix (v3.1.1, no Elo change)
and deeper-node generations (gen7+ at 28000+ nodes) add on top. This 3050 is
single-threaded.

**gen6 (2026-07-28):** 24000 nodes. SPRT vs gen5 (TC 10+0.1) drifted below 0.5
at 800 games and was stopped. Not promoted; gen5 remains the active teacher.
The gen6 dataset is included in the gen7 combined training set.

**gen7 (2026-07-29, v3.2.0):** 28000 nodes, embedded and promoted as a
**marginal** generation - the vs-gen5 SPRT is parity (76.2% LOS), not a formal
H1. Its own gauntlet (240 games, 60+0.6, single-thread, field 2862-3281)
placed it at **57.9%, ~3080 ±40 CCRL**, up from gen5's 51.0%/~3050 but inside
combined gauntlet noise. The honest read at the time: the human-opening
seeding this generation shipped with did not itself buy strength over gen5 -
the value was the data pipeline and pinning the NNUE-over-classical delta at
+28.5 (the old cascade-sum ~+46 had over-counted, since self-play Elo is not
additive). **Correction (2026-07-31):** the human-opening seeding never
actually ran - every manifest on disk says `"openingPlies": "8-9 random
legal"`, the `-Book` argument was never passed. The +28.5 and ~3080 figures
stand; what is void is the provenance claim and the "pure self-play is
exhausted" conclusion drawn from it, since gen7 was trained on random
openings after all. See [README](README.md) for the full correction.

Notes:
- gen2's SPRT log was later removed in a cleanup; its +1.9 (H1) is on record from
  the run, not a file.
- gen5's +34 is the deeper-labels payoff (14000→20000 nodes). Its absolute CCRL
  placement (~3050) comes from the field gauntlet; the internal-vs-classical step
  is skipped for gen5 because the gauntlet is the more direct placement.

**gen8 (2026-08-05): trained, measured, NOT PROMOTED.** The data-scale campaign's
first net: 331M positions (314,564,250 train / 16,555,978 val from 70 files) at
6000-node labels, ft=128, one output bucket, **6 epochs**. Three independent
measurements all said no:

1. **SPRT vs gen7** at 60+1, `Threads=1`, ponder off: stopped at **H0** after 198
   games (59W 95D 41L, 53.8%). No evidence of the +50 Elo the bounds asked for.
2. **Real games on the bot**, same binary, only the net swapped: the avoidable
   material-loss rate **tripled**, 0.23 to 0.72 per 100 moves (p≈0.017), and the
   score fell from 80.5% to 75.8% against opposition only 58 Elo stronger. See
   [[bot-version-timeline-aug2026]] in the session memory for the exact cutoffs.
3. **Gauntlet** vs the 12-engine field: started, then abandoned once the first
   two measurements agreed. No number recorded.

**The cause is the training schedule, not the data.** The loss curve never
flattened - validation loss fell 0.008005 → 0.005993 across the six epochs and
**the largest single drop was the last one** (-0.00065, against -0.00005 for the
first), with every epoch marked as a new best. `CosineAnnealingLR` is built with
`T_max=args.epochs`, so the learning rate hit its 7.63e-05 floor exactly when the
schedule ran out. Training stopped because the calendar ended, not because the
model converged. The `--epochs 6` default dates from the 4-20M-position
generations; at 331M it is roughly 2 billion samples seen, which is low by any
standard for this size of corpus.

**Numbering rule (settled here, precedent from gen6):** a generation number is
consumed by the ATTEMPT, not by the promotion. gen6 was trained, drifted below
0.5 and was never promoted, and the next net was still called gen7. Same now:
**gen8 keeps its number and this row**, and the retrain is **gen9**. Note that
gen9 is not a new generation in the datagen sense - it reuses gen8's corpus
byte for byte and changes only the training length (60 epochs against 6, on
disk-speed grounds - see below - not 120 as first planned), which is exactly
what makes the comparison between them clean.

**gen9 (2026-08-06): trained, measured, PROMOTED.** Same corpus as gen8, same
ft=128/one-bucket architecture, only `--epochs` changed from 6 to 60. The first
attempt used 120 and projected 3.7 days: the corpus lived on the repo's
mechanical HDD, so training was disk-starved rather than compute-bound (GPU
utilization 8-43%, step rate collapsed from ~40/s to ~7/s), and the shards had
only looked fast for gen8 because they were still warm in the OS page cache
from having just been written. Copied to an SSD instead (`train_ssd.bat`),
throughput recovered to ~28 steps/s and 60 epochs completed in 8.1 h. Validation
loss reached **0.005518** at epoch 60 (gen8: 0.005993 at epoch 6 - about 8%
lower), and the tail of the curve was flattening (-0.000063, -0.000058,
-0.000056 across the last three epochs), unlike gen8's accelerating tail -
consistent with a schedule that ran its course rather than one cut short.

**SPRT vs gen7 at 10+0.1, `Threads=1`, ponder off, no tablebases for either
side: H1 accepted, LLR 2.97 crossing the 2.94 bound at 1178 games** (352W 536D
290L, 52.63%, ~+18 Elo - close to the elo1=20 upper bound tested, not a
marginal scrape past elo0=0). The score drifted down substantially as the
sample grew (56.4% at 141 games, 54.8% at 221, 53.1% at 289, 52.5% at 702, 52.0%
at 950) before the final tally settled at 52.63% - a reminder that the
project's own naive score-tracking during a run is not the SPRT's actual
statistic; cutechess's pentanomial LLR is what decided this, not the eyeballed
trend. First real, confirmed Elo gain of the data-scale campaign, and a modest
one, matching what the flattening loss curve predicted rather than the dramatic
jump the campaign hoped for - the next lever (width, ft=128 to 512) is already
running, corpus and epoch count held fixed, to isolate that axis next.

**Status as of v4.3.0.4 (2026-08-04): still gen7, unchanged since v3.2.0.**
Everything shipped between v3.2.0 and v4.3.0.4 - Lazy SMP, complete correction
histories, output buckets, the ponderhit and root-move fixes - is search or
scheduling, not training, so this table has nothing new to record. The next
entry here is gated on the data-scale campaign (`Noa-DataScale.ps1`): phase 0
already measured the current net as **data-starved by +182 Elo** at equal
compute (20M positions @ 6000 nodes beat 4.3M @ 28000 nodes, LOS 100%), which
is why the campaign trains at 6000 nodes instead of pushing label depth
further - see [README](README.md) and [CHANGELOG](CHANGELOG.md).

**Status as of v4.3.1 (2026-08-05): still gen7, and now measured with the
current engine.** A field gauntlet of **v4.3.1 + gen7** scored **59.7% over 165
games** against the same 12 CCRL engines (average 3043), for a performance of
**~3110 ±45**. Applying one formula to all three runs for once: gen5 3050, gen7
3098, v4.3.1+gen7 3111. The **+13** over the gen7 figure sits well inside ±45,
so the correction histories and the 4.3.x fixes are **not measurably visible
here** - what the run establishes is a band, roughly **3070-3155**, with the
crossover against the field around 3150 (50.0% against Rubichess 3150, 60.7%
against Winter 3120, and losses to Princhess 3230 and above).

Two parameters differ from the gen5 and gen7 gauntlets, so treat this as the
start of a cleaner series rather than a strict continuation: the opening book
was cut at `plies=16`, and **no engine had tablebases** (uniform, therefore fair,
and stricter than a run where only NoaChess has them). Ponder was off, as it has
been throughout - cutechess only ponders when the bare `ponder` flag is present
on an `-engine` line, which these runs never pass. The run was stopped at 165 of
a planned 600 games.


---

## gen9's absolute number, measured 2026-08-07 (v4.4.0 gauntlet)

The first full-length gauntlet carrying gen9: **600 games, 60.1%, performance
~3114** against the 12-engine field averaging 3043, at 60+0.6 single-threaded,
ponder off, no tablebases for anyone. Per-opponent performances land between
3052 and 3215, so no single pairing is dragging the figure.

**This does NOT measure gen9's +18.** The previous full reading was ~3110 for
v4.3.1+gen7, and a 600-game gauntlet resolves roughly ±20. gen9 (+18 by SPRT)
plus the v4.4.0 search work (~+7 by node and nps measurement) should land near
3135; 3114 is inside the band either way. The honest statement is that the
engine sits around **3100-3150** and that nothing regressed - the gauntlet
confirms position, it does not resolve a delta of this size.

> **SUPERADO el 2026-08-10.** Esa banda de 3100-3150 fue cierta durante cuatro
> versiones y dejo de serlo con `fq60`: **3271 ±40** sobre 600 partidas, +128
> sobre los 3143 de v4.5.0, y el primer salto que sale limpiamente fuera de la
> barra de la version vecina. Lo de abajo se conserva porque explica como se
> llego hasta aqui, no donde esta el motor.

### The capacity axis is now closed in both directions

Two capacity experiments were trained on gen9's exact corpus and
hyperparameters, differing in one flag each, and both **lost**:

| variant | difference | result |
|---|---|---|
| `ds1w512` | `--ft-out 512` instead of 128 | **-76** at 10+0.1, **-93** at 60+0.6 |
| `ds1b8` | `--out-buckets 8` instead of 1 | **-15.2 ±25.3, H0** at 435 games |

The b8 result is clean - checkpoint metadata confirms 60 epochs, batch 16384,
lambda 0.85, ft_out 128, l1_out 32 and the same 70 shards for both, with
`out_buckets` the only difference - so it is not the undertraining that sank
gen8. Wider loses and more heads loses. **Do not re-propose network capacity as
the next NNUE lever**; if this axis reopens it will be from the data side.

> **CONFIRMADO el 2026-08-11, tras reabrirlo y equivocarme.** El eje se reabrió
> con el argumento de que estas medidas venían del entrenador roto. Repetido con
> factorización y QAT puestos, el ancho 256 mide **−30,3 [−52,4, −8,5], H0 en 494
> partidas**, y `fqw512` se cortó en la época 5 para no gastar 13,5 horas
> confirmando la misma dirección. La frase de arriba se mantiene, ahora con
> evidencia válida detrás. Nota aparte sobre los buckets: **`ds1b8` no era una
> comparación limpia** después de todo, porque un net con buckets solo se exporta
> como arch 3 (int8, QA=127) mientras la base era arch 1 (int16, QA=255), así que
> aquel −15,2 mezcla buckets con cuantización. Los mismos 8 buckets midieron
> **+20,1 con LOS 99,8%** en v4.2.0 sobre otro corpus, y esa contradicción sigue
> sin resolver: por eso `fqb1`/`fqb8` van EN PAREJA en la cola.

Each published engine bakes its net in as an embedded resource, so a net swap
requires a republish, and `src/NoaChess.UCI/Resources/noa-embedded.noannue`
persists between builds - verify the reported hash before every measurement.

## 2026-08-25/26 - in flight

The three training arms the crashed 11-08 queue never reached are running in
series on the fq60 recipe, one variable each: fqloss (the reference loss
instead of the raw MSE every published net trained with), fqlam (lambda
scheduled 1.0 to 0.7) and fqwd0 (no ft weight decay). Fixed-nodes SPRTs
follow each export; same architecture means the verdict transfers to the
clock unchanged. The psqt two-headed net is built end to end (engine lane,
trainer head, exact virtual-row folding, parity tests) and trains on top of
whichever recipe wins; verify_export must learn the psqt block first.
