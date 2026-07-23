# NoaChess

[Spanish below]

## UCI Chess Engine in C# (.NET 10)

NoaChess is a modular UCI chess engine written from scratch in C# on .NET 10, with its own WPF board GUI. It follows an incremental, measured development process: every version is validated with unit tests, Perft, automated engine matches and Elo estimation before moving on.

**Current strength: ~2720 Elo estimated** (v2.4.0: +13.0 ± 12.6 Elo SPRT over v2.3.0, which measured ~2710 CCRL-equivalent in a 231-game LTC precision gauntlet vs 7 engines rated 2580–2788 — see [CHANGELOG](CHANGELOG.md)).

### **Main Features (v2.4.0)**
- Bitboard board representation with **magic bitboards** for sliding pieces.
- Full legal move generation (castling, en passant, promotions), validated against the public Perft reference values.
- **Search**: iterative deepening, aspiration windows with progressive widening, PVS, transposition table (Zobrist), quiescence search, null-move pruning, check extensions, singular extensions, SEE (static exchange evaluation) ordering and pruning, killer/history/counter-move/continuation-history ordering, history-informed logarithmic LMR, reverse futility pruning, futility pruning, late move pruning, Internal Iterative Reductions, ProbCut.
- **Evaluation**: tapered (middlegame/endgame) evaluation blended by game phase — PeSTO piece values, two-phase piece-square tables, king safety (attack units + pawn shield through quadratic danger curve), piece mobility, knight outposts, space, bishop pair, rooks on open/semi-open files and on the 7th rank, tapered pawn structure (doubled/isolated/passed, connected passers, blocked passers, rook behind passer); mop-up term for won endgames.
- **NNUE infrastructure**: HalfKP neural network runtime, incremental accumulators, SIMD inference; optional via UCI `UseNNUE` / `EvalFile` options.
- **Time management**: soft/hard budgets, movestogo support, GUI-latency safety margins.
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
Dependency rule: `Core` depends on nothing; `Engine` uses `Core`; `UCI` and the GUI are thin hosts. The full plan lives in [NoaChess_ROADMAP.md](NoaChess_ROADMAP.md).

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

### **Roadmap & Versions**
- **v0.1** — Playable MVP: core rules, alpha-beta, WPF board. ✅
- **v0.2** — Measurable engine: TT, quiescence, move ordering, LMR. ✅
- **v1.0** — Competitive search & stable UCI: PVS, null move, SEE, pawn hash, time management. ✅
- **v1.1** — NPS optimization (magic bitboards, zero-alloc paths), Bullet profile, benchmarks. ✅ (~2070 Elo)
- **v2.0** — NNUE evaluation infrastructure. ✅
- **v2.2** — Tapered classical evaluation overhaul + search pruning (log LMR, RFP, futility, LMP). ✅ (~2600 Elo)
- **v2.3** — Search core overhaul: continuation/counter-move history, singular extensions, history-based LMR, IIR, ProbCut. ✅ (~2710 Elo measured)
- **v2.4** — Evaluation terms (knight outposts, advanced passers, rook on 7th, space) + full texel tuning (positional terms + PSTs) on fresh self-play data. ✅ (+13 ± 13 Elo SPRT vs v2.3.0)
- **Next** — Speed · NNUE retrain · Lazy SMP · Book/tablebases · see the full roadmap.

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

**Fuerza actual: ~2720 Elo estimado** (v2.4.0: +13.0 ± 12.6 Elo por SPRT sobre la v2.3.0, que midió ~2710 equivalente CCRL en un gauntlet LTC de precisión de 231 partidas contra 7 motores de 2580–2788 — ver [CHANGELOG](CHANGELOG.md)).

### **Características principales (v2.4.0)**
- Representación por bitboards con **magic bitboards** para piezas deslizantes.
- Generación de movimientos legales completa (enroques, al paso, promociones), validada contra los valores públicos de referencia de Perft.
- **Búsqueda**: iterative deepening, aspiration windows con ensanchado progresivo, PVS, tabla de transposición (Zobrist), quiescence, null-move pruning, extensiones de jaque, singular extensions, SEE para ordenación y poda, ordenación por killers/history/counter-move/continuation history, LMR logarítmico informado por historia, reverse futility pruning, futility pruning, late move pruning, Internal Iterative Reductions, ProbCut.
- **Evaluación**: evaluación tapered (fase media/final) mezclada por fase de juego — valores PeSTO, tablas pieza-casilla de dos fases, seguridad del rey (unidades de ataque + escudo de peones mediante curva cuadrática), movilidad de piezas, outposts de caballo, espacio, par de alfiles, torres en columnas abiertas/semiabiertas y en séptima fila, estructura de peones tapered (doblados/aislados/pasados, pasados conectados, pasados bloqueados, torre detrás del pasado); término mop-up para finales ganados.
- **Infraestructura NNUE**: runtime de red neuronal HalfKP, acumuladores incrementales, inferencia SIMD; opcional mediante opciones UCI `UseNNUE` / `EvalFile`.
- **Gestión de tiempo**: presupuestos soft/hard, soporte movestogo, márgenes de seguridad para la latencia de la GUI.
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
Regla de dependencias: `Core` no depende de nada; `Engine` usa `Core`; `UCI` y la GUI son hosts finos. El plan completo está en [NoaChess_ROADMAP.md](NoaChess_ROADMAP.md).

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

### **Roadmap y versiones**
- **v0.1** — MVP jugable: reglas, alpha-beta, tablero WPF. ✅
- **v0.2** — Motor medible: TT, quiescence, move ordering, LMR. ✅
- **v1.0** — Search competitivo y UCI estable: PVS, null move, SEE, pawn hash, gestión de tiempo. ✅
- **v1.1** — Optimización NPS (magic bitboards, cero allocations), perfil Bullet, benchmarks. ✅ (~2070 Elo)
- **v2.0** — Infraestructura NNUE. ✅
- **v2.2** — Evaluación clásica tapered + podas de búsqueda (LMR log, RFP, futility, LMP). ✅ (~2600 Elo)
- **v2.3** — Búsqueda core: continuation/counter-move history, singular extensions, LMR por historia, IIR, ProbCut. ✅ (~2710 Elo medido)
- **v2.4** — Términos de evaluación (outposts de caballo, pasados avanzados, torre en séptima, espacio) + texel tuning completo (términos posicionales + PSTs) con datos propios frescos. ✅ (+13 ± 13 Elo por SPRT contra la v2.3.0)
- **Siguiente** — Velocidad · Re-entrenamiento NNUE · Lazy SMP · Libro/tablebases · ver roadmap completo.

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
