# NoaChess — Master Development Roadmap (DEFINITIVO)

> **Documento único de referencia.** Contiene el historial completo, el plan detallado hasta el final del proyecto, y las decisiones técnicas que no queremos repetir. Actualizar al cerrar cada bloque.
>
> **Regla de oro SPRT:** un término por SPRT, TC 10+0.1, 8192 partidas, elo0=0 elo1=10. Nunca tunear movilidad (señal EG espuria). Vigilar NPS antes y después de cada cambio de evaluación. Un término = un SPRT, siempre.
>
> **Regla de oro de escala de referencia:** TODO valor copiado de un motor de referencia (evaluate.cpp, pawns.cpp) se multiplica por **100/208 ≈ 0.48**. El motor de referencia trabaja en unidades internas donde PawnValueEg=208 equivale a los 100 cp que reporta por UCI; NoaChess evalúa directamente en ~centipawns (PeSTO). Copiar los números en crudo duplica la fuerza de cada término (lección de 4B: llr −1.09 en 200 partidas con los valores sin escalar).

---

## Estado actual

| Versión | Elo CCRL | Estado |
|---------|----------|--------|
| **3.1.0** | **Red gen5 embebida mide ~3050 ±40 CCRL (gauntlet 51.0% en 240p vs campo 2862–3281, a 1 hilo — 1ª calibración CCRL de la NNUE). Lazy SMP: `Threads=1` byte-idéntico a 3.0.0 (1.307.077 nodos exactos); escalado de nodos ~7,6× a 8 hilos; el SMP mide +253 Elo `Threads=30` vs `Threads=1` (20+0.2, LOS 100%, gauntlet CCRL de campo pendiente)** | **Búsqueda paralela Lazy SMP.** N workers buscan la misma raíz compartiendo UNA sola TT (lock-free por carreras benignas: verificación de clave de 32 bits + veto de pseudo-legalidad descartan lecturas rotas); pila de búsqueda, historias, tablero (`Board.Clone()`) y evaluador son por hilo (NNUE comparte la red read-only con acumuladores propios; clásico instancia nueva). El worker principal gestiona el tiempo y reporta `info`; al final los workers votan la jugada (ponderada por score, con manejo de mates). UCI `Threads` 1–32. **Arreglo de tiempo SMP** (instabilidad promediada sobre el pool + deadline soft acotado + cap a nivel de nodo a media iteración a 1,5× el soft): acota un pico de reloj en ponderhit sobre TT caliente (recaptura forzada 22-37s → ≤~5s, max 5.2s/10 corridas). Verificado: sin crashes y jugadas legales a 1–32 hilos con Classical y NNUE. 205/205 tests |
| **3.0.0** | **gen3 +4.5 ±11.4 vs clásico (1002-968-680 [0.506] 2650p, LOS 77.8%, positivo agotado); gauntlet LTC pendiente** | **NNUE HalfKAv2_hm: la evaluación neuronal supera a la clásica.** Feature transformer schema 2 (InputSize 22528, reyes como features, 32 buckets), topología FT 22528→128 ×2 → L1 32 → 1, cuantización QA=255/QB=64/OutputScale=400. Inferencia SIMD AVX2 (VPMADDWD, activación clipped precomputada, MoveFeature fusionado): 312k→446k NPS. Acumulador incremental verificado por paridad. `NoaChess.DataGen` con mezcla WDL + adjudicación Syzygy/resign/tablas. **Bug crítico corregido:** el hard-stop por límite de nodos devolvía Score 0 en la primera jugada de raíz, poniendo a cero el 57% de las etiquetas (57.6%→2.1%). Self-play generacional: gen2 +1.9 Elo, gen3 +4.5 Elo vs clásico. Red `noa-gen3` embebida en el exe. 276/276 tests |
| **2.8.4** | **+9.2 ±9.1 vs 2.8.3 (3000p exhausted positive, LOS 97.5%, LLR 1.91); gauntlet LTC pendiente** | **Ajustadores LMR ttCapture y ttPv sobre el pipeline de punto fijo.** ttCapture `r += 1079`: cuando la jugada TT es una captura, las quietas tardías se reducen ~1 ply más. ttPv `r -= 1024 + ajustes`: nodos que estuvieron en la PV anterior se reducen ~1 ply menos. Cada uno escrutado individualmente (>93% LOS) y validados juntos vs v2.8.3 en el checkpoint SPRT. cutNode hilado a través de Negamax como infraestructura (término aislado CUT a 4026 y 1536). Cota ContinuationHistory corregida a 8192. Término muerto de historia LMR eliminado. 276/276 tests |
| **2.8.3** | **+24.4 ±17.5 vs 2.8.2 (835p, intervalo excluye el cero); gauntlet LTC +112 ±24 relativo** | **Gravedad de historia que sí actúa.** La "gravedad acotada" de v2.8.2 era **numéricamente inerte**: `score×|bonus|/MaxScore` trunca a 0 con cota 2²⁰ frente a valores cercanos a 7 000. Dimensionada la cota de butterfly en 7183 como la referencia: media +71.8 → +13.5, cola 6086 → 3134. Pipeline de LMR a punto fijo (1024avos), **verificado neutro** por nodos idénticos. **Cortado por el camino:** statScore como término de historia en LMR, remedido al ritmo real, −18 Elo H0 — cierra el hueco de evidencia de 5C, que lo había juzgado a 5+0.05. ⚠️ **El SPRT se cortó a mano con LLR 2.61 frente a banda 2.94: no es un H1 formal.** 276/276 tests |
| 2.8.2 | **SPRT H1 +28.0 ±17.2 vs 2.8.1 (834p); gauntlet LTC +94 ±23, ~3013 ±30 CCRL** | **Auditoría del search clásico validada, sin adelantar NNUE/SMP.** Pawn correction history; ProbCut con verificación depth>=1 y promociones exentas del SEE simplificado; ventana de aspiración fija con recentrado fail-low; bandas killer/counter probadas conservadas; sin extensión incondicional de jaque; log UCI solo explícito. 276/276 tests. ⚠️ Esta fila acreditaba además "continuation history por gravedad": **corregido el 2026-07-23, esa gravedad es numéricamente inerte** (`6086×169/2²⁰` trunca a 0) y el +28.0 vino del resto del paquete. |
| 2.8.1 | **SPRT +14.1 ±10.8 H1 2175p · gauntlet LTC +75 ±23 · ~3000 ±25 CCRL** | **Bugfix Syzygy + ordenación 5G.** Dos bugs críticos en 2.8.0: (1) el filtro de raíz se anulaba — `SearchRoot` regeneraba todas las jugadas DESPUÉS de `FilterRootMovesByTablebase`, descartando el filtro; (2) el ranking DTZ puntuaba las jugadas irreversibles antes de que ocurrieran y elegía la derrota más rápida en posiciones perdidas. Corregidos. Seguridad TT: `CanReuseTtScore` bloquea reutilización de scores en banda TB cuando `halfmoveClock > 0`. `SyzygyTable` migrado a `MemoryMappedFile` + offsets `long` (elimina el límite de 2 GB para ficheros de 6/7 piezas). Ordenación: `_captureHistory` integrado en la búsqueda principal (7×víctima + historia); partial sort quiets (`−3000×depth`, `MoveRangeToFront` garantiza QUIET antes de BAD_CAPTURE); `CheckBonus +16 384` para jaques directos seguros; bonus/penalización escape/entrada en amenazas de piezas menores. X-ray movilidad: sliders solo transparentan la dama propia. UCI: opción `Ponder` declarada. Tests Syzygy portables por `NOACHESS_SYZYGY_PATH`. Nuevos tests: `CaptureHistoryTests`, `UciSearchLimitsTests`. Herramientas nuevas: `NoaChess.DataGen`, `NoaChess.Tuner`, pipeline Python NNUE. 268 tests descubiertos (193 ejecutados con los ficheros Syzygy ausentes) |
| 2.8.0 | ❌ nunca validado — dos bugs críticos corregidos en 2.8.1 | **Bloque 9: tablebases Syzygy.** Resultado exacto con ≤5 piezas: sondeo WDL en la búsqueda (condicionado al contador de 50 jugadas, veredictos en banda propia bajo el rango de mate) y **filtrado** de las jugadas de raíz por WDL y DTZ. En la raíz es filtro y NO retorno inmediato, para no romper el anuncio de mate de la v2.7.1. **Port nativo de ~1250 líneas, no P/Invoke**: no hay compilador de C y una DLL rompería el exe único. Verificado contra un prober independiente en **3000 finales, cero discrepancias**; cazó 3 bugs (base del árbol de símbolos cacheada por tabla en vez de por PairsData → colgaba con peones; off-by-one en el remapeo DTZ; reyes desnudos sin tabla de 2 piezas). Medido: KPvK ganado se convierte en 15 plies vs 25. Coste 1.1% NPS tras reordenar el guard por selectividad (era 3.5%). 208/208 tests |
| (auditoría ProbCut) | **ProbCut reauditado y embarcado en 2.8.2** | La revisión 2.8.2 garantiza verificación normal depth>=1 y conserva el rework medido. |
| 2.7.4 | **SPRT −2.1 ±9.9 (H0, 2347p) · gauntlet LTC +52 ±23 vs +48 de la 2.7.2 · ~2975 ±25 CCRL SIN CAMBIO** | **Rework de quiescence — versión de CORRECCIÓN, no de fuerza.** En jaque: sin stand-pat, TODAS las jugadas, cero poda, mate detectado. Guard de ahogado, fail-soft, las 4 promociones. Bloque de poda de referencia portado entero (futility 147, SEE −36, capture history con gravedad + 7×víctima). **Arregla el cuelgue en raíz** con mate/ahogado, presente desde siempre. −5.7% nodos, tiempo a profundidad −9.0%/−12.6%, **WAC 269/300 récord**, 192/192 tests. Los dos instrumentos coinciden en equidad: se publica por los bugs, no por Elo |
| (2.7.3) | ❌ CORTADO SIN RELEASE 2026-07-19 | Campaña doble: 5E singular (4 SPRTs, todos ≤ equidad, −19.7/−12.5 los peores) y 5G historia multinivel (4 builds, −33.9→−10.9→[0.496]→−4.2; tablas por distancia + gravity + gate depth≥6 construidos y probados, el cero final lo causan las bandas duras killers/counter). Motor sigue = 2.7.2. Ambos bloques cerrados |
| 2.7.2 | **+37.9 ±15.0 SPRT agrupado 1103p · +48 ±23 rel LTC · ~2975 ±25 CCRL** | 5D (era 5F) TT redesign: clustering 4×16B/línea de caché, aging por generación (depth−8×edad), eval estático cacheado (+24% nps), flag ttPv pegajoso sin consumidor aún; −19% nodos, WAC 265/300 récord |
| (5C) | ❌ CORTADO 2026-07-18 · **CERRADO 2026-07-23** | Suite LMR de referencia + statScore. Al ritmo real: bundle −9.7, rebuild −25.7, maquinaria statScore −10.8; statScore-en-LMR remedido a 10+0.1 sobre pipeline en 1024avos **−18 Elo, 47.4%, H0**. Lo que SÍ sobrevivió y embarcó: el pipeline en punto fijo (v2.8.3) y los ajustadores ttCapture/ttPv (v2.8.4). Hallazgos permanentes: la granularidad en 1024avos era un prerrequisito no identificado (ya convertida y verificada neutra), y la tabla butterfly está sesgada por construcción (media +71.8 vs mediana −8) |
| 2.7.1 | **+2.9 ± 7.4 SPRT agrupado 4347p · +44 ± 23 rel LTC · ~2970 ± 25 CCRL** | 5B recortado: verificación NMP a depth≥14 (nmpMinPly), NMP fail-soft, término statScore en el margen del RFP + guard eval≥beta, pila statScore ×0.28 medido; WAC 262/300 con −21% nodos d15 / −45% d16. + fixes de mate: ID ya no corta en scores de mate (defensa más larga al perder), UCI `score mate N` |
| 2.7.0 | +4.0 ±27.1 SPRT (parado a 380g, LLR ~0) · **~2965 ±25 CCRL medido** (624g, **+43 ±23 relativo vs +16 de 2.6.9 en campo/TC idénticos — la ganancia de búsqueda CRECE a LTC**) | Improving flag (5A): eval estático por ply, `eval[ply] > eval[ply-2]` modula LMR (+1 ply si empeora), RFP (margen ×(depth−improving)) y LMP (umbral a la mitad si empeora) — Anterior |
| 2.6.9 | +34.3 ±19.5 SPRT · **~2941 ±25 CCRL medido** (624g, +16 ±23 relativo; mismo ancla que 2.6.8 — la ganancia STC se encoge a LTC) | Winnable / factores de escala de final (4I): complexity, almostUnwinnable, OCB, finales de torre, sin dama, factor de material sin peones — Anterior |
| 2.6.8 | +78.4 ±31.5 SPRT · **~2944 ±15 CCRL medido** (gauntlet 1560g campo 2680–3200, +19 ±15 relativo) | Polinomio material imbalance (Romstad, diagonal par zeroed) + retune conjunto valores de pieza CON poly activo (N+20 B+34 R+126 Q+223, BishopPair 67/110) + guardarraíl sostenibilidad bullet — Anterior |
| 2.6.7.1 | +14.3 ±13.5 SPRT · **~2920 ±20 CCRL** (round-robin a ritmo exacto CCRL 40/15, campo 10 motores; anclas limpias Meltdown-2817, Colossus-2862, Tcheran-2917, Pedone-2978 → 2917–2927; KnightX excluido, Pedone confirmado limpio, Velvet-2880 y Ethereal-2901 mislabeled) | Parche timeman (freno de apertura, primer movimiento neutro) + protocolo UCI endurecido (hint de ponder garantizado — freeze de Arena resuelto) — Anterior |
| 2.6.7 | +28.4 ±17.5 SPRT · **2895 ±25 CCRL estimado** | Cadena de estructura de peones de referencia (4G) — Anterior |
| 2.6.6 | +45.8 ±23.1 SPRT · **2880 ±25 CCRL medido** | Peones pasados de referencia (4F) — Anterior |
| 2.6.5 | +19.5 ±13.6 SPRT · **2835 ±25 CCRL medido** | Términos de piezas (4E, outposts exactos) + timeman completo de referencia — Anterior |
| 2.6.4 | **2875 ± 20 MEDIDO** (2728g precisión 2580–2917, 11 rivales; estimaciones ancladas 2847–2899 en 9 rivales fiables excl. Pedantic/Minic outliers) | Anterior |
| 2.6.3 | **2800 ± 25 MEDIDO** (420g precisión 2780–2917, 8 rivales excl. Leorik-2780; estimaciones ancladas por rival 2761–2837) | Anterior |
| 2.6.2 | **2780 ± 20 MEDIDO** (2 gauntlets LTC independientes: 1900g campo 2550–3500 + 811g precisión 2750–2917 por rival) | Anterior |
| 2.5.0 | ~2670 (retro-estimado: 2780 − 103 SPRT; el gauntlet antiguo de 392g vs campo de referencia 2580–2788 dio ~2768 pero ese campo tenía etiquetas mal calibradas) | Anterior |

