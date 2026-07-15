# NoaChess

[Spanish below]

## UCI Chess Engine in C# (.NET 10)

NoaChess is a modular UCI chess engine written from scratch in C# on .NET 10, with its own WPF board GUI. It follows an incremental, measured development process: every version is validated with unit tests, Perft, automated engine matches and Elo estimation before moving on.

**Current version: v2.6.8 — ~2920 ± 20 CCRL** (time-management patch: bullet sustainability guard — per-move budget bounded by `inc + clock/16` target and `inc + clock/4` hard deadline in sudden-death, stabilizing clock spend around the increment; non-regression confirmed vs v2.6.7.1 at 420 games [0.509], +6.9 ± 23.7 Elo, LOS 71.7%). See [CHANGELOG](CHANGELOG.md).

### **Main Features**
- Bitboard board representation with **magic bitboards** for sliding pieces (**PEXT/BMI2** lookup on CPUs where it is fast — Intel and AMD Zen3+ — with an automatic CPUID guard).
- Full legal move generation (castling, en passant, promotions), validated against the public Perft reference values.
- **Search**: iterative deepening, aspiration windows with progressive widening, PVS, transposition table (Zobrist), **staged move generation with lazy legality** (TT move served without generating, then captures, then quiets), quiescence search, null-move pruning, check extensions, singular extensions, SEE (static exchange evaluation) ordering and pruning, killer/history/counter-move/continuation-history ordering, history-informed logarithmic LMR, reverse futility pruning, futility pruning, late move pruning, Internal Iterative Reductions, ProbCut.
- **Evaluation**: tapered (middlegame/endgame) evaluation blended by game phase — PeSTO piece values, two-phase piece-square tables, reference-style attack bitboards (attackedBy per piece type + double attacks) feeding a full threat evaluation (hanging pieces, threats by minor/rook/king, safe-pawn and pawn-push threats, restricted pieces, knight/slider threats on the queen), king safety (attack units + pawn shield through quadratic danger curve), non-linear piece mobility (reference lookup tables over a reference-style mobility area, with x-ray attacks and pinned-piece restriction), tempo bonus, knight outposts, space, bishop pair, rooks on open/semi-open files and on the 7th rank, tapered pawn structure (doubled/isolated/passed/backward, phalanx, connected passers, blocked passers, rook behind passer); mop-up term for won endgames.
- **NNUE infrastructure**: HalfKP neural network runtime, incremental accumulators, SIMD inference; optional via UCI `UseNNUE` / `EvalFile` options.
- **Time management**: soft/hard budgets, 85% increment use, conservative ply-adaptive horizon, movestogo support, GUI-latency safety margins.
- **Full UCI**: uci, isready, ucinewgame, setoption (Hash, Threads, MoveOverhead, Profile, UseNNUE, EvalFile), position, go (depth/nodes/movetime/clock/ponder/infinite), stop, quit. Asynchronous search; publishes as a single self-contained .exe.
- Engine profiles (Default/Bullet) with tunable search/time parameters.
- **WPF GUI** (MVVM): play against the engine with vector piece graphics (Cburnett SVG), live evaluation bar and search depth, background analysis on the user's turn.
- Allocation-free hot paths; BenchmarkDotNet benchmark suite.

### **Solution Layout**
```
src/NoaChess.Core      -> pure chess rules: board, bitboards, moves, FEN, Zobrist
src/NoaChess.Engine    -> search, evaluation, heuristics, TT, time management
src/NoaChess.UCI       -> UCI console host (the engine executable for GUIs)
src/NoaChess.GUI.Wpf   -> WPF board application (MVVM)
tests/                 -> xUnit test suites (Perft, make/unmake, search, SEE...)
benchmarks/            -> BenchmarkDotNet micro-benchmarks
```
Dependency rule: `Core` depends on nothing; `Engine` uses `Core`; `UCI` and the GUI are thin hosts. The full plan lives in [ROADMAP.md](ROADMAP.md).

