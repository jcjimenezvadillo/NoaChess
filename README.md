# NoaChess

[Spanish below]

## UCI Chess Engine in C# (.NET 10)

NoaChess is a modular UCI chess engine written from scratch in C# on .NET 10, with its own WPF board GUI. It follows an incremental, measured development process: every version is validated with unit tests, Perft, automated engine matches and Elo estimation before moving on. Development is AI-assisted; the architecture, the one-term-per-SPRT methodology, the calibrated gauntlets and every accept/cut decision are the author's own.

**Current version: v2.8.4 — +9.2 ±9.1 Elo vs v2.8.3 (3000 games exhausted positive, LOS 97.5%, LLR 1.91); LTC gauntlet pending.** Two LMR reduction adjusters on the fixed-point pipeline: ttCapture (r += 1079, +7.1 screened) and ttPv (r -= ~1 ply, +7.5 screened), validated together against the shipped v2.8.3 baseline. cutNode threaded through search as infrastructure for future allNode/cutoffCnt; its isolated LMR term rejected at both magnitudes (−4.0 and −7.1). 276/276 tests. See [CHANGELOG](CHANGELOG.md).

### **Main Features**
- Bitboard board representation with **magic bitboards** for sliding pieces (**PEXT/BMI2** lookup on CPUs where it is fast — Intel and AMD Zen3+ — with an automatic CPUID guard).
- Full legal move generation (castling, en passant, promotions), validated against the public Perft reference values.
- **Search**: iterative deepening, aspiration windows with progressive widening, PVS, transposition table (Zobrist), **staged move generation with lazy legality** (TT move served without generating, then captures, then quiets), quiescence search, null-move pruning, singular extensions, SEE (static exchange evaluation) ordering and pruning, killer/history/counter-move/continuation-history ordering, history-informed logarithmic LMR, reverse futility pruning, futility pruning, late move pruning, Internal Iterative Reductions, ProbCut.
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
- **Language**: C# 12, .NET 10 LTS
- **IDE**: Visual Studio + extensions
- **Testing**: xUnit, FluentAssertions, Moq
- **API**: ASP.NET Core WebAPI (future)
- **CI/CD**: GitHub Actions (roadmap)
- **Performance**: BenchmarkDotNet, .NET profiling tools