**Objetivos:**

| Versión | Elo CCRL | Estado |
|---------|----------|--------|
| 2.8.2 | **SPRT H1 +28.0 ±17.2 vs 2.8.1; gauntlet LTC +94 ±23, ~3013 ±30 CCRL** | **Auditoría del search clásico validada.** Correction history y ProbCut se conservan; se cortan la aspiración inicial adaptativa, la extensión de jaque y los bonus continuos killer/counter tras el H0 del primer candidato. 276/276 tests. |
| 2.7.x | ~2975 alcanzado (objetivo era ~2990–3010) | Bloque 5: 5A/5B/5D + quiescence embarcados; **5F ProbCut rescatado y embarcado en 2.8.2**; 5G parcial en 2.8.1/2.8.2 |
| **(campaña 5C, CERRADA 2026-07-23)** | **HECHO — v2.8.3 + v2.8.4** | Pipeline de punto fijo (v2.8.3) + ajustadores sobre él (v2.8.4). Embarcados: **ttCapture +7.1**, **ttPv +7.5**, bundle +9.2 ±9.1 LOS 97.5% vs v2.8.3. Cortados: statScore −18 Elo H0, cutNode −4.0/−7.1 H0 a ambas magnitudes, todos los directos de historia (3 variantes). **Bloque de búsqueda cerrado en v2.8.4; el siguiente paso es el NNUE (bloque 6).** |
| **(revisión de eval, POST-NNUE)** | **aplazado a después del bloque 6** | **King Safety Fase B** y **KingProtector**, movidos detrás del NNUE el 2026-07-23 — ver §🔁 REVISIÓN FINAL al final de este documento. Motivo: la búsqueda sobrevive al NNUE y la evaluación no; su remedio documentado es un retune texel global cuyo producto son parámetros que el bloque 6 sustituye |
| 2.8.1 | ~3000 ±25 CCRL ✅ | Bugfix crítico Syzygy + ordenación 5G — SPRT H1 +14.1 ±10.8 vs v2.7.4 |
| 2.8.0 | ❌ nunca validado | Bloque 9: Syzygy HECHO — bugs de raíz/DTZ corregidos en 2.8.1 |
| **3.0.0+NNUE** | **HECHO — gen3 +4.5 Elo vs clásico; gauntlet LTC pendiente** | **Bloque 6: NNUE HalfKAv2_hm en producción.** Feature transformer + acumulador incremental, red cuantizada, inferencia SIMD, datagen con adjudicación Syzygy, self-play generacional. La red neuronal supera al evaluador clásico. Siguiente: iterar generaciones (gen4+) y calibrar el absoluto CCRL con gauntlet |
| **3.1.0+SMP** | **HECHO (implementación) — ~3150+ esperado con 16 núcleos, gauntlet LTC pendiente** | **Lazy SMP multihilo (v3.1.0).** `Threads=1` byte-idéntico a 3.0.0, escalado de nodos ~7,6× a 8 hilos. Calibración CCRL absoluta con gauntlet a TC largo pendiente |

**Nota de calibración (2026-07-11):** la cifra ~2870 que se manejó para la 2.6.1 era una extrapolación del SPRT STC (tc 10+0.1) sobre un gauntlet con campo mal etiquetado. Las ganancias de eval encogen a LTC. La referencia sólida a partir de ahora son los gauntlets con campos verificados; el ~2780 actual está doblemente confirmado.

---

## ✅ BLOQUE 1 — Búsqueda (v2.3.0)

**Estado: HECHO · Rama: `2.3.0` · SPRT: pasado (+91 ±34 Elo vs 2.2.0)**

El mayor salto de Elo en una sola iteración. Convirtió NoaChess de un engine básico a uno con búsqueda competitiva.

- **Continuation history + counter-move history** — mejora ordenación de jugadas; cada movimiento aprende del contexto de los dos movimientos anteriores.
- **Singular extensions** — detecta la jugada "única buena" y la extiende un ply; evita cortes prematuros en posiciones críticas.
- **LMR afinada por historia** — la reducción Late Move se escala con la puntuación de historia acumulada, no con un valor fijo.
- **Aspiration windows con ensanchado progresivo** — ventana inicial estrecha; se amplía en geometría si falla.
- **Internal Iterative Reductions (IIR)** — reduce depth en nodos sin jugada TT para forzar una jugada TT rápida.
- **ProbCut** — pruning especulativo con búsqueda reducida; evita buscar profundamente posiciones ya claramente malas.

---

## ✅ BLOQUE 2 — Evaluación clásica base (v2.4.0)

**Estado: HECHO · Rama: `2.4.0` · SPRT: pasado (+13 Elo vs 2.3.0)**

- Outposts de caballo (rank 4-6, protegido, sin ataque de peón enemigo).
- Peones pasados avanzados: bloqueador, pasados conectados, torre detrás del pasado.
- Espacio (control del centro ponderado por fase).
- **Tuning texel completo** — tuner propio de coordenadas descendentes sobre partidas self-play. Valores PeSTO como punto de partida, ajustados al motor. 50K partidas / 4.42M posiciones, seed 20250709, K=0.9125.

**Lecciones permanentes:**
- Movilidad NUNCA se tunea con texel — converge a valores EG negativos por correlación espuria (el bando ganador simplifica). Excluida de ParameterRegistry permanentemente.
- Vigilar NPS después de cada nuevo término — las nuevas piezas costaron ~13% NPS hasta cachear los bitboards de pasados en la caché de peones.

---

## ✅ BLOQUE 2.5 — Evaluación clásica fina (v2.4.5)

**Estado: HECHO · Rama: `2.4.5` · SPRT: pasado (+12 Elo vs 2.4.0)**

**Fase A (implementada y tuneada):**
- **Tempo** — bonificación por tener el turno.
- **Phalanx / connected pawns** — bonificación por peón amigo en la misma fila y archivo adyacente; indexada por rango relativo.
- **Backward pawns** — penalización si la casilla stop está atacada por peón enemigo y no hay peón amigo al mismo nivel o detrás en archivos adyacentes; exclusiva de isolated (corregido en 2.5.0).

**Fase B — DESCARTADA (v2.4.6):**
King safety. Resultado: −77 Elo (safe checks con máscara solo de peones inundaba la curva de peligro). Máscara estricta: 0 Elo. Decisión permanente: implementar shelter/storm en Bloque 4D con código de referencia, sin safe checks hasta nueva evaluación.

**Fase C — DESCARTADA antes de intentar:**
TrappedRook + material imbalance. Movido a Bloques 4E y 4H.

---

## ✅ BLOQUE 3 — Velocidad / Movegen (v2.5.0)

**Estado: HECHO · Rama: `2.5.0` · SPRT: pasado (+101 Elo vs 2.4.5)**

El mayor salto en Elo del proyecto hasta la fecha.

- **Generación de jugadas por etapas (staged movegen):** jugada TT primero (validada con IsPseudoLegal), capturas, silenciosas, capturas perdedoras al final.
- **Lazy legality:** generación pseudo-legal + comprobación al ejecutar. Elimina la comprobación adelantada de legalidad.
- **PEXT / BMI2 con CPUID guard:** PEXT activo en Intel y Zen3+ (familia ≥ 0x19). Desactivado en Zen+ / Zen2 (familia 0x17 — Threadripper 2950X) donde PEXT es microcodificado y lento. Guard via `ComputeUsePext()`.

**Bugfixes de evaluación incluidos en 2.5.0 (post-SPRT):**
- Backward: `supportMask` ahora incluye el mismo rango (miembro de phalanx nunca es backward).
- Backward: exclusivo de isolated (se evita doble penalización).

---

## ✅ BLOQUE 4 — Evaluación clásica de nivel referencia (v2.6.x) — COMPLETO

**Estado: COMPLETO (2026-07-16) · 4A (v2.6.0) · 4B threats (v2.6.1, +103) · 4C mobility (v2.6.2, +6.6) · 4D king safety (v2.6.3, +76.9) · 4D.5 timeman (v2.6.4) · 4E piece terms (v2.6.5, +19.5) · 4F passed pawns (v2.6.6, +45.8) · 4G pawn chain (v2.6.7, +28.4) · 4G.1 timeman/UCI (v2.6.7.1, +14.3) · 4H imbalance (v2.6.8, +78.4) · 4I winnable (v2.6.9, +34.3) · Total del bloque: ~2670 → ~2941 CCRL medido**

Objetivo: replicar sistemáticamente una evaluación clásica de referencia. Cada sub-bloque es un SPRT independiente. No mezclar más de un término por SPRT salvo que sean prereqs entre sí.

**Elo total estimado del bloque: +120–160 Elo**

---

### ✅ 4A — Infraestructura attackedBy (v2.6.0) ⚠️ PREREQ OBLIGATORIO

**Estado: HECHO · Rama: `2.6.0` · Neutral en evaluación (nodos idénticos), coste NPS ~2-3%**

**Elo: habilitador, no directo · Esfuerzo: Medio**

Prerequisito para threats (4B), king safety (4D) y movilidad mejorada (4C). Sin esto no se puede implementar nada de lo que sigue en el bloque 4.

- Añadir pass de inicialización al principio de `ClassicalEvaluator.Evaluate()` (equivalente a la referencia `initialize<Us>()`).
- `attackedBy[color][pieceType]` — bitboard de casillas atacadas por cada tipo de pieza de cada color.
- `attackedBy2[color]` — casillas atacadas por **dos o más** piezas del mismo color (doble ataque). Esencial para threats y king safety.
- Estos bitboards se reutilizan en todos los términos siguientes; el coste se amortiza enseguida.
- Benchmark NPS antes/después: se espera ~2-4% de coste. Si es mayor, revisar.

---

### ✅ 4B — Amenazas / Threats (v2.6.1)

**Estado: HECHO · Rama: `2.6.1` · SPRT: pasado (+103 ± 35 Elo vs 2.5.0, llr 2.99, H1 en 243 partidas, 64.4%)**

Muy por encima del estimado (+25-35): el mayor salto de evaluación del proyecto. Lección crítica incorporada a las reglas de oro: los valores de referencia van SIEMPRE reescalados ×0.48 (el primer intento con valores en crudo tendía a llr −1.09).

**Elo estimado: +25–35 (real: +103) · Esfuerzo: Medio**

NoaChess tiene **cero términos de amenazas**. Es el mayor gap individual de evaluación.

| Término | Valor ref | Descripción | Prioridad |
|-----------|---------|-------------|-----------|
| `ThreatBySafePawn` | S(167, 99) | Peón amigo seguro atacando pieza enemiga no-peón | ALTA |
| `Hanging` | S(72, 40) | Pieza enemiga débil y desprotegida | ALTA |
| `ThreatByMinor[victim]` | hasta S(81,163) | Menor (caballo/alfil) atacando pieza defendida o débil | ALTA |
| `ThreatByRook[victim]` | hasta S(60,39) | Torre atacando pieza débil | MEDIA |
| `ThreatByKing` | S(24, 87) | Rey atacando pieza débil en el final | MEDIA |
| `ThreatByPawnPush` | S(48, 39) | Avance de peón seguro amenazando pieza enemiga el próximo movimiento | MEDIA |
| `RestrictedPiece` | S(6, 7) | Movimientos enemigos restringidos por nuestro control | BAJA |
| `WeakQueenProtection` | S(14, 0) | Pieza débil solo defendida por la dama | BAJA |
| `KnightOnQueen` | S(16, 11) | Caballo bifurcando o amenazando la dama | BAJA |
| `SliderOnQueen` | S(62, 21) | Piezas deslizantes con doble ataque sobre la dama enemiga | BAJA |

Implementar en orden de impacto: ThreatBySafePawn → Hanging → ThreatByMinor → resto.

---

### ✅ 4C — Movilidad no-lineal (v2.6.2) — HECHO

**Estado: HECHO (tablas de referencia ×0.48 re-centradas, x-ray, área de referencia, restricción de clavadas) · SPRT vs 2.6.1: +6.6 ± 11.5 Elo, LOS 87%, 2000 partidas (cotas no alcanzadas; se mantiene: infraestructura prerequisito de 4D/4E)**