### **Build & Run**
```bash
dotnet test                                   # run the full test suite
dotnet run --project src/NoaChess.GUI.Wpf    # play against the engine (Windows)
dotnet publish src/NoaChess.UCI -c Release -o out   # single-file UCI engine .exe
```
The published `NoaChess.UCI.exe` works in any UCI GUI (Arena, CuteChess, BanksiaGUI...).

### **Technologies**
- **Language**: C# 14, .NET 10 LTS
- **Testing**: xUnit; Perft validation; automated gauntlets with cutechess-cli
- **Performance**: BenchmarkDotNet
- **GUI**: WPF (MVVM), SharpVectors, MdXaml

### **Roadmap & Versions**
- **v0.1** — Playable MVP: core rules, alpha-beta, WPF board. ✅
- **v0.2** — Measurable engine: TT, quiescence, move ordering, LMR. ✅
- **v1.0** — Competitive search & stable UCI: PVS, null move, SEE, pawn hash, time management. ✅
- **v1.1** — NPS optimization (magic bitboards, zero-alloc paths), Bullet profile, benchmarks. ✅ (~2070 Elo)
- **v2.0** — NNUE evaluation infrastructure. ✅
- **v2.2** — Tapered classical evaluation overhaul + search pruning (log LMR, RFP, futility, LMP). ✅ (~2600 Elo)
- **v2.3** — Search core overhaul: continuation/counter-move history, singular extensions, history-based LMR, IIR, ProbCut. ✅ (~2710 Elo measured)
- **v2.4** — Evaluation terms (knight outposts, advanced passers, rook on 7th, space) + full texel tuning (positional terms + PSTs) on fresh self-play data. ✅ (+13 ± 13 Elo SPRT vs v2.3.0)
- **v2.4.5** — Evaluation Fase A: tempo bonus, phalanx/connected pawns, backward pawns + full retune on fresh data. ✅ (+12 ± 15 Elo SPRT vs v2.4.0)
- **v2.5.0** — Speed block: staged move generation, lazy legality, PEXT with CPU guard. ✅ (+101 ± 37 Elo SPRT vs v2.4.5; ~2768 CCRL in a 392-game LTC gauntlet)
- **v2.6.0** — attackedBy infrastructure (reference-style attack bitboards, enabler for threats / mobility / king safety). ✅ (evaluation-neutral)
- **v2.6.1** — Full reference-engine threat evaluation (10 terms, rescaled to NoaChess units). ✅ (+103 ± 35 Elo SPRT vs v2.5.0 — largest single evaluation gain of the project)
- **v2.6.2** — Non-linear mobility (reference lookup tables, re-centered), x-ray attacks, reference mobility area, pinned-piece attack restriction. ✅ (+6.6 ± 11.5 Elo SPRT vs v2.6.1, LOS 87%; **2780 ± 20 CCRL** measured across two independent LTC gauntlets, 2700+ games)
- **v2.6.3** — Full reference-engine king safety: shelter/storm tables, KingOnFile, pre-castling shelter max, EG king-pawn proximity, reference king-danger formula (no safe checks), king flank terms, Rook/BishopOnKingRing. ✅ (+76.9 ± 31.2 Elo SPRT vs v2.6.2, LOS 100%; **2800 ± 25 CCRL** measured)
- **v2.6.4** — Time management: 85% increment use + conservative ply-adaptive horizon (a best-move instability extension was tried and reverted — regressed at fast TC and overspent in bullet). ✅ **2875 ± 20 CCRL measured** (LTC gauntlet 2728 games, field 2580–2917)
- **v2.6.5** — Reference piece terms (TrappedRook, RookOnClosedFile, BishopPawns, BishopXRayPawns, LongDiagonalBishop, MinorBehindPawn, reference-exact outposts with pawn-attacks-span, UncontestedOutpost, WeakQueen; KingProtector disabled) + full reference time manager (optimum/maximum + fallingEval/stability/instability stop factors). ✅ (+19.5 ± 13.6 Elo SPRT vs v2.6.4, LOS 99.7%; **2835 ± 25 CCRL** measured, 880 LTC games)
- **v2.6.6** — Reference passed pawns (reference passed definition + candidate passers, blocked-passer filter, king proximity to the block square, path-to-queen safety ladder, PassedFile). ✅ (+45.8 ± 23.1 Elo SPRT vs v2.6.5, LOS 100%; **2880 ± 25 CCRL** estimated)
- **v2.6.7** — Reference pawn-structure scoring chain (full Connected formula, WeakUnopposed, WeakLever, DoubledEarly, blocked pawns on ranks 5-6, reference Doubled/Isolated/Backward semantics). ✅ (+28.4 ± 17.5 Elo SPRT vs v2.6.6, LOS 99.9%; **2895 ± 25 CCRL** estimated)
- **v2.6.7.1** — Time-management patch (opening damp, neutral first-move factors — 3+2 first move 19s → 6s) + UCI protocol hardening: guaranteed ponder hint on every bestmove (a bare bestmove stalls Arena's Permanent Brain until the engine is restarted), no bestmove leak on self-terminated ponder/infinite searches, fault-proof command loop, Debug Log File traffic log. ✅ (+14.3 ± 13.5 Elo SPRT vs v2.6.7, LOS 98.1%; **~2920 ± 20 CCRL** — confirmed at CCRL exact TC 40/15 round-robin)
- **v2.6.8** — Time-management patch: bullet sustainability guard — the per-move budget is bounded by `inc + clock/16` (target) and `inc + clock/4` (hard deadline) in sudden-death, so the clock stabilizes around the increment instead of decaying geometrically (2+1 with 5s left: hard deadline 3.96s → 2.22s; fixes time losses in won bullet games). ✅ Non-regression confirmed vs v2.6.7.1 (420 games [0.509], +6.9 ± 23.7 Elo, LOS 71.7%). Dev record: block 4H polynomial material imbalance was attempted and cut (SPRT a: −30, b: ±0 — texel piece values already absorbed the synergies; rescue path: joint texel retune WITH the term, see ROADMAP.md Revisión Final).
- **Next** — reference classical eval block (winnable/scale factors) · NNUE retrain · Lazy SMP · Book/tablebases · see [ROADMAP.md](ROADMAP.md).

Full change history in [CHANGELOG.md](CHANGELOG.md).

### **Contributing**
Contributions, suggestions, and feedback are welcome.
Please check the [Contribution Guide](CONTRIBUTING.md) and open issues or pull requests.

### License
This project is licensed under the **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)** license.
**Use, copy, modification, and redistribution are allowed for any NON-commercial purpose.**
**Any commercial use is strictly prohibited, except by the author or co-authors of the project.**
For other uses, please contact the owner.