### **Roadmap & Versions** (newest first)
- **v2.8.4** — **LMR ttCapture + ttPv adjusters on the fixed-point pipeline. +9.2 ±9.1 Elo vs v2.8.3 (3000 games exhausted positive, LOS 97.5%, LLR 1.91); LTC gauntlet pending.** ttCapture: when the TT move is a capture, late quiets are reduced ~1 ply more (r += 1079). ttPv: when the node was on a previous PV, late quiets are reduced ~1 ply less (r -= 1024 + adjustments). Both screened individually (>93% LOS) and validated together vs v2.8.3. cutNode threaded through search as infrastructure (isolated term rejected at both magnitudes). ContinuationHistory MaxScore corrected to 8192. Dead LMR history term removed. New lesson: clean categorical signals (TT-move type, PV membership) work; noisy derived signals (cut-node classification, raw history magnitude) do not. 276/276 tests.
- **v2.8.3** — **Working history gravity. +24.4 ±17.5 Elo vs v2.8.2 (835 games); LTC gauntlet +112 ±24 relative.** The "bounded gravity" credited in v2.8.2 never ran: its decay term `score×|bonus|/MaxScore` integer-truncates to zero at a 2²⁰ bound against values near 7 000. Sizing the butterfly bound at 7183, as the reference does, makes it act — measured mean +71.8 → +13.5, tail 6086 → 3134. The LMR pipeline also moved to fixed point (1024ths), verified behaviour-neutral by identical node counts. statScore as the LMR history term was re-measured at the real time control and cut at −18 Elo, closing an evidence gap where block 5C had judged it on a 5+0.05 match. Caveat: the SPRT was stopped by hand at LLR 2.61 against a 2.94 bound, so it is a strong point estimate rather than a passed test. 276/276 tests.
- **v2.8.2** — **Validated search audit and UCI log hardening. SPRT H1: 256-189-389, +28.0 ±17.2 Elo vs v2.8.1; LTC gauntlet 296-131-197, +94 ±23 relative, ~3013 ±30 CCRL.** Adds pawn correction history to main search and quiescence while preserving raw static evaluation in the TT; reworks ProbCut around mandatory regular-search verification and exempts queen promotions from its simplified SEE gate; gives continuation history bounded gravity updates; and makes UCI logging explicit-only, lifecycle-safe and able to distinguish clean `quit` from stdin EOF. The first all-features candidate failed H0 at -13.1 ±15.2 Elo. The final H1 build kept correction and ProbCut but restored the fixed aspiration window and hard killer/counter bands and removed the unconditional check extension.
- **v2.8.1** — **Syzygy correctness fixes + 5G move ordering. ~3000 ±25 CCRL.** Two critical bugs in v2.8.0: (1) the root Syzygy filter was silently nullified — `SearchRoot` regenerated all moves after `FilterRootMovesByTablebase`, discarding the filter entirely; (2) DTZ ranking at the root scored irreversible moves as if they had not yet happened and chose the fastest loss in losing positions. Fixed. TT safety: `CanReuseTtScore` now blocks reuse of TB-band scores when the halfmove clock is non-zero. `SyzygyTable` migrated to `MemoryMappedFile` with `long` offsets, eliminating the 2 GB `byte[]` ceiling for future 6/7-man files. Move ordering: capture history now feeds the main search (not just quiescence); partial quiet sort (`PartialSortRange`, `−3000×depth` cutoff) with `MoveRangeToFront` guaranteeing QUIET before BAD_CAPTURE; `CheckBonus +16 384` for safe direct checks; threat-escape/enter bonus using pawn/minor/rook threat maps. X-ray mobility: sliders now see through own queen only (bug since v2.6.2). UCI: `Ponder` option declared. Portable Syzygy tests via `NOACHESS_SYZYGY_PATH`. New: `CaptureHistoryTests`, `UciSearchLimitsTests`; development tools: `NoaChess.DataGen` (NNUE self-play), `NoaChess.Tuner` (Texel), Python NNUE training pipeline. ✅ (**+14.1 ±10.8 Elo SPRT vs v2.7.4, LOS 99.5%, H1 at 2175 games; +75 ±23 gauntlet LTC; ~3000 ±25 CCRL**)
- **v2.8.0** — **Syzygy endgame tablebases.** Exact results at five men or fewer: WDL probed in the search (gated on the fifty-move counter, verdicts in their own band below the mate range) and root moves filtered by WDL then DTZ, so a won ending is converted instead of shuffled into a fifty-move draw. Root handling is a filter and NOT an early return, which keeps "mate in N" reporting intact. UCI: `SyzygyPath`, `SyzygyProbeDepth`, `SyzygyProbeLimit`, `Syzygy50MoveRule`. A ~1250-line managed port rather than the P/Invoke the roadmap assumed — no C toolchain here, and a native DLL would break the single-exe requirement — differentially verified against an independent prober over **3000 random endgames with zero WDL and zero DTZ discrepancies**, which caught three bugs that would otherwise have reached play silently. Measured: a won KPvK converts in 15 plies against 25 without. 208/208 tests. ⚠️ (two critical bugs fixed in v2.8.1 made the root filter and DTZ ranking a functional no-op — never independently validated)
- **After NNUE** — King Safety Phase B and KingProtector, deferred behind block 6 on 2026-07-23: both live inside the classical evaluator that NNUE supersedes, and their documented remedy is a full global texel retune whose product is classical-eval parameters.
- **Next** — NNUE training (block 6), with the tablebases now available to adjudicate datagen and relabel every ≤5-man position with exact WDL, deliberately before NNUE (reference order: in-play endgame strength now, plus datagen adjudication and WDL relabelling later) · NNUE training · Lazy SMP · competition book last · see [ROADMAP.md](ROADMAP.md).
- **Search block 5 is CLOSED.** Shipped: 5A improving, 5B scope-cut NMP/RFP, 5D TT redesign, corrected quiescence, capture-history ordering, verified ProbCut, continuation-history gravity, and (v2.8.4) ttCapture + ttPv LMR adjusters. Measured and cut: 5C statScore and cutNode LMR adjusters, all history-based LMR adjusters (three variants), 5E singular, multi-level continuation blending, multi-cut, dynamic NMP R, adaptive initial aspiration, unconditional check extension and continuous killer/counter bonuses. Remaining 5C candidates (cutoffCnt, allNode) require per-ply counter infrastructure; deferred to post-NNUE given uncertain gain and the meta-lesson on noisy signals.
- **v2.7.4** — **Quiescence rework, a correctness release.** In check the search no longer stands pat on a meaningless static eval (it could return a beta cutoff *while being mated*), generates ALL moves instead of captures only (a quiet king step or an interposition is usually the only escape), prunes nothing, and returns mate properly. Every capture that gives check lands the opponent in that node, so the hole sat on the main line of every tactical sequence — and ProbCut, null-move probes and multi-cut all verify captures through quiescence, so they were reading wrong scores as proof. Adds a stalemate guard, fail-soft scores and all four promotion pieces; ports the reference's quiescence pruning block whole (futility margin 147, SEE floor relaxed to −36, captures ordered by a new gravity-updated **capture-history** table). Separately fixes a hang present since forever: a **terminal root** (mate or stalemate on the board) looped through every depth without ever sending `bestmove`, freezing any GUI that handed the engine such a position. −5.7% nodes, −9.0%/−12.6% wall time to depth, NPS neutral, **WAC 269/300 (new record)**, 192/192 tests. ✅ (**no measurable strength change**: SPRT −2.1 ± 9.9 over 2347 games, H0; LTC gauntlet +52 ± 23 vs v2.7.2's +48 on the identical field; **~2975 ± 25 CCRL unchanged**)
- **(v2.7.3)** — ❌ cut with no release after a double campaign measured at the real TC: 5E singular-extension upgrade (four SPRTs, all at or below equity — reference extensions need reference-grade reductions first) and 5G multi-level continuation history (four builds converging from −33.9 to exact equity; per-distance tables, gravity updates and a depth≥6 blend gate were built, proven correct, and kept — the residual zero is caused by the fixed killer/counter-move ordering bands sitting above all history). Both blocks are archived with concrete revisit plans for the pre-NNUE checkpoint; the engine remains v2.7.2 exactly.
- **v2.7.2** — 5D transposition-table redesign (formerly 5F, pulled forward and renumbered after the 5C heuristic suite measured negative and was cut): 16-byte entries, 4-entry clusters per 64-byte cache line, generation aging with the reference `depth − 8×age` replacement, cached static eval served on TT hits (+24% NPS) plus eval-only entries on misses, sticky ttPv flag (consumer-less until play-tested). −19% nodes, WAC 265/300 record. ✅ (**+37.9 ± 15.0 pooled SPRT** at 1103 games, two independent H1 runs both LOS 100%; **+48 ± 23 relative at LTC; ~2975 ± 25 CCRL**)
- **v2.7.1** — 5B, scope cut by measurement: NMP verification search at depth ≥ 14 (nmpMinPly), fail-soft null cutoffs, statScore-informed reverse futility margin — 21–45% fewer nodes; the full reference NMP bundle was SPRT-rejected and dissected into three ecosystem prerequisites (qsearch checks, NNUE eval, 5C reductions). Plus mate-search fixes: iterative deepening no longer stops on mate scores (longest defense when mated, shortest mate when mating — WAC 259 → 262) and UCI `score mate N` reporting. ✅ (+2.9 ± 7.4 SPRT STC pooled over 4347 games; **+44 ± 23 relative at LTC; ~2970 ± 25 CCRL** — search gains grow with TC, the LTC gauntlet carries the signal)
- **v2.7.0** — 5A improving flag (per-ply static-eval stack; `eval[ply] > eval[ply-2]` gates LMR +1 ply, RFP margin ×(depth−improving), LMP threshold halved when worsening) — opens the search block. ✅ (+4.0 ± 27.1 SPRT STC; **+43 ± 23 relative at LTC vs v2.6.9's +16 on the identical field — search gains grow with TC; ~2965 ± 25 CCRL measured**)
- **v2.6.9** — 4I winnable / endgame scale factors (complexity initiative with almostUnwinnable, opposite-colored-bishop scaling, single-flank rook endings, queen-vs-no-queen, no-pawn material draws KBK/KRKB/KmmKm) — **classical evaluation block complete**. ✅ (+34.3 ± 19.5 Elo SPRT vs v2.6.8, LOS 100%; **~2941 ± 25 CCRL** measured, 624 LTC games)
- **v2.6.8** — 4H material-imbalance polynomial (Romstad second-degree, bishop-pair diagonal zeroed, packed-counts cache) + joint texel retune of piece values WITH the polynomial active (N+20, B+34, R+126, Q+223 over PeSTO; BishopPair 67/110) + bullet sustainability time guard (`inc + clock/16` target, `inc + clock/4` hard deadline in sudden-death). ✅ (+78.4 ± 31.5 Elo SPRT vs v2.6.7.1, LOS 100%; **~2944 ± 15 CCRL** measured, 1560 LTC games)
- **v2.6.7.1** — Time-management patch (opening damp, neutral first-move factors — 3+2 first move 19s → 6s) + UCI protocol hardening: guaranteed ponder hint on every bestmove (a bare bestmove stalls Arena's Permanent Brain until the engine is restarted), no bestmove leak on self-terminated ponder/infinite searches, fault-proof command loop, Debug Log File traffic log. ✅ (+14.3 ± 13.5 Elo SPRT vs v2.6.7, LOS 98.1%; **~2920 ± 20 CCRL** — confirmed at CCRL exact TC 40/15 round-robin)
- **v2.6.7** — Reference pawn-structure scoring chain (full Connected formula, WeakUnopposed, WeakLever, DoubledEarly, blocked pawns on ranks 5-6, reference Doubled/Isolated/Backward semantics). ✅ (+28.4 ± 17.5 Elo SPRT vs v2.6.6, LOS 99.9%; **2895 ± 25 CCRL** estimated)
- **v2.6.6** — Reference passed pawns (reference passed definition + candidate passers, blocked-passer filter, king proximity to the block square, path-to-queen safety ladder, PassedFile). ✅ (+45.8 ± 23.1 Elo SPRT vs v2.6.5, LOS 100%; **2880 ± 25 CCRL** estimated)
- **v2.6.5** — Reference piece terms (TrappedRook, RookOnClosedFile, BishopPawns, BishopXRayPawns, LongDiagonalBishop, MinorBehindPawn, reference-exact outposts with pawn-attacks-span, UncontestedOutpost, WeakQueen; KingProtector disabled) + full reference time manager (optimum/maximum + fallingEval/stability/instability stop factors). ✅ (+19.5 ± 13.6 Elo SPRT vs v2.6.4, LOS 99.7%; **2835 ± 25 CCRL** measured, 880 LTC games)
- **v2.6.4** — Time management: 85% increment use + conservative ply-adaptive horizon (a best-move instability extension was tried and reverted — regressed at fast TC and overspent in bullet). ✅ **2875 ± 20 CCRL measured** (LTC gauntlet 2728 games, field 2580–2917)
- **v2.6.3** — Full reference-engine king safety: shelter/storm tables, KingOnFile, pre-castling shelter max, EG king-pawn proximity, reference king-danger formula (no safe checks), king flank terms, Rook/BishopOnKingRing. ✅ (+76.9 ± 31.2 Elo SPRT vs v2.6.2, LOS 100%; **2800 ± 25 CCRL** measured)
- **v2.6.2** — Non-linear mobility (reference lookup tables, re-centered), x-ray attacks, reference mobility area, pinned-piece attack restriction. ✅ (+6.6 ± 11.5 Elo SPRT vs v2.6.1, LOS 87%; **2780 ± 20 CCRL** measured across two independent LTC gauntlets, 2700+ games)
- **v2.6.1** — Full reference-engine threat evaluation (10 terms, rescaled to NoaChess units). ✅ (+103 ± 35 Elo SPRT vs v2.5.0 — largest single evaluation gain of the project)
- **v2.6.0** — attackedBy infrastructure (reference-style attack bitboards, enabler for threats / mobility / king safety). ✅ (evaluation-neutral)
- **v2.5.0** — Speed block: staged move generation, lazy legality, PEXT with CPU guard. ✅ (+101 ± 37 Elo SPRT vs v2.4.5; ~2768 CCRL in a 392-game LTC gauntlet)
- **v2.4.5** — Evaluation Fase A: tempo bonus, phalanx/connected pawns, backward pawns + full retune on fresh data. ✅ (+12 ± 15 Elo SPRT vs v2.4.0)
- **v2.4** — Evaluation terms (knight outposts, advanced passers, rook on 7th, space) + full texel tuning (positional terms + PSTs) on fresh self-play data. ✅ (+13 ± 13 Elo SPRT vs v2.3.0)
- **v2.3** — Search core overhaul: continuation/counter-move history, singular extensions, history-based LMR, IIR, ProbCut. ✅ (~2710 Elo measured)
- **v2.2** — Tapered classical evaluation overhaul + search pruning (log LMR, RFP, futility, LMP). ✅ (~2600 Elo)
- **v2.0** — NNUE evaluation infrastructure. ✅
- **v1.1** — NPS optimization (magic bitboards, zero-alloc paths), Bullet profile, benchmarks. ✅ (~2070 Elo)
- **v1.0** — Competitive search & stable UCI: PVS, null move, SEE, pawn hash, time management. ✅
- **v0.2** — Measurable engine: TT, quiescence, move ordering, LMR. ✅
- **v0.1** — Playable MVP: core rules, alpha-beta, WPF board. ✅

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

NoaChess es un motor de ajedrez UCI modular escrito desde cero en C# sobre .NET 10, con su propia GUI de tablero en WPF. Sigue un desarrollo incremental y medido: cada versión se valida con tests unitarios, Perft, matches automáticos entre motores y estimación de Elo antes de avanzar. El desarrollo está asistido por IA (Claude como herramienta de pair-programming); la arquitectura, la metodología de un término por SPRT, los gauntlets calibrados y cada decisión de aceptar o cortar un término son del autor.

**Versión actual: v2.8.3 — +24.4 ±17.5 Elo frente a v2.8.2 (835 partidas, intervalo que excluye el cero; la tirada se cortó a mano con LLR 2.61 frente a una banda de 2.94, así que no es un H1 formal); gauntlet LTC +112 ±24 relativo al campo** (gravedad que sí actúa en la tabla butterfly — la de v2.8.2 era numéricamente inerte con cota 2²⁰ — más el pipeline de LMR en punto fijo, verificado neutro). Ver [CHANGELOG](CHANGELOG.md).

### **Características principales**
- Representación por bitboards con **magic bitboards** para piezas deslizantes (lookup **PEXT/BMI2** en CPUs donde es rápido — Intel y AMD Zen3+ — con guarda automática por CPUID).
- Generación de movimientos legales completa (enroques, al paso, promociones), validada contra los valores públicos de referencia de Perft.
- **Búsqueda**: iterative deepening, aspiration windows con ensanchado progresivo, PVS, tabla de transposición (Zobrist), **generación de movimientos por etapas con legalidad perezosa** (jugada de TT servida sin generar, luego capturas, luego silenciosas), quiescence, null-move pruning, singular extensions, SEE para ordenación y poda, ordenación por killers/history/counter-move/continuation history, LMR logarítmico informado por historia, reverse futility pruning, futility pruning, late move pruning, Internal Iterative Reductions, ProbCut.
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
- **Lenguaje**: C# 12, .NET 10 LTS
- **IDE**: Visual Studio + extensiones
- **Testing**: xUnit, FluentAssertions, Moq
- **API**: ASP.NET Core WebAPI (futuro)
- **CI/CD**: GitHub Actions (roadmap)
- **Performance**: BenchmarkDotNet, profiling .NET

### **Roadmap y versiones** (de más reciente a más antigua)
- **v2.8.3** — **Gravedad de historia que sí funciona. +24.4 ±17.5 Elo frente a v2.8.2 (835 partidas); gauntlet LTC +112 ±24 relativo.** La "gravedad acotada" que v2.8.2 se acreditó nunca llegó a ejecutarse: su término de decaimiento `score×|bonus|/MaxScore` trunca a cero con una cota de 2²⁰ frente a valores cercanos a 7 000. Dimensionar la cota de butterfly en 7183, como hace la referencia, la activa — media medida +71.8 → +13.5, cola 6086 → 3134. El pipeline de LMR pasó además a punto fijo (1024avos), verificado neutro por conteo de nodos idéntico. statScore como término de historia en LMR se remidió al ritmo real y quedó cortado en −18 Elo, cerrando un hueco de evidencia donde el bloque 5C lo había juzgado con un match a 5+0.05. Salvedad: el SPRT se cortó a mano con LLR 2.61 frente a una banda de 2.94, así que es una estimación puntual sólida, no un test superado. 276/276 tests.
- **v2.8.2** — **Auditoría de búsqueda validada y endurecimiento del log UCI. SPRT H1: 256-189-389, +28.0 ±17.2 Elo frente a v2.8.1; gauntlet LTC 296-131-197, +94 ±23 relativo, ~3013 ±30 CCRL.** Añade pawn correction history a búsqueda principal y quiescence manteniendo la evaluación estática cruda en la TT; rehace ProbCut con verificación obligatoria mediante búsqueda normal y deja las promociones a dama fuera del gate SEE simplificado; aplica gravedad acotada a continuation history; y hace que el log UCI sea exclusivamente explícito, seguro durante todo su ciclo de vida y capaz de distinguir `quit` limpio de EOF. El primer candidato con todos los experimentos falló H0 con -13.1 ±15.2 Elo. La versión final H1 conservó correction y ProbCut, pero restauró la ventana fija de aspiración y las bandas duras killer/counter y eliminó la extensión incondicional de jaque.
- **v2.8.1** — **Correcciones Syzygy + ordenación 5G. ~3000 ±25 CCRL.** Dos bugs críticos de v2.8.0: (1) el filtro de raíz se anulaba — `SearchRoot` regeneraba todas las jugadas tras `FilterRootMovesByTablebase`, descartando el filtro; (2) el ranking DTZ puntuaba las jugadas irreversibles antes de que ocurrieran y elegía la derrota más rápida. Seguridad TT: `CanReuseTtScore` bloquea scores en banda TB cuando `halfmoveClock > 0`. `SyzygyTable` migrado a `MemoryMappedFile` + offsets `long` (sin límite de 2 GB para 6/7 piezas). Ordenación: capture history integrado en la búsqueda principal (7×víctima + historia); partial sort quiets (−3000×depth, QUIET antes de BAD_CAPTURE); CheckBonus +16 384; bonus escape/entrada de amenazas. X-ray: sliders solo transparentan la dama propia. UCI: opción `Ponder`. 268 tests descubiertos (193 ejecutados con los ficheros Syzygy ausentes). ✅ (+14.1 ±10.8 SPRT vs v2.7.4, LOS 99.5%, H1; +75 ±23 gauntlet LTC)
- **v2.8.0** — **Tablebases Syzygy.** Resultado exacto con cinco piezas o menos: WDL consultado dentro de la búsqueda (condicionado al contador de 50 jugadas, con los veredictos en su propia banda por debajo del rango de mate) y jugadas de raíz filtradas por WDL y luego DTZ, para que un final ganado se convierta en vez de acabar en tablas por repetición. En la raíz es un **filtro y no un retorno inmediato**, lo que preserva el anuncio de "mate en N". UCI: `SyzygyPath`, `SyzygyProbeDepth`, `SyzygyProbeLimit`, `Syzygy50MoveRule`. Port nativo de ~1250 líneas en vez del P/Invoke que asumía el roadmap —no hay compilador de C aquí, y una DLL nativa rompería el requisito de un único exe— verificado contra un prober independiente sobre **3000 finales aleatorios sin una sola discrepancia**, lo que cazó tres bugs que habrían llegado a partida en silencio. Medido: un KPvK ganado se convierte en 15 plies frente a 25 sin tablebases. 208/208 tests. ⚠️ (dos bugs críticos corregidos en v2.8.1 hacían el filtro de raíz y el ranking DTZ un no-op en la práctica)
- **Después del NNUE** — King Safety Fase B y KingProtector, aplazados detrás del bloque 6 el 2026-07-23: los dos viven dentro del evaluador clásico que NNUE sustituye, y su remedio documentado es un retune texel global cuyo producto son parámetros de eval clásica.
- **Siguiente** — Entrenamiento NNUE (bloque 6), ya con las tablebases disponibles para adjudicar el datagen y re-etiquetar con WDL exacto toda posición de ≤5 piezas, a propósito antes del NNUE (orden de la referencia: fuerza en finales ya, y adjudicación + re-etiquetado WDL del datagen después) · Entrenamiento NNUE · Lazy SMP · libro de competición al final · ver [ROADMAP.md](ROADMAP.md).
- **El bloque 5 de búsqueda está CERRADO.** Embarcaron 5A improving, 5B NMP/RFP recortado, 5D TT, quiescence corregida, capture-history ordering, ProbCut verificado y gravedad en continuation history. Se midieron y cortaron 5C, 5E, el blend multinivel, multi-cut, la R dinámica del NMP, la aspiración inicial adaptativa, la extensión incondicional de jaque y los bonus continuos killer/counter. El paquete final v2.8.2 pasó H1; ningún experimento se conserva por sus A/B cortos si el conjunto validado lo contradice.
- **v2.7.4** — **Rework de la quiescence: versión de corrección.** En jaque la búsqueda ya no hace stand-pat sobre un eval estático sin sentido (podía devolver un corte por beta *estando mateada*), genera **todas** las jugadas en vez de solo capturas (la única salida de un jaque suele ser un movimiento tranquilo de rey o una interposición), no poda nada y detecta mate. Cada captura que da jaque deja al rival exactamente en ese nodo, así que el agujero estaba en la línea principal de toda secuencia táctica — y ProbCut, las probes de null-move y multi-cut verifican capturas **a través** de la quiescence, con lo que estaban leyendo esas puntuaciones falsas como prueba. Añade guard de ahogado, puntuaciones fail-soft y las cuatro piezas de promoción; porta entero el bloque de poda de la referencia (margen de futility 147, umbral SEE relajado a −36, capturas ordenadas por una nueva tabla de **capture history** con updates por gravedad). Aparte, arregla un cuelgue que llevaba ahí desde siempre: con **mate o ahogado en la raíz** el motor recorría todas las profundidades sin emitir jamás `bestmove`, congelando cualquier GUI que le pasara esa posición. −5.7% nodos, −9.0%/−12.6% de tiempo real hasta profundidad, NPS neutro, **WAC 269/300 (récord)**, 192/192 tests. ✅ (**sin cambio de fuerza medible**: SPRT −2.1 ± 9.9 en 2347 partidas, H0; gauntlet LTC +52 ± 23 frente al +48 de la v2.7.2 en el campo idéntico; **~2975 ± 25 CCRL sin cambios**)
- **(v2.7.3)** — ❌ cortada sin release tras una campaña doble medida al TC real: 5E singular extensions (cuatro SPRTs, todos en equidad o peor — las extensiones de la referencia necesitan antes reducciones de su calibre) y 5G historia de continuación multinivel (cuatro builds convergiendo de −33.9 a equidad exacta; las tablas por distancia, los updates con gravity y el gate de blend a depth≥6 quedaron construidos, probados y conservados — el cero residual lo causan las bandas fijas de killers/counter-move por encima de toda la historia). Ambos bloques archivados con plan concreto de revisita para el checkpoint pre-NNUE; el motor sigue siendo v2.7.2 exacto.
- **v2.7.2** — Bloque 5D (antes 5F), rediseño de la transposition table (adelantado y renumerado tras medir y cortar la suite heurística de 5C): entradas de 16 bytes, clusters de 4 por línea de caché de 64B, aging por generación con el reemplazo `depth − 8×edad` de la referencia, eval estático cacheado servido en hits (+24% nps) más entradas eval-only en misses, flag ttPv pegajoso (sin consumidor hasta probarlo por juego). −19% nodos, WAC 265/300 récord. ✅ (**+37.9 ± 15.0 SPRT agrupado** a 1103 partidas, dos runs H1 independientes ambos LOS 100%; **+48 ± 23 relativo a LTC; ~2975 ± 25 CCRL**)
- **v2.7.1** — Bloque 5B, alcance recortado por medición: búsqueda de verificación del NMP a profundidad ≥ 14 (nmpMinPly), cortes nulos fail-soft, margen de reverse futility informado por statScore — 21–45% menos nodos; el bundle completo del NMP de referencia fue rechazado por SPRT y disecado en tres prerequisitos de ecosistema (jaques en quiescence, eval NNUE, reducciones de 5C). Más arreglos de búsqueda de mate: el iterative deepening ya no se detiene en scores de mate (defensa más larga al perder, mate más corto al ganar — WAC 259 → 262) y reporte UCI `score mate N`. ✅ (+2.9 ± 7.4 SPRT STC agrupando 4347 partidas; **+44 ± 23 relativo a LTC; ~2970 ± 25 CCRL** — las ganancias de búsqueda crecen con el ritmo, el gauntlet LTC lleva la señal)
- **v2.7.0** — Bloque 5A: improving flag (pila de eval estático por ply; `eval[ply] > eval[ply-2]` modula LMR +1 ply, margen RFP ×(depth−improving), umbral LMP a la mitad si empeora) — abre el bloque de búsqueda. ✅ +4.0 ± 27.1 SPRT STC; **+43 ± 23 relativo a LTC frente a los +16 de v2.6.9 con campo idéntico — la ganancia de búsqueda crece con el ritmo; ~2965 ± 25 CCRL medido**.
- **v2.6.9** — Bloque 4I: winnable / factores de escala de final (complexity con almostUnwinnable, escalado por alfiles de colores opuestos, finales de torre a un flanco, dama contra sin dama, tablas de material sin peones KBK/KRKB/KmmKm) — **bloque de evaluación clásica COMPLETO**. ✅ +34.3 ± 19.5 Elo SPRT vs v2.6.8, LOS 100%, H1 aceptado a 580 partidas; **~2941 ± 25 CCRL** medido (gauntlet 624 partidas, campo 2680–3200).
- **v2.6.8** — Bloque 4H: polinomio de desequilibrio material de referencia (Romstad segundo grado, diagonal par de alfiles zeroed) + retune texel conjunto de los valores de pieza CON el polinomio activo (offsets N+20 B+34 R+126 Q+223, BishopPair 67/110) + guardarraíl de sostenibilidad en bullet (presupuesto acotado a `inc + reloj/16` objetivo e `inc + reloj/4` deadline duro). ✅ +78.4 ± 31.5 Elo SPRT vs v2.6.7.1, LOS 100%, H1 aceptado a 284 partidas; **~2944 ± 15 CCRL** medido (gauntlet 1560 partidas, campo 2680–3200).
- **v2.6.7.1** — Parche de gestión de tiempo (freno de apertura, factores neutros de primer movimiento — primera jugada a 3+2: 19s → 6s) + endurecimiento del protocolo UCI: hint de ponder garantizado en cada bestmove (un bestmove desnudo atasca el Permanent Brain de Arena hasta reiniciar el motor), sin fuga de bestmove en búsquedas ponder/infinite auto-terminadas, bucle de comandos a prueba de fallos, log de tráfico Debug Log File. ✅ (+14.3 ± 13.5 Elo SPRT vs v2.6.7, LOS 98.1%; **~2920 ± 20 CCRL** — confirmado en round-robin a ritmo exacto CCRL 40/15)
- **v2.6.7** — Cadena de evaluación de estructura de peones de referencia (fórmula Connected completa, WeakUnopposed, WeakLever, DoubledEarly, peones bloqueados en filas 5-6, semántica Doubled/Isolated/Backward de referencia). ✅ (+28.4 ± 17.5 Elo SPRT vs v2.6.6, LOS 99.9%; **2895 ± 25 CCRL** estimados)
- **v2.6.6** — Peones pasados de referencia (definición de pasado de referencia + candidatos, filtro de pasados bloqueados, proximidad de reyes a la casilla de bloqueo, escalera de seguridad del camino a dama, PassedFile). ✅ (+45.8 ± 23.1 Elo SPRT vs v2.6.5, LOS 100%; **2880 ± 25 CCRL** estimado)
- **v2.6.5** — Términos de piezas de referencia (TrappedRook, RookOnClosedFile, BishopPawns, BishopXRayPawns, LongDiagonalBishop, MinorBehindPawn, outposts exactos con pawn-attacks-span, UncontestedOutpost, WeakQueen; KingProtector desactivado) + gestor de tiempo completo de referencia (optimum/maximum + factores fallingEval/estabilidad/inestabilidad). ✅ (+19.5 ± 13.6 Elo SPRT vs v2.6.4, LOS 99.7%; **2835 ± 25 CCRL** medidos, 880 partidas LTC)
- **v2.6.4** — Gestión de tiempo: uso del 85% del incremento + horizonte adaptativo conservador por ply (se probó una extensión por inestabilidad del best move y se revirtió — regresó a ritmo rápido y sobregastaba en bullet). ✅ **2875 ± 20 CCRL medido** (gauntlet LTC 2728 partidas, campo 2580–2917)
- **v2.6.3** — Seguridad del rey completa de referencia: tablas shelter/storm, KingOnFile, máximo de shelter pre-enroque, proximidad rey-peones en el final, fórmula de peligro de referencia (sin safe checks), términos de flanco del rey, Rook/BishopOnKingRing. ✅ (+76.9 ± 31.2 Elo SPRT contra v2.6.2, LOS 100%; **2800 ± 25 CCRL** medidos)
- **v2.6.2** — Movilidad no-lineal (tablas lookup de referencia, re-centradas), ataques x-ray, área de movilidad de referencia, restricción de piezas clavadas. ✅ (+6.6 ± 11.5 Elo por SPRT contra la v2.6.1, LOS 87%; **2780 ± 20 CCRL** medidos en dos gauntlets LTC independientes, 2700+ partidas)
- **v2.6.1** — Evaluación de amenazas completa de referencia (10 términos, reescalados a unidades NoaChess). ✅ (+103 ± 35 Elo por SPRT contra la v2.5.0 — el mayor salto de evaluación del proyecto)
- **v2.6.0** — Infraestructura attackedBy (bitboards de ataque al estilo de referencia, habilitador de amenazas / movilidad / seguridad del rey). ✅ (neutral en evaluación)
- **v2.5.0** — Bloque de velocidad: generación por etapas, legalidad perezosa, PEXT con guarda de CPU. ✅ (+101 ± 37 Elo por SPRT contra la v2.4.5; ~2768 CCRL en gauntlet LTC de 392 partidas)
- **v2.4.5** — Evaluación Fase A: tempo, peones en falange (phalanx), peones retrasados (backward) + retune completo con datos frescos. ✅ (+12 ± 15 Elo por SPRT contra la v2.4.0)
- **v2.4** — Términos de evaluación (outposts de caballo, pasados avanzados, torre en séptima, espacio) + texel tuning completo (términos posicionales + PSTs) con datos propios frescos. ✅ (+13 ± 13 Elo por SPRT contra la v2.3.0)
- **v2.3** — Búsqueda core: continuation/counter-move history, singular extensions, LMR por historia, IIR, ProbCut. ✅ (~2710 Elo medido)
- **v2.2** — Evaluación clásica tapered + podas de búsqueda (LMR log, RFP, futility, LMP). ✅ (~2600 Elo)
- **v2.0** — Infraestructura NNUE. ✅
- **v1.1** — Optimización NPS (magic bitboards, cero allocations), perfil Bullet, benchmarks. ✅ (~2070 Elo)
- **v1.0** — Search competitivo y UCI estable: PVS, null move, SEE, pawn hash, gestión de tiempo. ✅
- **v0.2** — Motor medible: TT, quiescence, move ordering, LMR. ✅
- **v0.1** — MVP jugable: reglas, alpha-beta, tablero WPF. ✅

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