**Lección (regla de oro adicional):** las tablas de referencia llevan un offset positivo grande en la movilidad típica (torre +59 eg, dama +63 eg) que la referencia absorbe en sus valores de pieza tuneados conjuntamente. Toda tabla de referencia que se porte debe RE-CENTRARSE (restar la entrada en la cuenta típica) para no inflar el balance de material texel-tuneado de NoaChess.

**Elo estimado: +20–30 · Esfuerzo: Medio**

El modelo lineal actual (`MobilityStep * (moves - baseline)`) pierde Elo porque pasar de 2→3 casillas para un caballo vale 5× más que pasar de 7→8. la referencia usa una tabla lookup de 32 entradas por pieza (MG+EG).

- Reemplazar con `MobilityBonus[pieceType][moveCount]` — array indexado por número de casillas.
- **Mejorar área de movilidad:** excluir también casillas del rey propio y la dama propia, y piezas clavadas al rey.
- **Ataques x-ray:** los alfiles "ven a través" de la dama propia; las torres ven a través de la dama propia y otras torres propias.
- **NO tunear** estos valores con texel tuner — copiar directamente de la referencia `MobilityBonus[]`.

---

### ✅ 4D — Shelter / Storm + King Safety completa (v2.6.3)

**Estado: HECHO · Rama: `2.6.3` · SPRT: pasado (+76.9 ±31.2 Elo vs 2.6.2, LOS 100%, H1 en 335 partidas)**

**Elo estimado: +15–30 · Esfuerzo: Medio-Alto, `pawns.cpp:231–297`**

Intento anterior (v2.4.6) fracasó por bug en safe checks. Estos componentes se implementan sin safe checks en esta versión.

**Componentes de shelter/storm (cacheable en pawn hash):**
- `ShelterStrength[4][8]` — tabla de puntuación por distancia del peón de cobertura al rey, para cada archivo relativo (0..3) y rango (0..7).
- `UnblockedStorm` — penalización por tormenta de peones enemigos sin bloquear, indexada por rango.
- `BlockedStorm` — penalización reducida cuando el peón de tormenta está bloqueado.
- `KingOnFile` — penalización si el rey está en archivo semiabierto o abierto con peón enemigo.
- **Pre-castling evaluation** — calcular shelter en la posición post-enroque y tomar el máximo con el actual.
- **EG king-pawn proximity** — shelter − 16 × minPawnDist en el final (el rey debe acercarse a sus peones).

**Componentes adicionales de king safety (fuera de la pawn cache):**
- `attackedBy2` en la zona del rey — `kingAttacksCount` (doble ataques a la zona del rey).
- `Weak squares in king zone` — 183 × popcount de casillas débiles en la zona del rey.
- `King flank attack / defense` — 3 términos: ataque al flanco, ataque² al flanco, defensa del flanco.
- `Blockers for king` — +98 por pieza bloqueadora (clavada) que protege al rey.
- `PawnlessFlank` — penalización cuando el rey está en un flanco sin peones propios.
- `FlankAttacks` — penalización escalada por ataques al flanco del rey.
- `Knight adjacency bonus` — −100 unidades de peligro por cada caballo defensor cerca del rey propio.
- `BishopOnKingRing` — +24 MG por alfil enemigo apuntando a la zona del rey.
- `RookOnKingRing` — +16 MG por torre enemiga en el mismo archivo que la zona del rey.
- **No-queen discount** — reducir el peligro en −873 unidades si el atacante no tiene dama.

⚠️ Benchmark NPS antes/después. Si cuesta >5% NPS, revisar cuáles componentes son los más caros y aislar.

---

### ✅ 4D.5 — Time Management adaptativo (v2.6.4) — HECHO Y **SUPERSEDIDO POR v2.6.5**

**Estado: CERRADO. No es trabajo pendiente y NO se rehace.** Llevaba un 🔄 —el único del documento, frente a 16 ✅ y 2 ❌— que era un resto de cuando estuvo en el aire. Corregido el 2026-07-23.

**No se marca en verde "a secas" porque de esta sub-versión no sobrevive ni una línea en el código** (verificado 2026-07-23): el incremento al 85% lo sustituyó el plegado completo de la referencia (`inc * (mtg - 1)`; el propio `TimeManager.cs` dice *"instead of the flat per-move percentage of earlier versions"*), el horizonte adaptativo por ply **nunca llegó a ejecutarse** y su constante saboteadora `AssumedMovesToGo` ya ni existe, y la extensión por inestabilidad se revirtió a −5.7 Elo antes de que v2.6.5 trajera los factores dinámicos de la referencia, que sí la implementan y sí miden. Rehacerlo sería reinstalar un scheduler casero encima de un port de referencia medido: ir hacia atrás.

**Rama: `2.6.4` · Sin SPRT completado (ver nota) · Gauntlet LTC: 2875 ± 20 CCRL medido (2728g, campo 2580–2917)**

**Elo estimado: +0–10 · Esfuerzo: Bajo · Archivo: `TimeManager.cs` + `AlphaBetaSearch.cs`**

El gestor de tiempo anterior dejaba ~1:50 sin usar en partidas a 2+6 y usaba solo el 50% del incremento. Cambios finales:

- **Incremento al 85%** — `inc / 2` → `inc * 85 / 100`. Es la mejora principal: el 15% restante queda de margen de seguridad. La 2.6.3 banqueaba la mitad del incremento sin motivo.
- **Horizonte adaptativo conservador** — divisor por ply `clamp(52 - pow(ply+3, 0.45)*2.2, 38, 52)` (≈48 en apertura → ≈38 en medio juego) en lugar del 25 fijo. El presupuesto por jugada es una fracción pequeña del reloj (~2%), igual que produce la fórmula de optimum de un motor fuerte.

**REVERTIDO — extensión por inestabilidad del best move (primer intento).** Multiplicaba el soft por `1 + 1.7*totBestMoveChanges` (+ falling-eval) y eliminaba el corte predictivo. **Regresó −5.7 ±11.8 Elo (H0, LOS 17%)** y en bullet gastaba ~16 s en la 1ª jugada de un 2+1: multiplicaba una base ya grande (el slice fijo `clock/horizonte`) por factores que en la fórmula de referencia parten de un estado estable de ~0.5×optimum.

**SUPERSEDIDO POR v2.6.5:** el gestor completo de referencia (timeman.cpp + factores dinámicos de search.cpp) reemplaza este scheduler. Nota post-mortem: el horizonte adaptativo por ply de esta versión NUNCA llegó a ejecutarse en partidas reales — `EngineProfile.AssumedMovesToGo = 25` (fijo) lo sobreescribía silenciosamente en UciLoop, así que la 2.6.4 jugó siempre con `clock/25 + 85% inc` (de ahí la 1ª jugada de varios minutos a 40/2h: soft ~4.8 min, hard ~19 min). La ganancia medida (+75 a LTC) vino del 85% del incremento.

---

### ✅ 4E — Términos de piezas faltantes + timeman de referencia (v2.6.5) — HECHO (REVISADO)

**Estado: HECHO Y REVISADO · SPRT vs 2.6.4: +19.5 ±13.6 Elo, LOS 99.7%, H1 aceptada · 2835 ±25 CCRL medido (2 gauntlets LTC, 880 partidas limpias) · 141 tests verdes**

**Elo estimado: +15–20 → medido +19.5.** Nota sobre el ancla absoluta: los 2835 salen ~40 por debajo de los 2875 de la 2.6.4 pese al SPRT positivo — es un artefacto de re-anclaje del field (5 rivales del gauntlet A tenían etiquetas falsas y se excluyeron: Counter 3.8, Mr Bob 0.9.0, Tucano 8.00, Meltdown 1.10, Minic 1.09). La señal relativa fiable es el SPRT.

**Revisión (2026-07-13).** El primer intento quedó por DEBAJO de la 2.6.4 en el gauntlet ancho (−167 vs −159 relativo). Causas encontradas y corregidas contra evaluate.cpp:

1. **Outposts no fieles.** El primer intento trataba CUALQUIER peón enemigo del cono como expulsor; la referencia usa `pawn_attacks_span`, que **excluye los peones enemigos bloqueados y retrasados** (nunca podrán avanzar a expulsar) — la versión antigua concedía muchos menos outposts. Además faltaba la alternativa de escudo (`shift<Down>(pawns)`: casilla con un peón delante cuenta aunque no esté protegida por peón propio) y el outpost se calculaba en una segunda pasada con ataques planos en vez de usar el bitboard real del bucle de piezas (x-ray a través de damas, restricción de clavada). Ahora es exacto y las casillas de outpost + span se calculan en la caché de peones (inputs solo-peones, coste ~0).
2. **KingProtector desactivado (evidencia del gauntlet largo):** sobre PSTs PeSTO duplica la distancia al rey y su Eg cancela los outposts. No reactivar sin SPRT.
3. **KnightOutpost conserva el valor texel S(51,18)** (bajarlo al ×0.48 genérico perdió Elo medido); `BishopOutpost` escalado por el mismo ratio → S(29,13).
4. **Timeman completo de referencia** (adelantado desde 5H, pedido explícito): `TimeManagement::init` textual (optimum/maximum, dos formas de TC, incremento completo plegado en el horizonte) + factores dinámicos por iteración (`fallingEval` con deltas ×2.08 a unidades internas, `timeReduction` 1.37/0.65 con arrastre entre jugadas, `bestMoveInstability`). El estado estable de la fórmula es ~0.5×optimum — por eso el intento de 2.6.4 (que multiplicaba el slice fijo) regresó y esto no. MoveOverhead default 100→30 (la fórmula lo reserva ×52 y con 100 colapsaba los finales de bullet a jugadas instantáneas). `AssumedMovesToGo` eliminado del perfil.

| Término | Valor ref | Pieza | Descripción |
|---------|---------|-------|-------------|
| `TrappedRook` | S(55,13) × (1 + !canCastle) | Torre | Torre con ≤3 casillas de movimiento — error posicional grave |
| `RookOnClosedFile` | S(10,5) penalización | Torre | Torre en archivo bloqueado por peón propio |
| `BishopPawns` | −3 a −24 MG por peón | Alfil | Peones propios en el color del alfil × distancia al borde |
| `BishopXRayPawns` | S(4,5) por peón | Alfil | Peones enemigos en la diagonal del alfil |
| `LongDiagonalBishop` | S(45,0) | Alfil | Alfil que ve ambas casillas centrales a través de peones |
| `KingProtector` | S(7,9) / casilla | Alfil, Caballo | Penalización por distancia al rey propio |
| `MinorBehindPawn` | S(18,3) | Alfil, Caballo | Bonificación cuando un peón está directamente delante del menor |
| Outpost alfil | S(31,25) | Alfil | Alfil en outpost (rango 4-6, protegido, sin ataque de peón) |
| `ReachableOutpost` | S(33,19) | Alfil, Caballo | Menor que puede alcanzar un outpost el próximo movimiento |
| `UncontestedOutpost` | S(0,10)/peón | Caballo | Caballo en ala sin objetivos rivales |
| `WeakQueen` | S(57,19) penalización | Dama | Dama atacada por piezas deslizantes o clavadas |

**Notas de implementación (final):** `BishopPawns` fiel = `BishopPawns[edgeDist] × peonesMismoColor × ((noProtegidoPorPeón?1:0) + peonesPropiosBloqueadosEnColumnasCentrales)`. `WeakQueen` reutiliza la lógica de snipers/`Between` de las clavadas del rey (dama = único bloqueador entre torre/alfil enemigo y un objetivo). `UncontestedOutpost` sólo caballo, en ala (a/b/g/h), EG, por peón (de ambos colores) en el ala. Toda la cadena de outposts vive dentro del bucle de piezas (usa el bitboard de ataques real, x-ray + clavadas) y las casillas de outpost + `pawnAttacksSpan` se calculan en la caché de peones.

---

### ✅ 4F — Peones pasados mejorados (v2.6.6) — HECHO

**Estado: HECHO · SPRT vs 2.6.5: +45.8 ±23.1, LOS 100%, H1 aceptado · 2880 ±25 CCRL medido (450g, 8 anclas fiables; Patricia-3027 confirmada outlier ~3290 y excluida) · 148 tests verdes · NPS sin cambio (613k vs 598k)**

**Elo estimado: +12–18 (real: +45.8) · Esfuerzo: Bajo-Medio**

Implementado (fiel a la referencia, evaluate.cpp `passed()` + pawns.cpp):

- **Definición de pasado de referencia** (en la caché de peones): (a) solo stoppers-lever, o (b) solo lever-pushes con phalanx que los iguala/supera, o (c) candidato bloqueado en fila relativa 5+ con peón de apoyo que puede subir con seguridad. Sustituye al test simple de máscara de cono. Nunca pasado si hay peón propio delante en la misma columna.
- **Blocked passer filter** (segunda pasada, piece-aware): el candidato bloqueado solo conserva el bonus si un peón amigo puede ofrecerse en cambio (casilla de avance vacía y no doblemente atacada salvo defensa propia); si no, devuelve el bonus de fila concedido por la caché. Sustituye a la penalización simple enemy-on-stop (BlockedPasserDivisor eliminado).
- **Proximidad de reyes al bloqueo** — `+prox(Them)·19/4·w − prox(Us)·2·w` (Eg), más cobertura del segundo avance si blockSq no es la casilla de coronación. `w = 5·rank − 13`, filas 4+.
- **Escalera de seguridad del camino** — k = 36/30/17/7/0 (+5 si blockSq defendido o torre/dama propia detrás); torre/dama enemiga detrás del pasado disputa todo el span. `(k·w, k·w)` en unidades de referencia, ×0.48 por peón al final.
- **`PassedFile`** — S(6,4) por distancia al borde (S(13,8)×0.48), registrado en el tuner.
- Se conserva el Tarrasch `RookBehindPasser` texel-tuned de NoaChess (complementa el k+5).