---

## Motor de ajedrez UCI en C# (.NET 10)

NoaChess es un motor de ajedrez UCI modular escrito desde cero en C# sobre .NET 10, con su propia GUI de tablero en WPF. Sigue un desarrollo incremental y medido: cada versión se valida con tests unitarios, Perft, matches automáticos entre motores y estimación de Elo antes de avanzar.

**Versión actual: v2.6.8 — ~2920 ± 20 CCRL** (parche de gestión de tiempo: guardarraíl de sostenibilidad bullet — presupuesto por jugada acotado a `inc + reloj/16` objetivo e `inc + reloj/4` deadline duro en sudden-death, estabilizando el gasto en torno al incremento; no-regresión confirmada vs v2.6.7.1 en 420 partidas [0.509], +6.9 ± 23.7 Elo, LOS 71.7%). Ver [CHANGELOG](CHANGELOG.md).

### **Características principales**
- Representación por bitboards con **magic bitboards** para piezas deslizantes (lookup **PEXT/BMI2** en CPUs donde es rápido — Intel y AMD Zen3+ — con guarda automática por CPUID).
- Generación de movimientos legales completa (enroques, al paso, promociones), validada contra los valores públicos de referencia de Perft.
- **Búsqueda**: iterative deepening, aspiration windows con ensanchado progresivo, PVS, tabla de transposición (Zobrist), **generación de movimientos por etapas con legalidad perezosa** (jugada de TT servida sin generar, luego capturas, luego silenciosas), quiescence, null-move pruning, extensiones de jaque, singular extensions, SEE para ordenación y poda, ordenación por killers/history/counter-move/continuation history, LMR logarítmico informado por historia, reverse futility pruning, futility pruning, late move pruning, Internal Iterative Reductions, ProbCut.
- **Evaluación**: evaluación tapered (fase media/final) mezclada por fase de juego — valores PeSTO, tablas pieza-casilla de dos fases, bitboards de ataque al estilo de referencia (attackedBy por tipo de pieza + dobles ataques) que alimentan una evaluación de amenazas completa (piezas colgadas, amenazas de menor/torre/rey, amenazas de peón seguro y de avance de peón, piezas restringidas, amenazas de caballo/deslizantes sobre la dama), seguridad del rey (unidades de ataque + escudo de peones mediante curva cuadrática), movilidad no-lineal de piezas (tablas lookup de referencia sobre un área de movilidad al estilo de referencia, con ataques x-ray y restricción de piezas clavadas), bonus de tempo, outposts de caballo, espacio, par de alfiles, torres en columnas abiertas/semiabiertas y en séptima fila, estructura de peones tapered (doblados/aislados/pasados/retrasados, peones en falange, pasados conectados, pasados bloqueados, torre detrás del pasado); término mop-up para finales ganados.
- **Infraestructura NNUE**: runtime de red neuronal HalfKP, acumuladores incrementales, inferencia SIMD; opcional mediante opciones UCI `UseNNUE` / `EvalFile`.
- **Gestión de tiempo**: presupuestos soft/hard, uso del 85% del incremento, horizonte adaptativo conservador por ply, soporte movestogo, márgenes de seguridad para la latencia de la GUI.
- **UCI completo**: uci, isready, ucinewgame, setoption (Hash, Threads, MoveOverhead, Profile, UseNNUE, EvalFile), position, go (depth/nodes/movetime/reloj/ponder/infinite), stop, quit. Búsqueda asíncrona; se publica como .exe único autocontenido.
- Perfiles de motor (Default/Bullet) con parámetros de búsqueda/tiempo ajustables.
- **GUI WPF** (MVVM): juega contra el motor con piezas vectoriales (SVG Cburnett), barra de evaluación y profundidad en vivo, análisis en segundo plano durante tu turno.
- Hot paths sin allocations; suite de benchmarks con BenchmarkDotNet.