---

### ✅ 4G — Estructura de peones adicional (v2.6.7) — HECHO

**Estado: HECHO · SPRT: +28.4 ±17.5 Elo, LOS 99.9%, H1 aceptado · 153 tests verdes · NPS sin cambio (todo en la caché de peones)**

**Elo medido: +28.4 Elo SPRT · 2895 ±25 CCRL estimado (8 anclas 2841–2970, media 2894)**

Implementado (fiel a la referencia, pawns.cpp `evaluate()`, todo ×0.48). La clave: el scoring por peón de la referencia es una cadena de ramas MUTUAMENTE EXCLUYENTES (connected / isolated / backward), no una suma de términos independientes — se portó la cadena completa y se retiraron los términos aditivos antiguos (DoubledPawn por columna, IsolatedPawn, Phalanx[], BackwardPawn texel-tuned).

| Término | Valor ref | Estado | Descripción |
|---------|---------|--------|-------------|
| Fórmula Connected completa | `Connected[r]·(2+phalanx−opposed) + 22·support`, eg `v·(r−2)/4` | ✅ HECHO | En unidades de referencia crudas, ×0.48 al final; sustituye al Phalanx[] simple |
| `WeakUnopposed` | S(15,18) → S(7,9) | ✅ HECHO | Sobre Isolated/Backward con columna libre delante (backward solo fuera de a/h) |
| `WeakLever` | S(2,57) → S(1,27) | ✅ HECHO | Peón sin apoyo atacado por dos peones enemigos |
| `DoubledEarly` | S(17,7) → S(8,3) | ✅ HECHO | Doblado mientras ningún peón enemigo está fijo |
| `BlockedPawn` filas 5-6 | {S(−19,−8), S(−7,3)} → {(−9,−4),(−3,1)} | ✅ HECHO | Peón propio bloqueado avanzado limita al rival |
| `Doubled` semántica ref | S(11,51) → S(5,25) | ✅ HECHO | Peón propio JUSTO detrás y sin apoyo (no el conteo por columna) |
| `Isolated` / `Backward` ref | S(1,20)/S(6,19) → S(0,10)/S(3,9) | ✅ HECHO | Los valores texel antiguos describían otros eventos (ramas distintas) |

---

### ✅ 4H — Material imbalance (v2.6.8) — HECHO

**Estado: IMPLEMENTADO · SPRT vs v2.6.7.1: +78.4 ±31.5 Elo, LOS 100%, H1 aceptado @ 284g [0.611] · Gauntlet LTC: ~2944 ±15 CCRL (13 anclas 2680–3200, 1560g, +19 ±15 relativo)**

Dos intentos anteriores fallaron (SPRT a: −30 @ 440g, b: ±0 @ 250g) porque los valores de pieza texel-tuned habían absorbido las sinergias medias del polinomio. La ruta de rescate documentada: retune texel conjunto de los valores de pieza CON el polinomio activo, de modo que el tuner reparta el trabajo entre ambos. Ejecutada con un offset único igual por pieza (mg=eg) para evitar el valle degenerado (queen → 1841/664 en el primer intento libre). Offsets convergidos sobre PeSTO: N+20, B+34, R+126, Q+223; BishopPair S(44,68) → S(67,110).

- Polinomio de Romstad segundo grado: sinergias propias (`QuadraticOurs`) e interacciones enemigas (`QuadraticTheirs`). Par de alfiles = "pieza extendida" índice 0 con entrada diagonal `[0][0]` zeroed (el término explícito `BishopPair` textex-tuneado sigue siendo el propietario del valor del par; la diagonal del polinomio solo añade las interacciones del par con el resto del material).
- Factor combinado ×3/100 (referencia /16 × ×0.48 NoaChess). Diferencia pura Blancas−Negras: exactamente cero para material simétrico.
- Caché direct-mapped 8192 slots con hash Fibonacci sobre los diez conteos de piezas; solo se recalcula en capturas y promociones (~2.4% NPS).

### ✅ 4H.1 — Parche timeman: guardarraíl de sostenibilidad bullet (v2.6.8)

**Estado: IMPLEMENTADO · No-regresión confirmada (cut @ 420g [0.509], +6.9 ±23.7 Elo, LOS 71.7%)**

Espiral de muerte en bullet de Arena (2+1): rápido en la apertura, 3-4s/mov al desvanecerse el freno de apertura (sangrando 2-3s netos por jugada contra +1s de incremento), y 1-2s/mov (¡deadline duro ~4s!) con 5s en el reloj — pérdidas por tiempo en posiciones ganadas. Causa raíz: la fórmula de referencia pliega 49 incrementos futuros en el tiempo usable y su único freno es el tope del 20% del reloj restante, que deja decaer el reloj geométricamente en vez de estabilizar el gasto en torno al incremento.

- Guardarraíl (solo rama sudden-death): objetivo ≤ `inc + reloj/16`, deadline duro ≤ `inc + reloj/4 − overhead`. Relojes sanos intactos (los umbrales quedan por encima de la curva de referencia hasta que el reloj cae); en apuros el gasto converge al incremento (2+1 con 5s: deadline 3.96s → 2.22s).
- La rama movestogo (controles clásicos tipo 40/900) NO se toca — el comportamiento a ritmo CCRL está validado tal cual.

---

### ✅ 4I — Factores de escala / Winnable (v2.6.9) — HECHO

**Estado: HECHO (2026-07-16) · SPRT vs 2.6.8: +34.3 ±19.5 Elo, LOS 100%, H1 aceptado @ 580g [0.549] · Gauntlet LTC: ~2941 ±25 CCRL (624g, +16 ±23 relativo — mismo ancla absoluta que 2.6.8, la ganancia STC se encoge a LTC dentro del error) · 135 tests verdes**

**Elo estimado: +8–15 (en el final) · Esfuerzo: Alto**

Port fiel de `winnable()` (evaluate.cpp) + el factor drawish del material entry (material.cpp), aplicado al score total White-relative justo antes de la interpolación de fase:

- **Complexity metric** — `9·pasados + 12·peones + 9·outflanking + 21·ambosFlancos + 24·infiltración + 51·finalPuroDePeones − 43·almostUnwinnable − 110`, en unidades de referencia crudas y convertido ×0.48 una sola vez (los topes de mg/eg son centipawns NoaChess). Solo puede reducir el mg; empuja el eg en ambos sentidos; nunca cambia el signo de ninguno.
- **`almostUnwinnable`** — reyes cruzados (outflanking < 0) con todos los peones en un flanco → −43 de complexity.
- **Scale factors** (el eg de la mezcla se multiplica por sf/64, ratios adimensionales SIN ×0.48): factor de material primero — bando fuerte sin peones y ≤ un alfil de ventaja: sf=0 por debajo de torre (KK/KBK/KNK), 4 contra menor solo (KRKB/KRKN), 14 el resto (KmmKm). Heurísticas generales si no: OCB puro `18+4·pasadosFuertes`; OCB con más material `22+3·unidadesFuertes`; final de torre única con ≤1 peón de ventaja, peones fuertes en un flanco y rey débil defendiendo → 36; dama vs sin dama `37+3·menoresSinDama`; resto cap `36+7·peonesFuertes` (−4 extra a flanco único); −4 final en toda rama con todos los peones en un flanco (la rama por defecto acumula −8, verificado carácter a carácter contra la fuente de la referencia).
- **Fuera de alcance (documentado):** funciones especializadas de finales (KXK, KBPsK, KQKRPs, KPsK, KPKP, KNNK) — las cubrirá Syzygy (Bloque 9).
- **Perf:** sin caché — unos popcounts por Evaluate; wall time depth 16 idéntico (1.23s vs 1.22s).
- **Tests:** todas las ramas del scale factor fijadas a mano + pipeline complexity/interpolación end-to-end + KBK casi-tablas + simetría de color.
- **Incluido en v2.6.9 — crédito de tiempo en ponderhit:** el relanzamiento tras ponderhit arrancaba una búsqueda nueva con presupuesto COMPLETO ignorando lo ya ponderado (en Lichess: 30s en respuestas casi forzadas, nunca respuesta instantánea, reloj sangrando frente a bots que mueven al instante). La referencia ancla su reloj en el "go ponder"; ahora el relanzamiento lleva `ElapsedOffsetMs` descontado de cada check soft/hard (suelo de 100ms de hard). Verificado por cable: ponder de 6s → bestmove 30ms tras ponderhit (antes ~4s). Invisible para SPRT/gauntlets (cutechess juega sin ponder) — ganancia pura en juego con ponder.

---

## BLOQUE 5 — Búsqueda de nivel referencia (v2.7.x)

**Estado: CERRADO en v2.8.4.** Embarcaron 5A, 5B recortado, 5D, el rework de quiescence, 5F ProbCut, la gravedad de historia (v2.8.2/v2.8.3), el pipeline LMR en punto fijo (v2.8.3) y los ajustadores ttCapture/ttPv (v2.8.4). En 2.8.2 continuation history adopta gravedad conservando las bandas duras killer/counter (sustituirlas por bonus continuos falló H0). El resto de la suite de búsqueda de referencia no transfiere a este motor clásico. **El siguiente bloque es el NNUE (bloque 6, v3.0.0).**

---

### ✅ 5A — Improving flag (v2.7.0) — HECHO (+4.0 ±27.1 SPRT STC · +43 ±23 relativo en gauntlet LTC, ~2965 ±25 CCRL)

**Elo estimado: +5–8 · Esfuerzo: Muy bajo**

Una variable booleana que la referencia pasa a varios sitios simultáneamente. Mínimo código, múltiple impacto.

```
improving = staticEval[ply] > staticEval[ply-2]  // false si cualquiera estaba en jaque (sentinel NoEval)
```

Implementado (2026-07-16):
- **LMR** — si not improving, reducir un ply más (el uso de mayor impacto del flag).
- **RFP (futility margin)** — `85 × (depth − improving)`: la forma de referencia `165 × (depth − improving)`, ya en su equivalente ×0.48 (85/ply).
- **LMP** — umbral `3 + d²` a la mitad si not improving.
- **NMP** — deliberadamente NO tocado: la condición de entrada refinada (que también consume el flag) es alcance de 5B.
- Eval estático por ply en `_stackEval[]` con sentinel `NoEval` para nodos en jaque.

**Resultado:** +4.0 ±27.1 STC (SPRT parado a 380g con LLR ~0 — ganancia real pero pequeña a 10+0.1), pero **+43 ±23 relativo en el gauntlet LTC vs los +16 de 2.6.9 en campo y TC idénticos**: la ganancia de búsqueda CRECE con el TC (patrón opuesto a los términos de eval). ~2965 ±25 CCRL — primera versión medida por encima de la meseta 2941–2944.

**Lección (2026-07-16):** los features de búsqueda se validan mejor en LTC — el SPRT STC infravalora las mejoras de pruning/reducción porque su precisión se compone con la profundidad. Para el resto del bloque 5: no descartar un feature por un STC plano sin mirar el gauntlet.

**Auditoría de campo (3 gauntlets cruzados):** renombrados Ethereal 2756→2910, Inanis 2997→2905, Bit-Genie 3101→3010 (desviaciones consistentes en las tres tiradas); Meltdown-2817 limpio; Marvin-3000 y Winter-3200 en observación.

---

### ✅ 5B — NMP y futility refinados (v2.7.1) — ALCANCE RECORTADO POR MEDICIÓN — HECHO

**SPRT vs v2.7.0 (dos runs AGRUPADOS): +2.9 ± 7.4 Elo a 4347 partidas [0.504]** (run 1 parado estable a 1398p [0.517] +11.8; run 2 a término H0 a 2949p [0.498] −1.3; el A/B de control entre ambos builds — con/sin fix de mate — dio [0.500] a 1743p: mismo motor, el agrupado es la cifra honesta y el run 1 fue cola alta del ruido). **Gauntlet LTC: +44 ± 23 relativo al campo (56.3%, 624 partidas) → ~2970 ± 25 CCRL** — por la lección de la 5A, la señal de calidad de un bloque de búsqueda es el LTC, no el STC. Sin renombrados de campo este ciclo (Marvin-3000 y Dumb-2856 en observación).

**Además, dos arreglos de búsqueda de mate encontrados en partida de Arena (Noa perdida rechazó comer una dama que llevaba a mate-en-8 y se metió en el mate-en-4):**
- El iterative deepening rompía en cuanto una iteración devolvía score de mate (`|score| > MateBound → break`). Con mate EN CONTRA, las iteraciones profundas son justo las que encuentran defensas más largas (el final de torre mated-in-8 necesita 16 plies). También explicaba el "regala las piezas cuando está perdida". La referencia nunca corta por mate: el reloj termina la búsqueda. Verificado: la defensa KRK sigue profundizando d8→d22+; WAC 259→262 (seguir tras un mate encontrado también acorta mates propios).
- UCI reportaba mates como `score cp ±99xxx` en vez de `score mate N` (violación de protocolo; evals absurdos en GUI y riesgo en adjudicación).

**Elo estimado original: +5–8 · Lo aprendido: el NMP de referencia NO es portable todavía**

**Historia (2026-07-17):** el bundle completo de referencia se implementó fielmente (condición de entrada con margen improvement/complexity, filtro statScore, R profunda, verificación, capture futility, quiet futility por lmrDepth — todo ×0.48 donde tocaba y recalibrado ×0.28 medido en los umbrales de historia) y el SPRT lo tumbó: **[0.451], −34 Elo a 143 partidas**. La disección con WAC-300 + benches de nodos en siete builds identificó TRES dependencias de ecosistema:

1. **La R profunda necesita una quiescence fiable.** Las nulls de referencia aterrizan en qsearch desde depth 3–7 y la nuestra devolvía puntuaciones falsas allí (WAC 249/300; el mate de WAC.001 pasó de d13 a invisible >d17/100M nodos; verificación desde d8 ni recupera táctica ni conserva nodos). → retomar tras el bloque de corrección de quiescence.

   ⚠️ **CORRECCIÓN (2026-07-19, auditoría del usuario):** este punto decía que "SU qsearch genera JAQUES en el primer ply". **Es FALSO** y la premisa se propagó a cinco decisiones posteriores (gates de 5E multi-cut, small ProbCut de 5F, etc.). El `search.cpp` de la referencia dice literalmente *"we presently use two stages of move generator in quiescence search: captures, or evasions only when in check"*: fuera de jaque genera capturas, en jaque evasiones completas. **No genera jaques tranquilos.** La diferencia real era otra: en jaque arranca `bestValue = -VALUE_INFINITE`, lo que deja muerto todo su bloque de poda y le hace buscar TODAS las evasiones (incluidas las tranquilas) y detectar mate — que es exactamente lo que a la nuestra le faltaba. No implementar nunca una qsearch de jaques silenciosos alegando fidelidad a la referencia, porque no lo sería.
2. **La entrada condicionada por eval necesita un eval preciso.** Exigir `staticEval >= beta` infló el árbol ~30% a táctica igual: nuestro eval clásico es ruidoso respecto a la búsqueda y las probes con eval<beta siguen encontrando cortes reales. → retomar con NNUE.
3. **La futility por lmrDepth necesita las reducciones grandes de referencia** (su lmrDepth es sistemáticamente menor) — y **los márgenes de pruning NO llevan ×0.48**: los crudos reproducen nuestros márgenes validados (d3: 251 vs 300; d4: 396 vs 400); los escalados podan doble y ciegan. → 5C.
4. Capture futility sin test de jaque poda sacrificios de captura (−6 WAC); su forma de referencia necesita además captureHistory. → 5G.

**Lo que SÍ embarca la v2.7.1** (sobre el NMP viejo intacto):
- **Pila statScore** — `2×butterfly + contHist − 1250` por ply (umbrales ×0.28: ratio medido entre nuestras tablas depth² sin gravedad y las gravity-capped de referencia; probe: butterfly p99 3218, contHist p99 630 vs caps 14365/29952).
- **Término statScore en RFP** — `staticEval − 85×(depth−improving) − statScore[ply−1]/180 >= beta` + guard `staticEval >= beta`: tras una jugada del padre refutada el corte estático llega antes; tras una de gran reputación exige margen. **Fuente principal del ahorro de nodos.**
- **Verificación NMP a depth ≥ 14** con `nmpMinPly = ply + 3(depth−R)/4` (anti-zugzwang, Fine 70 ✓).
- **NMP fail-soft** (devuelve nullScore, no beta) + guard de mate conservado (rango de mate cae a búsqueda real).
- **improvement** con fallback ply−4 tras jaques; default en frío ESTRICTO (el +173 de referencia relaja LMR/LMP en todo nodo frío y infla el árbol +36% medido).

**Resultado medido:** WAC 258–265/300 vs 259 de 2.7.0 (igual dentro del ruido ±5), nodos d15 2.92M vs 3.72M (−21%), startpos d16 2.25M vs 4.10M (−45%), mate WAC.001 a d13 (paridad), Fine 70 ✓, 138 tests.

---

### ❌ 5C — LMR adjuster suite + statScore (era v2.7.2) — MEDIDO Y CORTADO (2026-07-18)

**Todo el contenido de 5C mide NEGATIVO al ritmo real. Cortado por la regla de decisión del proyecto (como king-safety Fase B). La búsqueda quedó revertida a la 2.7.1 exacta y el número de versión 2.7.2 libre para el siguiente bloque.**

Los números (para no reintentarlo jamás sin su ecosistema):

| Candidato | Contenido | vs 2.7.1 |
|---|---|---|
| Bundle referencia completo | base 20.26·ln + delta/rootDelta + 8 ajustadores + statScore/13628 sin clamp | **−9.7 ±13.8** (SPRT 10+0.1) |
| Rebuild conservador | base 2D validada + 6 ajustadores + statScore clamp; nps igual, −23% nodos, WAC 263 | **−25.7 ±20.0** (SPRT 10+0.1) |
| V-a: solo ajustadores | cutNode/ttCapture/moveCount>7/cutoffCnt/singularQuiet/amenazas | **−11.5 ±16.0** (1000p 5+0.05) |
| V-b: solo maquinaria statScore | 4 componentes (fix contHist ply2/4) en RFP + reprieve futility | +17.4 @5+0.05 pero **−10.8 ±14.3 @10+0.1 (SPRT H0)** |
| V-c: V-b + statScore en LMR | el consumidor estrella de la referencia | **−6.9 ±16.3** (1000p 5+0.05) |
| **V-c bis (2026-07-23)** | statScore en LMR, **al ritmo real** y sobre pipeline en 1024avos | **≈ −18 Elo**, 47.4%, LLR −1.85, **H0 vs v2.8.2** |

### Reapertura y cierre de V-c (2026-07-23)

V-a y V-c se habían cortado con partidas a **5+0.05**, ritmo que la lección de oro 1 de este mismo bloque declara incapaz de predecir el signo a 10+0.1 — y V-b lo demostró pasando de +17.4 a −10.8. Se remidió V-c al ritmo real. **Confirma el corte**, ahora con evidencia válida.

**Tres hallazgos del diagnóstico, más valiosos que el experimento:**

1. **La granularidad era un ecosistema no identificado.** La referencia lleva TODO el pipeline LMR en 1024avos de ply (`reductionScale`, `−delta*577/rootDelta`, `+982` de base, `r −= 2179` en jugada TT, `r += r*276/(256*depth+268)` en allNode) y divide sólo al final; cada ajustador suyo es una FRACCIÓN de ply. El nuestro era entero y la tabla ya truncaba al construirse, así que ocho ajustadores de ±1 ply entero dan bandazos que la referencia nunca aplica. **Convertido a punto fijo y verificado neutro** (nodos idénticos en 6 posiciones; `floor(a)+k == floor(a+k)` para k entero). No explica por sí solo el fracaso, pero es prerrequisito real y ya está hecho.

2. **El término de historia en LMR llevaba tiempo muerto.** Era `Clamp(butterfly/16384, ±2)`, calibrado para una tabla que alcanza su rescale de 2²⁰. Medido de verdad: butterfly p99 2840, máx 6086. Esa división daba **0 en más del 99% de las jugadas quiet**.

3. **La causa del fracaso: la tabla butterfly está sesgada por construcción.** Distribución con signo medida: media **+71.8**, mediana **−8**, p10 −156, p90 +75, sólo 25% de entradas positivas, cola hasta 6086. Restar eso en LMR no discrimina — exime de reducción a unas pocas jugadas repetidamente por todo el árbol (+15-20% de nodos medidos, que a TC fijo es justo el Elo perdido). `AddBonus` crecía con rescale global en el raíl positivo mientras `AddMalus` recortaba individualmente en el negativo.

**Lección de oro nueva: fidelidad de fórmula ≠ fidelidad de semántica.** La referencia consume statScore CRUDO en LMR porque el suyo está centrado en cero (tablas acotadas por gravedad y simétricas). Copiar "usa el valor crudo" sobre una tabla sesgada importa un sesgo que la referencia no tiene. Antes de portar un consumidor, medir la DISTRIBUCIÓN de lo que consume, no sólo su magnitud.

**Corolario metodológico:** el conteo de nodos tampoco calibra el divisor. Un barrido dio +19.2 / +1.2 / +24.9 / +23.7 / −1.2% con búsquedas verificadas deterministas — caos genuino frente a un parámetro de poda, no ruido de medida.

**Lecciones de oro nuevas:**
1. **Los benches NO validan cambios de búsqueda**: el rebuild tenía el mejor perfil jamás medido (−23% nodos, WAC 263, nps igual) y perdía 25 Elo jugando. Solo partidas al ritmo REAL validan; los matches hiperrápidos (5+0.05) pueden invertir el signo respecto a 10+0.1.
2. **La suite LMR de referencia presupone su ecosistema** (reduce desde la jugada 2 con capturas, ttPv en TT, qsearch con jaques, sus dinámicas de historia). Cada subconjunto pierde sobre nuestro LMR quiet-only validado. Misma clase de fallo que el bundle NMP de 5B. Cerrado en v2.8.4 con ttCapture/ttPv embarcados; el resto de la suite no transfiere a este motor clásico y no se retoma.
3. **El fix real encontrado** (los contextos ply−2/ply−4 del contHist nunca se escribieron — claves de una sola paridad, detectado con un probe que leía ceros exactos) queda implementado y archivado con sus mediciones; se retomará en 5G cuando el update rule de historia sea el de referencia (bonus/gravity), que es lo que hace fiables esas lecturas.
4. ttPv −2 con proxy PvNode explota el subárbol PV +220% — necesita el flag en TT (hecho en 5D/v2.7.2; el consumidor LMR se probará por juego).

**Base formula mejorada:**
- referencia: `(20.26 + log(threads)/2) * log(i)` almacenado en array 1D `Reductions[i]`
- NoaChess: `0.75 + log(d)*log(m)/2.25` (2D table) — la referencia reduce más agresivamente
- **Delta adjustment**: `-delta*1024/rootDelta` hace que posiciones con ventana ajustada reduzcan menos (adapta LMR al estado de aspiración actual)

**Ajustes adicionales sobre la reducción base:**

| Ajuste | Delta ref | Estado actual (actualizado 2026-07-23, v2.8.4) |
|--------|---------|---------------|
| cutNode | +2 (r += 4026) | ❌ CORTADO a ambas magnitudes al ritmo real (−4.0 H0 a 4026; −7.1 H0 a 1536); el hilado queda (neutro, lo usan los siguientes) |
| PvNode / ttPv | −1 − 11/(3+depth) / −2 | ✅ **EMBARCADO v2.8.4** como ttPv ×0.34 (+7.5 screen) |
| ttCapture en jugada TT | +1 (r += 1079) | ✅ **EMBARCADO v2.8.4** crudo (+7.1 screen) |

**statScore** — ⚠️ **LA FÓRMULA DE ABAJO ESTABA OBSOLETA. Corregida contra la fuente en disco el 2026-07-23.**

Lo que decía esta sección (4 componentes en plies 1/2/4, offset −4433, `r -= statScore / 13628`) **no coincide con `search.cpp`**. Es la misma clase de error que ya costó el port de la R dinámica del NMP: notas propias citando una revisión vieja de la referencia. Lo que dice la fuente real:

```cpp
// search.cpp:1322-1325 — SOLO plies 1 y 2, sin offset, con pesos
ss->statScore = (2252 * mainHistory[us][move.raw()]
               + 1126 * (*contHist[0])[movedPiece][move.to_sq()]
               + 1093 * (*contHist[1])[movedPiece][move.to_sq()]) / 1024;

// search.cpp:1328 — consumo en LMR, con r en 1024avos de ply
r -= ss->statScore * 439 / 4096;
```

Diferencias que importan:
- **Dos contextos de contHist, no cuatro.** Plies 1 y 2. El ply-4 y el ply-6 no existen en el consumo de LMR.
- **Sin offset de recentrado.** El −4433 pertenece a OTRO consumidor; nuestro `StatScoreOffset` existe para el guard de RFP. Meterlo en LMR es un error (casi lo cometo).
- **El divisor real es `439/4096` sobre `r` en 1024avos**, no `/13628`.
- **Ese consumo crudo sólo es seguro si el estadístico está centrado en cero**, que es el caso de la referencia y NO el nuestro. Ver el cierre de V-c arriba.

**REGLA: no portar desde esta tabla. Leer `search.cpp`, `movepick.cpp` e `history.h` en disco antes de tocar nada.**

Otros consumidores de la referencia (verificar igualmente antes de usar):
- NMP guard: `(ss-1)->statScore < 17139`
- Futility: `history/52` añadido al eval

---

### ✅ 5D — Mejoras en TT (v2.7.2) — HECHO (era 5F, renumerado al orden real de ejecución tras el corte de 5C)

**Elo estimado: +5–8 · REAL: +37.9 ±15.0 SPRT agrupado a 1103 partidas [0.554]** (dos runs H1 casi idénticos: +38.3 propio a 546p y +37.6 confirmación a 557p, ambos LOS 100%) — la mayor ganancia de búsqueda desde la v2.3.0. **Gauntlet LTC: +48 ±23 relativo (56.8%, 624p) → ~2975 ±25 CCRL** (el ancla LTC satura entre versiones adyacentes; el SPRT lleva el incremento). **Auditoría de campo: renombrados Dumb-2810 y Marvin-2960 VALIDADOS** (desviaciones −16/−56 tras las −45/−35 sistemáticas); **BitGenie-3010 en vigilancia** (implícito −130 en esta tirada tras un ciclo limpio — volatilidad de una tirada, sin renombrar).

**Por qué se adelantó (2026-07-18):** tras 5B y 5C quedó demostrado que las CONSTANTES heurísticas de referencia no transfieren sin su ecosistema; 5F es INFRAESTRUCTURA pura y transfirió a la primera.