### **Estructura de la solución**
```
src/NoaChess.Core      -> reglas puras: tablero, bitboards, movimientos, FEN, Zobrist
src/NoaChess.Engine    -> búsqueda, evaluación, heurísticas, TT, gestión de tiempo
src/NoaChess.UCI       -> host de consola UCI (el ejecutable del motor para GUIs)
src/NoaChess.GUI.Wpf   -> aplicación de tablero WPF (MVVM)
tests/                 -> suites xUnit (Perft, make/unmake, búsqueda, SEE...)
benchmarks/            -> micro-benchmarks BenchmarkDotNet
```
Regla de dependencias: `Core` no depende de nada; `Engine` usa `Core`; `UCI` y la GUI son hosts finos. El plan completo está en [ROADMAP.md](ROADMAP.md).

### **Compilar y ejecutar**
```bash
dotnet test                                   # suite de tests completa
dotnet run --project src/NoaChess.GUI.Wpf    # jugar contra el motor (Windows)
dotnet publish src/NoaChess.UCI -c Release -o out   # .exe UCI único autocontenido
```
El `NoaChess.UCI.exe` publicado funciona en cualquier GUI UCI (Arena, CuteChess, BanksiaGUI...).

### **Tecnologías**
- **Lenguaje**: C# 14, .NET 10 LTS
- **Testing**: xUnit; validación Perft; gauntlets automáticos con cutechess-cli
- **Rendimiento**: BenchmarkDotNet
- **GUI**: WPF (MVVM), SharpVectors, MdXaml