- **Clustering** ✅ — entrada de 16 bytes exactos (key32 + score int32 + eval int32 + move16 + depth8 + genBound8) → **4 entradas por línea de caché de 64B** (la referencia mete 3×10B en 32B con scores int16; nuestros mates ±100k mantienen int32 y el cluster de 4 lo compensa sin reescalar la escala de mate).
- **Aging / Generation** ✅ — generación de 5 bits (ciclo 32) al inicio de cada "go"; reemplazo por `depth − 8×edad_relativa` (fórmula exacta de la referencia); un hit refresca la generación.
- **Static eval en TT** ✅ — hit sirve el eval cacheado sin evaluador; miss guarda entrada eval-only (bound None: nunca corta, nunca desaloja resultados reales). **+24% nps medido.**
- **PV flag en TT** ✅ almacenado y pegajoso — **SIN consumidor en LMR a propósito**: el ttPv −2 se midió en 5C explotando el subárbol PV; con el flag real ya guardado, ese ajustador se probará POR JUEGO en un bloque posterior.
- Regla de sobrescritura de referencia (Exact fresco siempre; bound >4 plies más superficial nunca; best move y marca PV sobreviven).
- Bench: −19% nodos, +24% nps, WAC 265/300 (récord), Fine 70 ✓, KRK ✓, 184 tests (7 nuevos de TT).

---

### ❌ 5E — Double extensions + singular más temprano — MEDIDO Y CORTADO (2026-07-19, campaña v2.7.3)

**Cuatro SPRTs a 10+0.1, todos negativos o en equidad: −19.7 (port completo) / [0.492] (solo trigger) / −12.5 ±15.0 (+ rework de evasiones en qsearch) / [0.476] (+ guard `!is_loss`). Cortado por la regla de decisión del proyecto.**

**Causa raíz:** las extensiones de la referencia solo son estables junto a reducciones de su calibre (r += 4026 cutNode, +1079 ttCapture en 1024avos; nuestra tabla LMR entera topa cerca de 4 y no dispara antes de la jugada 4). El acelerador necesita el freno.

**También medido y rechazado por el camino:** `depth++` en singular (explosión del árbol), márgenes fieles `(28+32)*depth/63`, multi-cut (WAC 265→245), y **TT probe/store en qsearch a depth 0** (las entradas depth-0 inundan los clusters y desalojan las de la búsqueda principal: nodos d15 SUBEN 1.35M→1.75M, nps −11%).

**Cerrado. No se retoma antes del NNUE:** todas estas variantes viven o mueren con el ecosistema de reducciones/eval de la referencia, que el bloque 6 sustituye. Código del candidato 5 archivado.

---

### ✅ 5F — ProbCut rework + capture history — REAUDITADO Y EMBARCADO EN v2.8.2

**Estado final (2026-07-21, v2.8.2):** reimplementado sobre la quiescence ya corregida y con suelo estricto de un ply de búsqueda normal: ningún cutoff descansa solo en quiescence. A/B aislado contra el ejecutable 2.8.1 congelado: **59-51-90, 52.0%, +13.9 ±35.8 Elo, LOS 77.7%**. Se embarca como parte del candidato completo; no se suman los Elo de componentes.

**Contenido embarcado:**
- Entrada de ProbCut desde depth 3 en **cualquier tipo de nodo** (antes: solo non-PV desde depth 5). Guard de la referencia: si el score de la TT ya está por debajo de la barra, ni se intenta.
- **Margen sensible a improving**: `beta + 150 − 40×improving`. La base sigue siendo NUESTRO 150 validado por juego — el 241 de la referencia midió peor aquí en nodos porque su margen presupone su precisión de eval/qsearch (misma lección de escala que 4B/4C/5B). El sustraendo es su 64 reescalado a nuestra base (64 × 150/241 ≈ 40).
- **Profundidad de verificación también sensible a improving**: `depth−5` improving / `depth−3` si no (antes un `depth−4` plano). Un eval más fiable compra margen MÁS BARATO y paga prueba MÁS PROFUNDA; los dos mandos se mueven en direcciones opuestas a propósito.
- **Filtro SEE de la referencia**: el intercambio debe cubrir el hueco entre el eval estático y la barra solo con material.
- **Retorno fail-soft** con el margen descontado + store del fail-high verificado en la TT a `probCutDepth+1` (lower bound). Los valores en rango de mate no se fían de una búsqueda reducida y siguen escaneando capturas.
- **Small ProbCut** (`beta + 428` sobre un lower bound de la TT a ≤4 plies) **restringido a `!inCheck`**, divergiendo de la referencia a propósito: sin restringir costaba **16 puntos de WAC** (255 vs 271) porque nuestra quiescence es solo-capturas y sus lower bounds en jaque no aguantan un corte a ciegas.
- **Tabla nueva `CaptureHistory`** `[piece][to][victimType]` con update por gravedad (`entry += bonus − entry×|bonus|/4096`) — la lección aprendida a golpes en 5G. Alimentada por bonus de cutoff, malus a capturas hermanas y bonus por fail-low a la captura del padre. **Solo la lee la ordenación de ProbCut** de momento; capture futility y la ordenación principal de capturas son bloque posterior (v2.7.5).
- **`cutNode` propagado por toda la búsqueda** exactamente como la referencia. Único consumidor hoy: la verificación de ProbCut (mismo patrón deliberado que el ttPv sin consumidor de 5D).

**Descartado: la forma de IIR de la referencia.** `depth−1` solo para nodos PV/cut desde depth 6 midió **+22% de nodos a igual profundidad** en aislamiento; una variante intermedia (mismo filtro de nodos, desde depth 4) aún costaba +4.8%. Se mantiene nuestra forma validada (depth ≥ 4, todo tipo de nodo): con nuestra ordenación más débil, reducir nodos sin información *en todas partes* es carga estructural. Misma lección de ecosistema que la suite de reducciones de 5C; no se retoma antes del NNUE.

---

### ✅ 5G — Historia y ordenación — CAPTURE HISTORY EN v2.8.1; GRAVEDAD EN v2.8.2, BANDAS CONTINUAS CORTADAS

**La mitad "quiet scoring multinivel" se intentó en cuatro builds a 10+0.1 y se cortó: −33.9 (tabla compartida) / −10.9 H0 a 1180p (tablas separadas) / [0.496] a ~1900p (+ gate depth≥6) / −4.2 H0 a 2000p (+ gravity). Las dos últimas son equidad exacta: la infraestructura correcta no pierde, pero tampoco gana.**

**Defectos REALES encontrados y arreglados por el camino (los fixes quedan probados y archivados):**
1. **Una tabla compartida corrompe los niveles** — el bonus escrito para "la jugada de hace 2 plies" cae en la misma clave que otro nodo lee como "hace 1 ply". Con tablas separadas por distancia, un control que lee solo el nivel 0 reproduce la v2.7.2 **bit a bit**.
2. **El blend no debe llegar al statScore** — los umbrales del RFP (offset 1250, divisor 180, transferencia ×0.28) describen una señal de un nivel.
3. **Blend en todas partes cuesta −9.9% NPS** (5 sondas aleatorias sobre 14 MB por quiet); con gate a depth ≥ 6 gana en nodos Y nps (−11.5/−14.0% tiempo real hasta profundidad).
4. **Gravity en vez de clamp** — la tabla nunca decae dentro de la partida (18M entradas, imposible barrerla como la mariposa); con clamp duro las parejas frecuentes se clavan en los railes ±2^20 y una entrada de nivel 0 clavada mete ±1M en el statScore. `entry += bonus − entry·|bonus|/2^20`, O(1), invisible en bench por diseño.

**Hipótesis histórica del cero:** killers y counter-move ocupan bandas duras fijas (3.0M / 2.9M) por encima de la historia. La prueba definitiva de v2.8.2 refutó que eliminarlas fuese una mejora en este motor: los bonus continuos formaron parte del candidato completo que perdió **-13.1 ±15.2 Elo, H0**, mientras la RC2 que restauró las bandas ganó H1. Las bandas quedan como diseño validado; no se reabren sin un A/B aislado de resolución suficiente.

**Estado final:** capture history, bonus de jaque/amenaza y partial quiet sort embarcaron en v2.8.1. En v2.8.2 continuation history usa gravedad, pero killers/counter conservan las bandas absolutas. El A/B corto de bonus continuos (**50-48-102, +3.5 ±33.8**) no resolvió nada; el SPRT del paquete final sí, y seleccionó las bandas. No se añade el blend multinivel que ya había medido equidad.

---

### ✅ 5H — Aspiration, draw detection, check extension — REAUDITADO Y RECORTADO EN v2.8.2

**Resultado final (2026-07-22):** el A/B corto de ventana inicial adaptativa + recentrado fail-low (**63-47-90, +27.9 ±35.8**) y el de extensión de jaque (**−1.7 ±32.5**) incluían cero. El primer candidato completo que los incorporaba perdió **-13.1 ±15.2 Elo, H0 a 1115 partidas**. La RC2 final restauró la ventana inicial fija y eliminó la extensión, conservando únicamente el recentrado de beta tras fail-low; pasó H1 con **+28.0 ±17.2**. La detección de repetición inminente se conserva.

---

## ✅ BLOQUE 9 — Tablas de finales Syzygy (v2.8.0 — ADELANTADO, antes del NNUE)

**Estado: HECHO en v2.8.0 y corregido en v2.8.1.**

**Por qué antes del NNUE (decisión 2026-07-18, orden exacto de la referencia):** la referencia integró Syzygy (2014) años antes que NNUE (2020), y su pipeline de datos lo explota: las partidas de datagen se **adjudican** al entrar en ≤6 piezas y las posiciones de final se **re-etiquetan** con el WDL exacto de la tablebase — la parte más ruidosa del dataset pasa a tener etiquetas perfectas. Con nuestra debilidad histórica en finales (bug de mate de la 2.7.1), esto mejora el juego YA y la calidad del dataset DESPUÉS. El libro de aperturas de competición (bloque 10) NO se adelanta: la referencia no tiene libro propio — lo que el datagen necesita es un libro SEMILLA de posiciones variadas (estilo UHO, ver 6B), no un libro de ganar.

Syzygy da el resultado perfecto (WDL + DTZ) para posiciones con ≤ 7 piezas.

### ✅ Implementación — HECHA en v2.8.0, corregida en v2.8.1

⚠️ **El punto 1 de este plan NO se cumplió, y fue la decisión correcta.** Se anotan el plan original y lo que realmente embarcó, porque el índice llevaba a pensar que el bloque estaba sin empezar.

1. ~~**P/Invoke de Fathom**~~ → **✅ PORT GESTIONADO EN C# de ~1250 líneas.** No hay compilador de C en esta máquina y una DLL nativa rompería el requisito de ejecutable único autocontenido. Como un índice equivocado devuelve un resultado *erróneo pero verosímil* que la búsqueda se cree a pies juntillas, el port se validó **diferencialmente contra un prober independiente sobre 3000 finales aleatorios de 3 a 5 piezas: cero discrepancias en WDL y cero en DTZ**. Ese arnés cazó tres bugs que habrían llegado a partida en silencio (base del árbol de símbolos cacheada por tabla en vez de por `PairsData`, que colgaba el motor con peones; off-by-one en el remapeo DTZ; y capturas que dejaban reyes desnudos fallando en vez de devolver tablas).
2. **✅ Root ranking** — implementado como **filtro y NO como retorno inmediato**, deliberadamente: devolver el veredicto directamente sustituiría "mate en 3" por una victoria TB genérica en la salida UCI y desharía el anuncio de mate de v2.7.1. Dos bugs críticos corregidos en v2.8.1: el filtro se anulaba porque `SearchRoot` regeneraba las jugadas después de aplicarlo, y el ranking DTZ puntuaba las jugadas irreversibles antes de que ocurrieran y elegía la derrota más rápida en posiciones perdidas.
3. **✅ WDL probe en Negamax** — tras el sondeo TT, condicionado al contador de 50 jugadas, con los veredictos en su propia banda por debajo del rango de mate. Seguridad TT añadida en v2.8.1: `CanReuseTtScore` bloquea la reutilización de scores en banda TB cuando `halfmoveClock > 0`. Guard ordenado por selectividad (cuenta de piezas primero, contra un límite cacheado que vale cero sin tablas): coste 1.1% NPS en posiciones que nunca sondean, frente al 3.5% del orden legible.
4. **✅ UCI options** — `SyzygyPath`, `SyzygyProbeDepth`, `SyzygyProbeLimit`, `Syzygy50MoveRule`, las cuatro declaradas.

**Medido:** un KPvK ganado se convierte en 15 plies frente a 25 sin tablas, mientras KRvK y KQvK convierten igual con o sin ellas — la ganancia está donde la heurística se equivoca, no donde el material ya decide. `SyzygyTable` migró a `MemoryMappedFile` con offsets `long` en v2.8.1, eliminando el techo de 2 GB de `byte[]` para ficheros de 6/7 piezas.

**Deuda conocida:** hay un `NullReferenceException` intermitente en `SyzygyTable.U8` durante `ProbeDtz`, reproducible ~1 de cada 4 ejecuciones de la suite. Diagnóstico: los tests corren en paralelo y reinicializan el estado estático de `Syzygy` mientras otro sondea. En partida real `Syzygy.Init` se llama una sola vez desde `UciLoop`, así que **no es un riesgo de partida**, pero conviene una guarda por si una GUI manda `setoption SyzygyPath` con búsqueda en curso.

### ✅ Tablas — juego de 3-4-5 piezas INSTALADO

En `F:\Works\_______________CHESSTEST\syzygy`: **290 ficheros, 940 MB** (145 `.rtbw` + 145 `.rtbz`), máximo 5 piezas. Verificado el 2026-07-23.

| Piezas | Tamaño | Estado |
|--------|--------|--------|
| 3-4-5 | ~1 GB | **✅ INSTALADO** — suficiente para la mayoría de finales |
| 6 | ~150 GB | 🔲 el lector ya lo soporta (`MemoryMappedFile` + offsets `long`) |
| 7 | ~18 TB | 🔲 solo con hardware dedicado |

**Ojo al configurar:** `max_pieces` del bot y `SyzygyProbeLimit` no deben superar el juego instalado — con 5 piezas en disco, cada sondeo por encima de eso falla sin más.

---

## ✅ BLOQUE 6 — NNUE — Producción (v3.0.0)

**Estado: HECHO (v3.0.0, 2026-07-25) · La red neuronal supera al evaluador clásico: gen3 +4.5 ±11.4 Elo, positivo agotado a 2650 partidas, LOS 77.8% · gauntlet LTC de calibración pendiente**

Esquema final **HalfKAv2_hm (feature_schema_id 2)**, no el HalfKP que asumía el plan original: reyes como features, InputSize 22528 por perspectiva (32 buckets × 704), topología FT 22528→128 ×2 → L1 32 → 1, cuantización QA=255/QB=64/OutputScale=400. Inferencia SIMD AVX2 (VPMADDWD, activación clipped precomputada, MoveFeature fusionado) a ~66% de la velocidad del clásico. Acumulador incremental con full-refresh en movimiento de rey + parche de la perspectiva rival, verificado por paridad incremental==recálculo. Datagen (`NoaChess.DataGen`) con mezcla `lambda·sigmoid(score/SCALE) + (1−lambda)·wdl` y adjudicación Syzygy/resign/tablas.

**Bug crítico del datagen corregido:** `FindBestMove` devolvía Score 0 en el hard-stop por nodos durante la primera jugada de raíz, poniendo a cero el 57% de las etiquetas (57.6%→2.1%; invisible al juego, sólo el `.Score` que consume el datagen). Sin esa corrección la red aprendía "media tabla es igualdad muerta".

**Self-play generacional** (cierra el distribution shift del net de imitación de 1ª generación): cada red promocionada entrena el datagen de la siguiente. gen2 +1.9 Elo, gen3 +4.5 Elo vs clásico. Pipeline automatizado de punta a punta. Red `noa-gen3` embebida en el exe. **Siguiente: iterar generaciones (gen4+) y anclar el absoluto CCRL con gauntlet.**

La infraestructura de inferencia estaba ya completa: `NnueNetwork.cs`, `NnueInference.cs`, `NnueAccumulator.cs`, `NnueAccumulatorStack.cs`, `NnueFeatureIndex.cs`, `NnueEvaluator.cs`, `NnueModelLoader.cs`, `NnueModelHeader.cs`, `IIncrementalEvaluator.cs`.

---

### ✅ 6A — Feature encoding + incremental update correcto — HECHO (v3.0.0)

**Realizado como HalfKAv2_hm (schema 2), no HalfKP como decía el plan original.** Reyes como features, InputSize 22528. Incremental update con full-refresh en movimiento de rey + parche de la perspectiva rival, y `PushMove`/`Pop`/`PushNull` verificados por el test de paridad incremental==recálculo.

Antes de generar datos, verificar que el código de inferencia es completo:

- **`NnueFeatureIndex.cs`** — implementar **HalfKP** primero (más simple que HalfKAv2-hm de referencia):
  - Índice = `king_square × 640 + piece_type_color × 64 + piece_square`
  - 41,024 inputs posibles (64 posiciones del rey × 640 features de pieza)
- **Incremental update en `NnueAccumulatorStack`** — el caso crítico es el movimiento de rey: cuando el rey se mueve, el "king bucket" cambia y **TODOS** los features cambian → **full refresh obligatorio**. `PushMove` debe detectar si el movedor es un rey y llamar `RefreshAccumulator()`.
- Verificar `PushMove` (add/remove features), `Pop` (restaurar acumulador), `PushNull` (sin cambio de features).
- **Blending en transición** (opcional): `NNUE * 0.5 + Classical * 0.5` puede compensar una red débil mientras mejora. el motor de referencia usa NNUE puro con fallback clásico solo cuando `count(pieces) > 7 AND abs(psq) > 1760`.

---

### ✅ 6B — Generación de datos desde self-play — HECHO (v3.0.0, con desviaciones)

**Realizado con la herramienta independiente `NoaChess.DataGen`** (no el modo UCI `go datagen` que proponía el plan). Self-play con búsqueda limitada por nodos (`--nodes`), etiquetas `lambda·sigmoid(score/SCALE) + (1−lambda)·wdl(result)`, formato binario `.noadata` con cabecera mágica. Adjudicación por **resign** (`--resign`) y **tablas** (`--drawscore`/`--drawcount` tras `--drawply`) + `--maxplies`. **NO implementado del plan:** el re-etiquetado Syzygy WDL de posiciones ≤6 piezas (las tablebases existen desde v2.8.0 pero el datagen no las consulta todavía) y el libro semilla UHO (arranca desde aperturas aleatorias). Pendientes como mejora futura del datagen.

- **Target: 50–100M posiciones** de self-play a depth 7–9.
- **Formato de salida:** binario — (posición en bitboards, side to move, eval estático en centipawns).
- Las etiquetas son la evaluación clásica de NoaChess **después de todos los bloques 4+5 implementados** → etiquetas de máxima calidad.
- **Libro SEMILLA de aperturas (orden de la referencia, verificado 2026-07-18):** las partidas de datagen arrancan desde un libro de POSICIONES variadas, no desde startpos — diversidad de distribución, no fuerza. la referencia usa `noob_3moves.epd` de su repo oficial de books en `generate_training_data` (fuente: wiki de nnue-pytorch, Training datasets). Candidatos para NoaChess: noob_3moves.epd tal cual, o UHO. Esto NO es el bloque 10.
- **Re-etiquetado Syzygy (orden de la referencia, requiere bloque 9 = v2.8.0 hecho):** partidas adjudicadas al entrar en ≤6 piezas; toda posición del dataset con ≤6 piezas lleva la etiqueta WDL exacta de la tablebase en lugar del eval — la parte más ruidosa del dataset pasa a etiquetas perfectas. (Verificado 2026-07-18: el pipeline oficial nnue-pytorch pasa SyzygyPath al datagen y su rescorer re-etiqueta con TB; los datasets re-etiquetados "producen redes mejores en general".)
- **Plan B de datos (nota 2026-07-18):** las mejores redes modernas de la referencia se entrenan con datos DERIVADOS DE LEELA (lc0) re-etiquetados con Syzygy, no con self-play propio — si nuestro dataset de self-play se estanca en 6C/7, convertir data pública de Lc0 (herramienta lc0-data-converter) es una alternativa probada.
- Filtrar: posiciones en jaque, posiciones con eval > 2000 cp (demasiado desequilibradas).
- Implementar modo UCI `go datagen` integrado en el engine — lo más limpio para lanzar desde bat.
- Distribución esperada: ~70% posiciones de partidas empatadas/equilibradas (0–200 cp), ~30% posiciones con ventaja clara.

---

### ✅ 6C — Entrenamiento — HECHO (v3.0.0)

**Arquitectura final HalfKAv2_hm 22528→128→32→1** (no HalfKP-256). Pipeline PyTorch propio: `train_nnue.py` (cosine LR + weight decay, CUDA), `validate_nnue.py` (corr/slope/RMS/sign), `export_model.py` (float → `.noannue` cuantizado). Ancho de red parametrizado (`--ft-out`/`--l1-out`); el loader C# lee las dimensiones de la cabecera, así que barrer arquitecturas es sólo-Python. Las primeras generaciones perdían al clásico (net de imitación 1ª gen); el self-play generacional cerró el hueco.

- **Arquitectura objetivo:** HalfKP → 256 neuronas (hidden) → 32 → 1.
- **Herramienta:** `nnue-pytorch` (community trainer, formato compatible con el formato de referencia) o trainer propio en PyTorch.
- **Quantización:** exportar pesos en int16/int32 en el formato que espera `NnueModelHeader.cs`.
- **Iteraciones:**
  - run1 / run2: debug, verificación de formato, carga correcta en el engine.
  - run3 / run4: primeros modelos con datos reales. Esperar que el clásico los supere aún — las etiquetas son buenas pero la red es pequeña.
  - run5+: la red supera al clásico → punto de inflexión. A partir de aquí iterar.
- Benchmarks NPS: la inferencia NNUE cuesta nodos. Medir con `bench` antes y después de activar.

---

### ✅ 6D — Activación NNUE en producción — HECHO (v3.0.0, núcleo)

**SPRT `NnueEvaluator` vs `ClassicalEvaluator` superado: gen3 +4.5 ±11.4 Elo, positivo agotado a 2650p, LOS 77.8%.** Seleccionable con la opción UCI `UseNNUE`; red `noa-gen3` embebida en el exe. **NO implementado del plan (refinamientos pendientes):** el dampening por rule-50 del eval NNUE (`v = v·(195−rule50)/211`) y la `nnueComplexity` para time management. El `rule50` que ya existe en la búsqueda es el ranking DTZ de Syzygy, no esto.

- SPRT `NnueEvaluator` vs `ClassicalEvaluator` a TC 10+0.1.
- Si H1 pasa → v3.0.0. Si no → run siguiente.
- Complejidad NNUE: `nnueComplexity = (416*nnueComplexity + 424*|psq-nnue|) / 1024` (mide divergencia entre PSQ y NNUE — útil para time management).
- Dampening por rule-50: `v = v * (195 - rule50) / 211` — reduce el eval en posiciones con contador de 50 movimientos alto.

---

### ✅ 6E — Lazy SMP multihilo — HECHO (v3.1.0, 2026-07-28)

**Implementado.** Búsqueda paralela Lazy SMP: N workers buscan la misma raíz compartiendo UNA sola TT; el resto del estado (pila de búsqueda, historias, tablero, evaluador) es por hilo. El worker principal gestiona el tiempo y reporta; al final los workers votan la jugada. **`Threads=1` es byte-idéntico a v3.0.0** (verificado: 1.307.077 nodos exactos en una batería de 6 posiciones a profundidad fija). Escalado de nodos ~7,6× a 8 hilos. **Elo de juego real esperado +30–60 a TC largo con muchos núcleos; gauntlet LTC de calibración pendiente.**

**Elo estimado: +30–60 Elo en juego real con 16 núcleos (Threadripper 2950X)**

- **Lazy SMP:** múltiples hilos buscan la misma raíz compartiendo la TT; divergen por las carreras en la TT y se cruzan las mejores líneas. Hecho.
- `Board.Clone()` da a cada hilo su propio tablero (copia profunda con historial); make/unmake nunca compite. Hecho.
- Evaluador clonable por hilo: NNUE comparte la red read-only con acumuladores propios; clásico es instancia nueva (scratch por llamada). Hecho.
- TT compartida lock-free por carreras benignas (verificación de clave de 32 bits + veto de pseudo-legalidad descartan lecturas rotas). Hecho.
- UCI option `Threads` default 1, max 16. Hecho.
- **Pendiente:** calibrar con gauntlet a largo TC (60+0.6) donde el SMP aporta más que en bullet.

---

## ✅ BLOQUE 7 — NNUE self-play iterativo (v3.1.0)

**Estado: PENDIENTE · Después del bloque 6**

- **Iterar la red por self-play (RL):** el motor juega contra sí mismo con la red anterior como evaluador → genera posiciones → reentrena → nueva red. Cada iteración la red se aleja del profesor clásico y despega.
- En run5+ la red supera al clásico y actúa como profesor de sí misma.
- Históricamente en engines open-source esto multiplica el Elo x2–3 respecto a la primera red entrenada contra el clásico.
- El ciclo: `engine_vN → datagen(50M pos) → train → engine_v(N+1) → SPRT vs vN`.

---

## BLOQUE 8 — NNUE con posiciones de partidas humanas de alto nivel (v3.2.0)

**Estado: PENDIENTE · Después del bloque 7**

Este bloque añade **diversidad de posiciones** al entrenamiento sin cambiar la fuente de etiquetas.

### Por qué las partidas humanas

La debilidad del self-play puro es que el engine tiende a explorar siempre los mismos tipos de posiciones: tabias, variantes conocidas, posiciones que su propia búsqueda favorece. Las posiciones humanas de alto nivel cubren el rango 0–200 cp (donde vive el ajedrez real) con mucha mayor variedad estructural: sacrificios posicionales, desequilibrios complejos, finales técnicos que el self-play rara vez reproduce.

### Fuentes de datos

- **Lichess database** — partidas públicas en formato PGN, filtradas por ELO ≥ 2400 (ambos bandos). Disponible en `database.lichess.org`. ~50M partidas disponibles.
- **FIDE / chess.com bases de datos** — partidas de GM/IM de alto nivel.
- **Partidas del propio bot en Lichess (NoaBot, publicado 2026-07-16)** — el motor juega 24/7 contra bots heterogéneos vía lichess-bot (`F:\Works\Programacion\_BOT_EJECUTANDO_NO_BORRAR_lichess-bot-master`). Cada partida se archiva localmente (`pgn_directory` en config.yml) y también se puede descargar en bloque de la API (`lichess.org/api/games/user/NoaBot`). Aporta diversidad de posiciones contra estilos que el self-play no visita; la calidad de las partidas NO importa (las etiquetas las pone siempre la búsqueda propia re-evaluando cada posición). Volumen modesto (~100–300 partidas/día ≈ 5–15K posiciones útiles/día): fuente COMPLEMENTARIA de diversidad, no sustituye al datagen masivo de self-play del Bloque 6B.
- Extraer posiciones hasta el movimiento 40 (antes de los finales triviales) — filtrar jaque, eval > 2000 cp.