### **Roadmap y versiones**
- **v0.1** — MVP jugable: reglas, alpha-beta, tablero WPF. ✅
- **v0.2** — Motor medible: TT, quiescence, move ordering, LMR. ✅
- **v1.0** — Search competitivo y UCI estable: PVS, null move, SEE, pawn hash, gestión de tiempo. ✅
- **v1.1** — Optimización NPS (magic bitboards, cero allocations), perfil Bullet, benchmarks. ✅ (~2070 Elo)
- **v2.0** — Infraestructura NNUE. ✅
- **v2.2** — Evaluación clásica tapered + podas de búsqueda (LMR log, RFP, futility, LMP). ✅ (~2600 Elo)
- **v2.3** — Búsqueda core: continuation/counter-move history, singular extensions, LMR por historia, IIR, ProbCut. ✅ (~2710 Elo medido)
- **v2.4** — Términos de evaluación (outposts de caballo, pasados avanzados, torre en séptima, espacio) + texel tuning completo (términos posicionales + PSTs) con datos propios frescos. ✅ (+13 ± 13 Elo por SPRT contra la v2.3.0)
- **v2.4.5** — Evaluación Fase A: tempo, peones en falange (phalanx), peones retrasados (backward) + retune completo con datos frescos. ✅ (+12 ± 15 Elo por SPRT contra la v2.4.0)
- **v2.5.0** — Bloque de velocidad: generación por etapas, legalidad perezosa, PEXT con guarda de CPU. ✅ (+101 ± 37 Elo por SPRT contra la v2.4.5; ~2768 CCRL en gauntlet LTC de 392 partidas)
- **v2.6.0** — Infraestructura attackedBy (bitboards de ataque al estilo de referencia, habilitador de amenazas / movilidad / seguridad del rey). ✅ (neutral en evaluación)
- **v2.6.1** — Evaluación de amenazas completa de referencia (10 términos, reescalados a unidades NoaChess). ✅ (+103 ± 35 Elo por SPRT contra la v2.5.0 — el mayor salto de evaluación del proyecto)
- **v2.6.2** — Movilidad no-lineal (tablas lookup de referencia, re-centradas), ataques x-ray, área de movilidad de referencia, restricción de piezas clavadas. ✅ (+6.6 ± 11.5 Elo por SPRT contra la v2.6.1, LOS 87%; **2780 ± 20 CCRL** medidos en dos gauntlets LTC independientes, 2700+ partidas)
- **v2.6.3** — Seguridad del rey completa de referencia: tablas shelter/storm, KingOnFile, máximo de shelter pre-enroque, proximidad rey-peones en el final, fórmula de peligro de referencia (sin safe checks), términos de flanco del rey, Rook/BishopOnKingRing. ✅ (+76.9 ± 31.2 Elo SPRT contra v2.6.2, LOS 100%; **2800 ± 25 CCRL** medidos)
- **v2.6.4** — Gestión de tiempo: uso del 85% del incremento + horizonte adaptativo conservador por ply (se probó una extensión por inestabilidad del best move y se revirtió — regresó a ritmo rápido y sobregastaba en bullet). ✅ **2875 ± 20 CCRL medido** (gauntlet LTC 2728 partidas, campo 2580–2917)
- **v2.6.5** — Términos de piezas de referencia (TrappedRook, RookOnClosedFile, BishopPawns, BishopXRayPawns, LongDiagonalBishop, MinorBehindPawn, outposts exactos con pawn-attacks-span, UncontestedOutpost, WeakQueen; KingProtector desactivado) + gestor de tiempo completo de referencia (optimum/maximum + factores fallingEval/estabilidad/inestabilidad). ✅ (+19.5 ± 13.6 Elo SPRT vs v2.6.4, LOS 99.7%; **2835 ± 25 CCRL** medidos, 880 partidas LTC)
- **v2.6.6** — Peones pasados de referencia (definición de pasado de referencia + candidatos, filtro de pasados bloqueados, proximidad de reyes a la casilla de bloqueo, escalera de seguridad del camino a dama, PassedFile). ✅ (+45.8 ± 23.1 Elo SPRT vs v2.6.5, LOS 100%; **2880 ± 25 CCRL** estimado)
- **v2.6.7** — Cadena de evaluación de estructura de peones de referencia (fórmula Connected completa, WeakUnopposed, WeakLever, DoubledEarly, peones bloqueados en filas 5-6, semántica Doubled/Isolated/Backward de referencia). ✅ (+28.4 ± 17.5 Elo SPRT vs v2.6.6, LOS 99.9%; **2895 ± 25 CCRL** estimados)
- **v2.6.7.1** — Parche de gestión de tiempo (freno de apertura, factores neutros de primer movimiento — primera jugada a 3+2: 19s → 6s) + endurecimiento del protocolo UCI: hint de ponder garantizado en cada bestmove (un bestmove desnudo atasca el Permanent Brain de Arena hasta reiniciar el motor), sin fuga de bestmove en búsquedas ponder/infinite auto-terminadas, bucle de comandos a prueba de fallos, log de tráfico Debug Log File. ✅ (+14.3 ± 13.5 Elo SPRT vs v2.6.7, LOS 98.1%; **~2920 ± 20 CCRL** — confirmado en round-robin a ritmo exacto CCRL 40/15)
- **v2.6.8** — Parche de gestión de tiempo: guardarraíl de sostenibilidad en bullet — el presupuesto por jugada se acota a `inc + reloj/16` (objetivo) e `inc + reloj/4` (deadline duro) en sudden-death, estabilizando el gasto en torno al incremento (2+1 con 5s restantes: deadline 3.96s → 2.22s; corrige pérdidas por tiempo en partidas bullet ganadas). ✅ No-regresión confirmada vs v2.6.7.1 (420 partidas [0.509], +6.9 ± 23.7 Elo, LOS 71.7%). Registro de desarrollo: el bloque 4H de desequilibrio material polinómico fue intentado y cortado (SPRT a: −30, b: ±0 — los valores de pieza texel ya habían absorbido las sinergias medias; rescate: retune texel conjunto CON el término, ver ROADMAP.md Revisión Final).
- **Siguiente** — Bloque de evaluación clásica de referencia (factores winnable/escala) · Re-entrenamiento NNUE · Lazy SMP · Libro/tablebases · ver [ROADMAP.md](ROADMAP.md).