### Implementación

- Las **etiquetas siguen siendo la búsqueda propia de NoaChess** evaluando cada posición a depth 7–9 — las partidas humanas solo aportan *diversidad de posiciones*, no conocimiento externo.
- ⚠️ Jugar contra un motor fuerte NO funciona: las partidas son desequilibradas (posiciones con ventaja enorme) y las etiquetas siguen siendo malas porque la búsqueda etiqueta en un tablero ya decidido.
- **Mezcla de datos:** ~70% self-play + ~30% posiciones humanas (ratio a calibrar con SPRT).
- El entrenamiento con mezcla debería converger más rápido y producir una red más robusta en posiciones poco frecuentes en el self-play.

---

## BLOQUE 10 — Libro de aperturas de competición (v4.0.0 — SE MANTIENE AL FINAL)

**Estado: PENDIENTE · Nota de orden (2026-07-18): la referencia no tiene libro propio** — en CCRL/TCEC se juega con libros neutrales del torneo, así que el libro propio no puntúa en nuestra medición principal. Además un libro de competición se tunea contra el perfil de fuerza del motor FINAL (con NNUE); construirlo antes obligaría a re-tunearlo. Lo que sí se necesita antes del NNUE es el libro SEMILLA de posiciones para el datagen (estilo UHO) — eso vive en 6B, no aquí.

### Filosofía

El libro de NoaChess **no busca variedad — busca ganar**. Si una variante puntúa mejor, siempre se juega la mejor. La diversión o la variedad son secundarias al resultado.

### Opción A — Libro Polyglot existente (implementar primero)

- Formato `.bin` Polyglot estándar, compatible con UCI.
- Cargar en memoria al inicio; sondear por hash de posición Zobrist.
- Selección de jugada: **siempre la de mayor peso** (no aleatorio). Solo aleatoriedad entre jugadas con peso idéntico.
- Fuentes: `Performance.bin`, `komodo.bin`, `gm2600.bin` (partidas GM).

### Opción B — Libro propio desde bases de datos (mayor ventaja competitiva)

- Descargar base de datos Lichess de partidas ELO 2400+ (formato PGN, ~50M partidas).
- Extraer posiciones hasta movimiento 20 con resultado.
- Calcular para cada posición y jugada: `peso = frecuencia × (win_rate - 0.5 × draw_rate)`.
- Exportar en formato Polyglot con estos pesos.
- Resultado: libro extremadamente profundo en aperturas populares de alto nivel.

**Recomendación:** Opción A primero (1 día de implementación). Opción B si el engine llega a torneos serios.

---

## BLOQUE 11 — Extras de fuerza (v4.5.0+)

**Estado: RESERVADO · Definir cuando lleguemos aquí**

- Mejoras adicionales de búsqueda que la referencia incorpore entre ahora y entonces.
- Posible migración a HalfKAv2-hm (arquitectura NNUE más rica que HalfKP-256).
- Refinamiento adicional de time management (factores adaptativos completos: fallingEval, timeReduction, complexPosition) si la versión v2.6.4 no los incluye todos.
- Optimizaciones de velocidad adicionales si el NPS se queda atrás.

---

## Decisiones técnicas permanentes

### Lo que NO hay que volver a intentar

- **King safety con safe checks sin máscara estricta** — intentado en v2.4.6, −77 Elo. La máscara de cobertura debe incluir TODOS los defensores. Ver memoria `king-safety-fase-b-cut.md`.
- **Tunear movilidad con texel tuner** — señal EG espuria, converge a valores negativos. Usar valores de referencia directamente.
- **Múltiples términos en un solo SPRT** — si falla, no sabes cuál es el culpable. Un término = un SPRT, siempre.
- **Jugar contra un motor fuerte para generar datos NNUE** — partidas desequilibradas + etiquetas malas.

### Reglas de decisión SPRT

- SPRT H0 → descartar término, documentar en memoria, no reintentar antes del siguiente bloque mayor.
- SPRT H1 → versión bump, commit, gauntlet de precisión opcional para afinar el Elo medido.
- Gauntlet campo actual: 7 rivales 2580–2788 CCRL, tc=60+0.6, rounds=28.
- **Upgrade de campo:** cuando NoaChess supere el 70% de puntuación → subir rivales a 2750–2950 CCRL.

### CPU (Threadripper 2950X, Zen+, familia 0x17)

- PEXT microcodificado → lento. CPUID guard activo en `ComputeUsePext()` desde v2.5.0.
- AVX2 soportado y rápido → usar para NNUE inference (SIMD vectorizado).
- 16 núcleos / 32 hilos → Lazy SMP con `Threads=16` dará el mayor salto en juego real.
- No AVX-512.

---

## 🔁 REVISIÓN FINAL — términos cortados a rescatar **DESPUÉS DEL NNUE (bloque 6)**

**Orden decidido el 2026-07-23.** Antes esta sección decía "al final del bloque de eval clásica (antes de NNUE) o tras el primer retune texel global". **Van después del bloque 6.** El motivo:

- **La búsqueda sobrevive al NNUE; la evaluación no.** Estos dos son términos DENTRO del evaluador clásico, y el plan de integración (§6, blending) deja el clásico como fallback marginal — la referencia sólo lo consulta con más de 7 piezas Y `|psq| > 1760`. Lo que se afine aquí deja de consultarse.
- **Su diagnóstico documentado es "conflicto con el tuning existente", no "mal portado".** El remedio escrito es un **retune texel global**, que es el trabajo más caro de la lista y cuyo producto son parámetros de la eval clásica.
- **Su historial es el peor del proyecto**: Fase B falló tres veces (−77, 0, −13) y KingProtector envenenó el juego a TC largo. Menor valor esperado por hora de máquina.

**Contraargumento reconocido:** el datagen usa el motor actual para etiquetar, así que una eval mejor daría mejores etiquetas. Lo debilita que las tablebases ya re-etiquetan la parte más ruidosa del dataset y que las etiquetas salen del score de BÚSQUEDA, no de la eval estática.

**Si se quiere invertir en eval antes del NNUE, lo que sí compone es el retune texel global por sí mismo** — mejora todos los términos a la vez y además es el prerrequisito de estos dos.

| Término | Cortado en | Evidencia | Ruta de rescate |
|---------|-----------|-----------|-----------------|
| **King Safety Fase B** (shelter/storm/safe checks completos) | v2.4.6 | −77 → 0 → −13 en tres intentos | POST-NNUE. Dejar que la red aprenda seguridad del rey sola; sólo si el clásico sigue vivo, re-evaluar tras retune global |
| **KingProtector** (4E) | v2.6.5 | veneno a LTC sobre PSTs PeSTO | POST-NNUE. Sólo con PSTs re-tuneadas conjuntamente (tirada texel completa) |

---

## Historial de versiones

| Versión | Descripción | Elo SPRT | Elo CCRL est. |
|---------|-------------|----------|---------------|
| 2.3.0 | Search (cont-hist, singular, LMR, IIR, ProbCut) | +91 ±34 | ~2640 |
| 2.4.0 | Eval base + texel tuning | +13 Elo | ~2680 |
| 2.4.5 | Tempo + phalanx + backward | +12 Elo | ~2710 |
| 2.5.0 | Staged movegen + lazy legality + PEXT | +101 Elo | ~2833 |
| 2.6.0 | attackedBy infra (prereq) | — | — |
| 2.6.1 | Threats | +103 ±35 | ~2775 |
| 2.6.2 | Non-linear mobility | +6.6 ±11.5 (LOS 87%) | **2780 ±20 medido** |
| 2.6.3 | Shelter/Storm + King Safety | +76.9 ±31.2 (LOS 100%) | **2800 ±25 medido** |
| 2.6.4 | Time Management adaptativo | sin SPRT (ver nota) | **2875 ±20 medido** |
| 2.6.5 | Piece terms (TrappedRook, bishop, WeakQueen, etc.) + timeman ref | +19.5 ±13.6 | **2835 ±25 medido** (field re-anclado) |
| 2.6.6 | Passed pawns de referencia (definición + filtro + proximidad + camino) | +45.8 ±23.1 | **2880 ±25 medido** |
| 2.6.7 | Pawn structure chain (Connected completa, WeakUnopposed, WeakLever, DoubledEarly, BlockedPawn) | +28.4 ±17.5 | **2895 ±25 estimado** |
| 2.6.7.1 | Parche timeman (freno apertura) + fix UCI ponder/infinite (freeze Arena) | +14.3 ±13.5 | **~2920 ±20 medido (ritmo CCRL exacto)** |
| 2.6.8 | Polinomio material imbalance (Romstad) + retune conjunto valores de pieza + guardarraíl bullet | +78.4 ±31.5 (LOS 100%) | **~2944 ±15 medido** |
| 2.6.9 | Winnable / scale factors (complexity, almostUnwinnable, OCB, finales de torre, sin dama, factor material sin peones) | +34.3 ±19.5 (LOS 100%) | **~2941 ±25 medido** |
| 2.7.0 | Improving flag (LMR/RFP/LMP; NMP → 5B) | +4.0 ±27.1 STC (parado 380g) · **+43 ±23 rel LTC** | **~2965 ±25 medido** |
| 2.7.1 | Verificación NMP ≥14 + fail-soft + statScore en RFP (bundle completo de ref. tumbado por SPRT y disecado: R→post-qsearch-checks, entrada eval→NNUE, futility→5C, captFut→5G) + fixes de mate (ID no corta en mate, UCI score mate) | +2.9 ±7.4 agrupado · +44 ±23 rel LTC | **~2970 ±25 medido** |
| (5C) | ❌ CORTADO — suite LMR + statScore 4-comp: todo negativo a 10+0.1 (−9.7/−25.7/−11.5/−10.8); fix contHist ply2/4 archivado → 5G | — | — |
| 2.7.2 | 5D TT redesign (era 5F): clustering 4×16B, aging, eval cacheado, ttPv | +37.9 ±15.0 agrupado · +48 ±23 rel LTC | **~2975 ±25 medido** |
| (2.7.3) | ❌ CORTADO SIN RELEASE — campaña 5E singular (4 SPRTs negativos) + 5G historia multinivel (4 builds, las 2 últimas equidad exacta; infra tablas-por-distancia/gravity/gate construida, bloqueo = bandas duras killers/counter); ambos cerrados | — | — |
| 2.7.4 | **Rework de quiescence (CORRECCIÓN)**: en jaque sin stand-pat, todas las jugadas, cero poda, mate detectado; guard de ahogado, fail-soft, 4 promociones, bloque de poda de referencia (futility 147, SEE −36, capture history); **arregla el cuelgue en raíz** con mate/ahogado presente desde siempre | −2.1 ±9.9 SPRT (H0) · +52 ±23 rel LTC vs +48 | **~2975 ±25 SIN CAMBIO** |
| 2.8.2/5F | ✅ ProbCut reimplementado con verificación normal depth>=1; A/B aislado 59-51-90 | +13.9 ±35.8, LOS 77.7% | Incluido en el match completo |
| 2.8.0 | **Bloque 9: Syzygy — HECHO** (port nativo en C#, NO Fathom/P-Invoke: sin compilador de C, y una DLL rompería el exe único). WDL en búsqueda + filtrado DTZ en raíz + 4 opciones UCI | SPRT pendiente | 3000 finales verificados sin discrepancias |
| 2.8.1 | Correcciones críticas Syzygy + capture history/partial quiet sort/amenazas | +14.1 ±10.8 SPRT · +75 ±23 rel LTC | **~3000 ±25 CCRL** |
| 2.8.2 | Auditoría clásica final: pawn correction, ProbCut verificado, aspiración inicial fija + recentrado fail-low, gravedad con bandas killer/counter, sin extensión de jaque, log UCI endurecido | **+28.0 ±17.2, H1 a 834p** | **~3013 ±30 CCRL (624p LTC, +94 ±23 relativo)** |
| 3.0.0 | **Bloque 6: NNUE — HECHO.** HalfKAv2_hm (schema 2, reyes como features), inferencia SIMD AVX2, acumulador incremental, datagen con adjudicación Syzygy, bug de etiquetas 57%→2% corregido, self-play generacional (gen2 +1.9, gen3 +4.5 Elo vs clásico) | **gen3 +4.5 ±11.4 vs clásico, positivo agotado 2650p, LOS 77.8%** | gauntlet LTC pendiente |
| 2.8.0 | **Syzygy tablebases (ADELANTADO — orden de la referencia: TB antes que NNUE)** — eval perfecta en juego + adjudicación y re-etiquetado del datagen | TBD | +juego finales |
| 3.0.0 | NNUE producción (HalfKP-256; datagen con libro semilla de posiciones + re-etiquetado Syzygy) | TBD | ~3150+ |
| **3.1.0** | **Lazy SMP (16 hilos) — ✅ HECHO** — `Threads=1` byte-idéntico, escalado de nodos ~7,6× a 8 hilos | gauntlet LTC pendiente | ~3150+ esperado |
| 3.2.0 | NNUE con posiciones humanas Lichess/FIDE (bloque 6E de datos) | TBD | — |
| 3.3.0 | NNUE self-play RL | TBD | — |
| 4.0.0 | Libro de aperturas de competición (opcional — la referencia no tiene libro propio; los torneos usan libros neutrales) | — | torneo |
| 4.5.0+ | Extras de fuerza | — | — |