Historial de cambios completo en [CHANGELOG.md](CHANGELOG.md).

### **Contribuir**
Se aceptan contribuciones, sugerencias y feedback.
Por favor, revisa la [Guía de Contribución](CONTRIBUTING.md) y abre issues o pull requests para colaborar.

### **Licencia**
Este proyecto está publicado bajo la licencia **Creative Commons Attribution-NonCommercial 4.0 International (CC BY-NC 4.0)**.
**Está permitido el uso, copia, modificación y redistribución para cualquier fin NO comercial**.  
**Queda estrictamente prohibido cualquier uso comercial, salvo por el autor o coautores del proyecto.**
Para otros usos, contacta con el titular.

### **Texto legal y resumen en español**

- El texto legalmente vinculante de esta licencia está en inglés y se incluye en el fichero LICENSE de este repositorio:  
  https://creativecommons.org/licenses/by-nc/4.0/legalcode

- Para facilitar la comprensión, existe un resumen oficial en español:  
  https://creativecommons.org/licenses/by-nc/4.0/deed.es

> *Nota: la traducción al español es solo informativa. En caso de discrepancia, prevalece el texto legal en inglés.*

---

### **Contacto / Contact**

**Autor principal / Main Author:**  
Juan Carlos Jiménez Vadillo

- GitHub: [jcjimenezvadillo](https://github.com/jcjimenezvadillo)  

---
