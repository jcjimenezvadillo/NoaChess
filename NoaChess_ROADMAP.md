# NoaChess - Roadmap completo de desarrollo

## Plataforma tecnologica

NoaChess se desarrolla sobre **.NET 10 LTS** y **C# 14**. Todos los proyectos de la solucion deben usar `net10.0`, salvo que una futura plataforma de despliegue exija explicitamente otro target framework.

```text
NoaChess.Core          -> Class Library -> .NET 10
NoaChess.Engine        -> Class Library -> .NET 10
NoaChess.UCI           -> Console App   -> .NET 10
NoaChess.GUI.Wpf       -> WPF App       -> .NET 10
NoaChess.Core.Tests    -> xUnit         -> .NET 10
NoaChess.Engine.Tests  -> xUnit         -> .NET 10
NoaChess.Benchmarks    -> Console App   -> .NET 10
```

El runtime, JIT y las capacidades SIMD se aprovecharan especialmente en los hot paths: bitboards, generacion de movimientos, `MakeMove`/`UnmakeMove`, evaluacion y busqueda. Toda optimizacion debe validarse con benchmarks y, cuando afecte a la fuerza de juego, mediante pruebas Elo o SPRT.


## 1. Objetivo del documento

Este documento define el roadmap completo de desarrollo de **NoaChess**, un motor de ajedrez escrito en **C# / .NET 10**, con arquitectura modular, interfaz UCI, GUI WPF y capacidad futura de evolucionar hacia un motor competitivo con NNUE, libros de aperturas, tablebases de finales, tuning automatico, self-play y pipelines de validacion continua.

El objetivo es que el equipo de desarrollo tenga una referencia unica para entender:

- Que se construye en cada version.
- Que objetivo tecnico tiene cada hito.
- Que arquitectura .NET se debe respetar.
- Que features se incorporan progresivamente.
- Que componentes deben estar testeados antes de cerrar cada release.
- Que ramas Git, artefactos y pipelines conviene usar.
- Como evolucionara el motor desde una primera version AlphaBeta hasta una arquitectura avanzada con NNUE, Policy, RL y componentes experimentales.

Este roadmap esta pensado para avanzar por etapas. No se debe implementar todo a la vez. Cada version debe poder compilar, jugar partidas completas, ejecutar tests automaticos y compararse contra la version anterior mediante herramientas como CuteChess-cli, Perft, SPRT y benchmarks internos.

---

## 2. Principios de arquitectura

### 2.1 Separacion principal

NoaChess debe mantenerse dividido en capas claras:

```text
[ NoaChess.Core ]  -> reglas puras de ajedrez, tablero, movimientos, bitboards
[ NoaChess.Engine ] -> busqueda, evaluacion, heuristicas, TT, NNUE, book, tablebases
[ NoaChess.UCI ]    -> adaptador de consola compatible con protocolo UCI
[ NoaChess.GUI.Wpf ]-> interfaz grafica Windows, tipo Fritz
[ NoaChess.Tests ]  -> tests unitarios, perft, integracion y regresion
[ tools/ ]          -> scripts Python/C++ para book, tuning, NNUE, SPRT, self-play
```

La GUI no debe llamar directamente a clases internas del motor para buscar jugadas. La GUI debe poder usar el motor igual que cualquier otra GUI externa: mediante protocolo UCI o mediante un adaptador que replique la misma separacion.

### 2.2 Regla clave de dependencias

`NoaChess.Core` no debe depender de:

- WPF.
- UCI.
- ONNX.
- Syzygy.
- Polyglot.
- Python.
- CuteChess.
- GitHub Actions.
- Cualquier detalle de infraestructura.

`NoaChess.Core` debe contener solamente:

- Representacion del tablero.
- Reglas legales del ajedrez.
- Movimientos.
- Bitboards.
- FEN.
- Zobrist base.
- Utilidades puras.

`NoaChess.Engine` usa `Core`, pero `Core` no conoce `Engine`.

### 2.3 Estructura recomendada del repositorio

```text
NoaChess/
|
+-- src/
|   |
|   +-- NoaChess.Core/
|   |   +-- Board/
|   |   +-- Bitboards/
|   |   +-- Moves/
|   |   +-- MoveGeneration/
|   |   +-- Hashing/
|   |   +-- Notation/
|   |
|   +-- NoaChess.Engine/
|   |   +-- Search/
|   |   +-- Evaluation/
|   |   |   +-- Classical/
|   |   |   +-- Nnue/
|   |   |   +-- Policy/
|   |   |   +-- Experimental/
|   |   +-- Heuristics/
|   |   +-- Transposition/
|   |   +-- OpeningBook/
|   |   +-- Tablebases/
|   |   +-- TimeManagement/
|   |   +-- Parallelism/
|   |
|   +-- NoaChess.UCI/
|   |   +-- Commands/
|   |   +-- Options/
|   |   +-- Protocol/
|   |
|   +-- NoaChess.GUI.Wpf/
|       +-- Views/
|       +-- ViewModels/
|       +-- Services/
|
+-- tests/
|   +-- NoaChess.Core.Tests/
|   +-- NoaChess.Engine.Tests/
|   +-- NoaChess.UCI.Tests/
|   +-- NoaChess.Perft.Tests/
|
+-- benchmarks/
|   +-- NoaChess.Benchmarks/
|
+-- tools/
|   +-- books/
|   +-- tuning/
|   +-- selfplay/
|   +-- training/
|   +-- sprt/
|   +-- leaderboard/
|
+-- models/
|   +-- nnue/
|   +-- policy/
|   +-- experimental/
|
+-- books/
|   +-- rapid/
|   +-- blitz/
|   +-- bullet/
|
+-- .github/
|   +-- workflows/
|
+-- CHANGELOG.md
+-- ROADMAP.md
+-- README.md
```

---

## 3. Estrategia Git y releases

### 3.1 Ramas principales

```text
main              -> releases estables
release/x.y       -> estabilizacion de version concreta
dev               -> integracion continua de features
feature/*         -> desarrollo de features aisladas
hotfix/*          -> correcciones urgentes sobre main
experimental/*    -> ramas de investigacion sin garantia de merge
rl-experimental   -> investigacion RLHead / actor-critic
transformers      -> investigacion Policy con Transformer
mobile            -> optimizaciones mobile / AOT / INT8
web               -> WebAssembly / web engine
tcec              -> configuraciones especificas para TCEC/OpenBench/CCRL
```

### 3.2 Protecciones recomendadas

Para `main`:

- Merge solo mediante Pull Request.
- CI obligatorio.
- Tests Perft obligatorios.
- Tests UCI obligatorios.
- Tests de regresion basicos obligatorios.
- No permitir force push.
- Requerir al menos una revision.

Para `release/*`:

- Igual que `main`.
- Solo fixes de estabilizacion.

Para `dev`:

- CI obligatorio.
- Puede permitir merges mas frecuentes.

Para `feature/*`:

- Sin proteccion estricta.
- Debe integrarse en `dev` mediante PR.

### 3.3 Versionado semantico

Se recomienda usar SemVer:

```text
MAJOR.MINOR.PATCH
```

Ejemplo:

```text
0.1.0 -> primer motor funcional
1.0.0 -> primer release UCI estable
2.0.0 -> primera version NNUE
2.3.0 -> apertura Polyglot oficial
3.0.0 -> Lazy SMP + Syzygy
```

### 3.4 CHANGELOG

Formato recomendado: Keep a Changelog.

Ejemplo base:

```markdown
# Changelog

## [Unreleased]

### Added
- ...

### Changed
- ...

### Fixed
- ...

## [0.1.0] - YYYY-MM-DD

### Added
- First legal move generator.
- AlphaBeta baseline.
- Basic UCI loop.
```

---

## 4. Roadmap general resumido

```text
v0.1.0  Core ajedrecistico funcional
  |
v0.1.1  Tablero WPF visible
  |
v0.1.2  Interaccion humana
  |
v0.1.3  Motor conectado al tablero
v0.2  Motor medible contra otros motores
v1.0  Search competitivo y UCI estable
v1.1  Rendimiento y Bullet
v2.0  NNUE
v2.1  Policy Head
v2.2  Multi-phase Policy
v2.3  Base de aperturas Polyglot
v3.0  Lazy SMP + Syzygy Tablebases
v3.1  Mobile + Web
v3.2  Perfiles de competicion Rapid/Blitz/Bullet
v4.0  RLHead experimental
v4.1  Self-play avanzado y curriculum
v5.0  Transformer Policy experimental
v5.1  Arquitectura hibrida experimental
```

---

# 5. Versiones detalladas

---

## v0.1 - Primera version funcional y visible

La v0.1 se divide en cuatro entregas incrementales. Primero se valida el nucleo, despues la representacion visual, a continuacion la interaccion humana y finalmente el ciclo humano contra motor.

La regla arquitectonica principal es que existe una unica fuente de verdad: `Board` en `NoaChess.Core`. La GUI nunca mantiene un segundo tablero ni implementa reglas de ajedrez.

### v0.1.0 - Core ajedrecistico funcional

**Objetivo:** construir un nucleo correcto, determinista, rapido y completamente testeable, sin dependencias de GUI, UCI ni busqueda.

**Funcionalidad:**

- Bitboards de 64 bits.
- Tipos de dominio para color, pieza, casilla y movimiento.
- Estado completo: turno, enroques, en passant, halfmove clock y fullmove number.
- Generacion de peones, caballos y rey.
- Sliding Moves y Magic Bitboards para torres y alfiles.
- Dama como union de ataques de torre y alfil.
- `MagicData`, masks, magic numbers, shifts y attack tables.
- `MakeMove` y `UnmakeMove` incrementales.
- Capturas, doble avance, en passant y enroques.
- Promociones a dama, torre, alfil y caballo.
- Deteccion de jaque y filtrado de movimientos ilegales.
- Parsing y serializacion FEN.
- Zobrist hashing.
- Tests unitarios y Perft.

**Arquitectura:**

```text
NoaChess.Core/
|-- Board/
|-- Bitboards/
|-- Moves/
|-- MoveGeneration/
|-- Hashing/
`-- Notation/
```

`NoaChess.Core` no conoce WPF, UCI, Search, NNUE, Polyglot ni Syzygy.

**Criterios de salida:**

- Perft coincide con las posiciones de referencia.
- `MakeMove` + `UnmakeMove` restaura exactamente estado y hash.
- El generador legal no deja al propio rey en jaque.
- Todas las reglas especiales tienen tests.
- CI ejecuta los tests automaticamente.

### v0.1.1 - Tablero WPF visible

**Objetivo:** crear la primera version visible de NoaChess y representar una posicion real del Core.

**Funcionalidad:**

- Aplicacion WPF sobre .NET 10.
- Tablero dinamico 8x8.
- Representacion de las 64 casillas.
- Piezas vinculadas al estado real de `NoaChess.Core`.
- Posicion inicial y carga desde FEN.
- Orientacion desde blancas o negras.
- Actualizacion visual cuando cambia `Board`.
- Recursos graficos desacoplados de la logica.

**MVVM:**

```text
Board (NoaChess.Core)
        |
        v
BoardViewModel
        |
        v
WPF Grid 8x8
```

La vista representa el dominio. No calcula movimientos y no mantiene un tablero paralelo.

**Arquitectura:**

```text
NoaChess.GUI.Wpf/
|-- Views/
|-- ViewModels/
|-- Services/
`-- Resources/
```

**Criterios de salida:**

- Posicion inicial correcta.
- Cualquier FEN valida se representa correctamente.
- Los cambios en `Board` se reflejan en la vista.
- La orientacion se invierte sin modificar el Core.

### v0.1.2 - Interaccion humana con el tablero

**Objetivo:** permitir que una persona ejecute movimientos legales desde WPF.

**Funcionalidad:**

- Seleccion de piezas.
- Resaltado de origen y movimientos legales.
- Movimiento clic-clic.
- Drag and drop opcional.
- Rechazo de movimientos ilegales.
- Capturas, enroque y en passant.
- Dialogo de promocion.
- Indicacion del ultimo movimiento.
- Actualizacion inmediata de la vista.
- Deteccion visual basica de fin de partida.

**Flujo:**

```text
User input
   |
   v
BoardViewModel
   |
   v
MoveGenerator / Board
   |
   v
MakeMove
   |
   v
View refresh
```

**Criterios de salida:**

- Puede reproducirse una partida humana completa.
- Todas las jugadas especiales funcionan desde la GUI.
- La interfaz no permite posiciones ilegales.
- Las reglas residen exclusivamente en Core.

### v0.1.3 - Motor conectado al tablero

**Objetivo:** completar el primer ciclo jugable humano contra NoaChess.

**Funcionalidad:**

- Evaluador clasico inicial.
- Alpha-Beta basico.
- Loop de busqueda.
- Jugada humana legal.
- Busqueda de respuesta del motor.
- Aplicacion de `bestmove` sobre el mismo `Board`.
- Actualizacion automatica de la GUI.
- UCI basico: `position`, `go` y `bestmove`.
- Cancelacion de busqueda para no bloquear la UI.
- Tests de integracion Core-Engine-GUI.

**Flujo completo:**

```text
Usuario
   |
   v
NoaChess.GUI.Wpf
   |
   v
Board / MakeMove
   |
   v
NoaChess.Engine / Search
   |
   v
Best Move
   |
   v
Board / MakeMove
   |
   v
BoardViewModel refresh
```

**Criterios de salida:**

- El usuario puede jugar una partida contra NoaChess.
- La UI permanece responsiva durante la busqueda.
- El motor solo devuelve movimientos legales.
- GUI, Core y Search comparten la misma posicion logica.
- Las pruebas de integracion validan el ciclo completo.

## v0.2.0 - Primera version medible contra otros motores

### Objetivo

Convertir el motor legal en un motor medible. El objetivo es que NoaChess empiece a jugar contra otros motores, aunque todavia sea debil.

### Funcionalidades incluidas

#### Quiescence Search

Evita el efecto horizonte evaluando capturas en nodos hoja.

Flujo:

```text
depth == 0 -> Quiescence(alpha, beta)
```

Quiescence debe:

- Evaluar stand pat.
- Generar capturas.
- Ordenar capturas.
- Evitar capturas claramente malas con SEE cuando exista.

#### Transposition Table

Se incorpora TT basada en Zobrist.

Entrada tipica:

```csharp
public struct TTEntry
{
    public ulong Key;
    public int Depth;
    public int Score;
    public BoundType Bound;
    public Move BestMove;
}
```

Tipos de bound:

- Exact.
- LowerBound.
- UpperBound.

#### Iterative Deepening

Buscar por profundidades crecientes:

```text
depth 1
depth 2
depth 3
...
```

Ventajas:

- Permite cortar por tiempo.
- Mejora move ordering.
- Produce info UCI progresiva.

#### Aspiration Windows iniciales

En lugar de buscar siempre con ventana infinita, usar una ventana alrededor de la evaluacion anterior:

```text
alpha = lastScore - window
beta = lastScore + window
```

Si falla, se re-busca con ventana completa.

#### Move Ordering basico

Orden recomendado:

1. TT move.
2. Capturas buenas.
3. Killer moves.
4. History heuristic.
5. Movimientos restantes.

#### Killer Heuristic

Guardar movimientos no captura que causan beta cutoff en un ply.

#### History Heuristic

Incrementar puntuacion de movimientos que producen cortes.

#### LMR basico

Late Move Reductions:

- Reducir profundidad en movimientos tardios no prometedores.
- No aplicar a capturas, promociones, checks o nodos PV.

### Arquitectura .NET

Nuevos componentes:

```text
NoaChess.Engine/Search/
  IterativeDeepening.cs
  QuiescenceSearch.cs
  SearchContext.cs
  SearchLimits.cs
  SearchResult.cs

NoaChess.Engine/Transposition/
  TranspositionTable.cs
  TTEntry.cs
  BoundType.cs

NoaChess.Engine/Heuristics/
  MovePicker.cs
  KillerTable.cs
  HistoryTable.cs
```

### Criterios de salida

- CuteChess puede lanzar matches automaticos.
- El motor responde siempre con `bestmove`.
- No hay timeouts basicos.
- TT no causa errores por colisiones mal gestionadas.
- Se puede estimar Elo relativo contra motores debiles.

---

## v1.0.0 - Primer release oficial UCI estable

### Objetivo

Publicar la primera version oficial estable de NoaChess. Esta version debe tener un search competitivo, UCI completo, gestion de tiempo y mecanismos serios de validacion.

### Funcionalidades incluidas

#### UCI completo

Comandos:

```text
uci
isready
ucinewgame
setoption
position
go
stop
quit
```

Opciones iniciales:

```text
Hash
Threads
MoveOverhead
UseNNUE false
```

Mas adelante:

```text
BookFile
BookVariant
SyzygyPath
SyzygyProbeDepth
SyzygyProbeLimit
```

#### Time Management

Debe soportar:

```text
go wtime ... btime ... winc ... binc ...
go movetime ...
go depth ...
go nodes ...
```

Debe evitar:

- Perder por tiempo.
- Consumir todo el tiempo en apertura.
- Pensar demasiado en posiciones faciles.

#### PVS - Principal Variation Search

Tras el primer movimiento, los siguientes se buscan con ventana nula:

```text
[-alpha - 1, -alpha]
```

Si mejoran alpha, se re-buscan con ventana completa.

#### Null Move Pruning

Si el motor puede pasar turno y aun asi la posicion parece buena, se corta la rama.

Condiciones:

- No aplicar en jaque.
- No aplicar en zugzwang evidente.
- Profundidad minima.

#### SEE y SEE Pruning

Static Exchange Evaluation estima si una captura gana o pierde material tacticamente.

Uso:

- Ordenar capturas.
- Podar capturas malas en Quiescence.
- Podar movimientos tacticamente malos en search.

#### Pawn Hash

La estructura de peones cambia poco, por tanto se cachea:

```text
pawnHash -> pawnStructureScore
```

Evalua:

- Peones doblados.
- Peones aislados.
- Peones pasados.
- Peones retrasados.

#### SPRT pipeline inicial

Comparar candidato vs baseline:

```text
H0 = 0 Elo
H1 = +5 Elo
alpha = 0.05
beta = 0.05
```

Un cambio solo se acepta si supera SPRT.

#### Leaderboard automatico

Generar `leaderboard.csv` y grafico para GitHub Pages.

### Arquitectura .NET

```text
NoaChess.Engine/Search/
  Search.cs
  PrincipalVariationSearch.cs
  SearchLimits.cs
  SearchInfo.cs

NoaChess.Engine/TimeManagement/
  TimeManager.cs

NoaChess.Engine/Evaluation/Classical/
  ClassicalEvaluator.cs
  PawnStructureEvaluator.cs
  PieceSquareTables.cs
  EvaluationParams.cs

NoaChess.UCI/
  UciOptionRegistry.cs
  UciLoop.cs
  UciPositionCommand.cs
  UciGoCommand.cs
```

### Criterios de salida

- UCI validado en al menos CuteChess y Banksia.
- 1000 partidas automaticas sin crash.
- Perft sigue correcto.
- SPRT no detecta regresion contra v0.2.
- Release en GitHub con binario, README y CHANGELOG.

---

## v1.1.0 - Optimizacion de Bullet y NPS

### Objetivo

Optimizar velocidad, reducir overhead y preparar el motor para controles rapidos.

### Funcionalidades incluidas

#### Optimizacion de NPS

Medir y optimizar:

- Move generation.
- MakeMove / UnmakeMove.
- Evaluacion.
- TT lookup.
- Move ordering.
- Allocations.

Eliminar en hot paths:

- LINQ.
- Listas innecesarias.
- Clonaciones de Board en search.
- Boxing.
- Strings.

#### Perfil Bullet

Crear parametros agresivos:

- LMR mas fuerte.
- LMP mas agresivo.
- Menor overhead de info UCI.
- Menor probing de Syzygy cuando exista.
- Time management mas conservador.

#### BenchmarkDotNet

Benchmarks para:

- Generacion de movimientos.
- Perft.
- Evaluacion.
- Make/Unmake.
- NNUE inference cuando exista.

### Arquitectura .NET

```text
NoaChess.Benchmarks/
  MoveGenerationBenchmarks.cs
  MakeMoveBenchmarks.cs
  EvaluationBenchmarks.cs
  SearchBenchmarks.cs

NoaChess.Engine/Profiles/
  EngineProfile.cs
  BulletProfile.cs
  RapidProfile.cs
```

### Criterios de salida

- Mejora NPS medida.
- No hay perdida significativa de fuerza en SPRT.
- Perfil Bullet validado contra baseline.

---

## v2.0.0 - Primera version NNUE

### Objetivo

Incorporar una evaluacion neural eficiente para sustituir o complementar la evaluacion clasica.

### Funcionalidades incluidas

#### Feature Extractor

Crear extractor HalfKP o alternativa definida.

Debe convertir una posicion en vector de features:

```text
Board -> feature vector -> NNUE -> score
```

#### Training data

Generar datos desde:

- PGN de self-play.
- PGN de motores fuertes.
- Evaluaciones Stockfish como target.
- Posiciones EPD.

#### PyTorch training

Pipeline Python:

```text
generate_nnue_training_data.py
train_nnue.py
export_onnx.py
```

#### ONNX export

Exportar el modelo a ONNX para consumirlo desde C#.

#### Integracion en NoaChess

El motor debe poder elegir:

```text
UseNNUE true/false
```

Abstraccion:

```csharp
public interface IPositionEvaluator
{
    int Evaluate(Board board);
}
```

Implementaciones:

- `ClassicalEvaluator`
- `NnueEvaluator`

### Arquitectura .NET

```text
NoaChess.Engine/Evaluation/
  IPositionEvaluator.cs
  Classical/ClassicalEvaluator.cs
  Nnue/NnueEvaluator.cs
  Nnue/FeatureExtractor.cs
  Nnue/OnnxNnueSession.cs

models/nnue/
  NoaChess_v2.onnx

tools/training/
  generate_nnue_training_data.py
  train_nnue.py
  export_onnx.py
```

### Criterios de salida

- NNUE produce evaluaciones estables.
- El motor puede jugar con NNUE activado.
- SPRT demuestra mejora o se mantiene como opcion experimental.
- El modelo esta versionado.

---

## v2.1.0 - Policy Head

### Objetivo

Mejorar el orden de movimientos usando una Policy Head que prediga que movimientos son prometedores.

### Funcionalidades incluidas

#### Policy Output

La red devuelve:

```text
value_output
policy_logits_output
```

#### Encoding de movimientos

Se define un espacio fijo de movimientos posibles, por ejemplo 4672 indices.

Debe existir:

```csharp
int MoveToPolicyIndex(Move move);
Move PolicyIndexToMove(int index);
```

#### Move Ordering con Policy

Orden recomendado:

1. TT move.
2. Capturas ganadoras.
3. Policy score.
4. Killers.
5. History.
6. Resto.

La Policy no debe sustituir heuristicas tacticas al principio.

#### Entrenamiento Value + Policy

Loss combinada:

```text
loss = value_loss + policy_weight * policy_loss
```

### Arquitectura .NET

```text
NoaChess.Engine/Evaluation/Policy/
  IPolicyProvider.cs
  PolicyMoveEncoder.cs
  PolicyMoveScorer.cs
  PolicyBlender.cs
```

### Criterios de salida

- Policy mejora move ordering o no degrada.
- SPRT contra NNUE sin Policy.
- Validacion de encoding de movimientos.

---

## v2.2.0 - Multi-phase Policy

### Objetivo

Especializar la Policy para medio juego y final.

### Funcionalidades incluidas

#### Midgame y Endgame heads

Modelo con:

```text
value_output
policy_mid_output
policy_end_output
```

#### EvaluatePhase tunable

Calcular fase:

```text
0.0 -> final
1.0 -> medio juego
```

Pesos tunables:

- Peon.
- Caballo.
- Alfil.
- Torre.
- Dama.

#### Blending

```csharp
policy = (1 - phase) * policyEnd + phase * policyMid;
```

#### Tuning

Optuna puede tunear:

- Phase weights.
- Policy weight.
- Temperature.
- Interaccion con LMR y MovePicker.

### Arquitectura .NET

```text
NoaChess.Engine/Evaluation/Policy/
  MultiPhasePolicyProvider.cs
  GamePhaseEvaluator.cs
```

### Criterios de salida

- No degrada en finales.
- Mejora move ordering en posiciones con poco material.
- SPRT positivo o feature marcada experimental.

---

## v2.3.0 - Bases de aperturas Polyglot

### Objetivo

Integrar un sistema completo de libro de aperturas. Esta es la primera version que incorpora BBDD de aperturas.

### Concepto clave

NoaChess no se entrena internamente en aperturas. Se construye un archivo externo, por ejemplo:

```text
NoaChessBook.bin
```

Ese archivo contiene posiciones de apertura y jugadas con pesos. Cuando la posicion actual existe en el book, NoaChess juega instantaneamente una jugada del book.

### Fuentes de PGN

Fuentes recomendadas:

- KingBase: partidas de alta calidad.
- Lichess Elite: partidas modernas y variadas.
- PGN propios de self-play.
- PGN de torneos internos.

### Combinacion de PGNs

Metodo simple:

```bash
copy /b kingbase2024.pgn + lichess-elite-2024.pgn combined.pgn
python build_opening_book.py combined.pgn NoaChessBook.bin
```

Metodo pro:

- Mezclar multiples PGN con pesos.
- KingBase peso 1.0.
- Lichess Elite peso 0.5.
- Aplicar filtros por resultado.
- Filtrar jugadas con pocas partidas.
- Aplicar profundidad maxima.
- Aplicar temperatura.

### Weighted Book Builder

Parametros:

```text
MAX_PLIES
MIN_GAMES_PER_MOVE
PGN_WEIGHT
TEMPERATURE
DEPTH_FACTOR
```

### Seleccion de movimiento

Si una posicion tiene varias jugadas:

| Move | Weight |
|---|---:|
| e7e5 | 120 |
| c7c5 | 200 |
| g8f6 | 50 |

La probabilidad es:

```text
P(move) = weight / sum(weights)
```

Con temperatura:

```text
P(move) proporcional a weight^(1/T)
```

Temperatura baja:

- Mas solido.
- Menos variedad.

Temperatura alta:

- Mas explorador.
- Mas variedad.

### Book learning

Guardar fichero:

```text
NoaChessBook.learn
```

Cada partida puede ajustar pesos:

- Si una linea gana: subir peso.
- Si una linea pierde: bajar peso.
- Si empata: cambio neutro o pequeno.

### Book variants

Crear libros separados:

```text
NoaChessBook-Rapid.bin
NoaChessBook-Blitz.bin
NoaChessBook-Bullet.bin
```

UCI option:

```text
option name BookVariant type combo default Rapid var Rapid var Blitz var Bullet
option name BookFile type string default NoaChessBook-Rapid.bin
option name BookTemperature type spin/string
```

### Arquitectura .NET

```text
NoaChess.Engine/OpeningBook/
  IOpeningBook.cs
  PolyglotBook.cs
  PolyglotHash.cs
  BookMove.cs
  BookMoveSelector.cs
  BookLearningStore.cs
  BookOptions.cs

NoaChess.UCI/Options/
  BookFileOption.cs
  BookVariantOption.cs
  BookTemperatureOption.cs

tools/books/
  build_opening_book.py
  build_opening_book_pro.py
  run_book_sprt.py
  run_book_learning_update.py
```

### Criterios de salida

- El motor juega book moves correctamente.
- Si no hay book move, pasa a search normal.
- Book configurable por UCI.
- Book no rompe legalidad de movimientos.
- Tests de Polyglot hash.
- Benchmarks de apertura con CuteChess.

---

## v3.0.0 - Lazy SMP + Syzygy Tablebases

### Objetivo

Preparar el motor para competicion seria con multiples threads y finales perfectos. Esta es la version que introduce oficialmente las bases de datos de finales.

### Lazy SMP

En lugar de dividir explicitamente cada nodo, varios threads buscan de forma semiautonomica y comparten informacion clave:

- Transposition Table.
- Best move.
- PV.
- Estadisticas.

Cada worker debe tener su propio stack para evitar corrupciones.

### SplitPoint / SMP tradicional

Tambien puede existir una estrategia con SplitPoint:

- Nodo grande.
- Lista de movimientos compartida.
- Workers toman movimientos pendientes.
- Se sincroniza alpha/beta y abortos.

### Syzygy Tablebases

Syzygy proporciona juego perfecto en finales con pocas piezas.

Tipos:

- WDL: Win / Draw / Loss.
- DTZ: Distance To Zeroing move.

Recomendacion:

```text
6-men WDL  -> aproximadamente 150 GB
5-men DTZ  -> aproximadamente 1.1 GB
6-men DTZ  -> aproximadamente 900 GB - 1 TB
```

Por eso la recomendacion practica es:

- Usar 6-men WDL.
- Usar 5-men DTZ.
- Evitar 6-men DTZ salvo hardware dedicado.

### Opciones UCI Syzygy

```text
option name SyzygyPath type string default <empty>
option name SyzygyProbeDepth type spin default 1 min 1 max 100
option name Syzygy50MoveRule type check default true
option name SyzygyProbeLimit type spin default 6 min 1 max 6
```

### Uso en search

```csharp
if (board.PieceCount <= SyzygyProbeLimit && depth >= SyzygyProbeDepth)
{
    var result = tablebase.ProbeWdl(board);
    if (result.HasValue)
        return MapWdlToScore(result.Value);
}
```

En root tambien debe poder devolver la mejor jugada tablebase.

### Arquitectura .NET

```text
NoaChess.Engine/Parallelism/
  SearchWorker.cs
  SearchManager.cs
  SearchThreadPool.cs
  SplitPoint.cs
  LazySmpController.cs

NoaChess.Engine/Tablebases/
  ITablebaseProber.cs
  SyzygyProber.cs
  TablebaseResult.cs
  TablebaseOptions.cs
```

### Criterios de salida

- Determinismo con Threads=1.
- Estabilidad con Threads > 1.
- No hay crashes en matches largos.
- Syzygy configurable por UCI.
- El motor no pierde finales conocidos con tablebase disponible.

---

## v3.1.0 - Mobile y Web

### Objetivo

Crear builds portables para mobile y web.

### Mobile

Objetivos:

- ARM64.
- Modelo cuantizado INT8.
- Menor memoria.
- Menor latencia.
- Compatible con GUIs tipo DroidFish si se crea binario adecuado.

### WebAssembly

Objetivos:

- Build WASM.
- Uso en web propia.
- Posible demo online.

### Cuantizacion NNUE

Convertir FP32 a INT8:

```python
quantize_dynamic(
    "NoaChess_NNUE_Policy.onnx",
    "NoaChess_NNUE_Policy_INT8.onnx"
)
```

### Arquitectura .NET

Mantener hosts separados:

```text
NoaChess.UCI.Desktop
NoaChess.Web
NoaChess.Mobile
```

Evitar que el core dependa de APIs especificas de Windows.

### Criterios de salida

- Build desktop no se degrada.
- Modelo INT8 validado.
- Web demo funcional si se implementa.
- Documentacion de limitaciones.

---

## v3.2.0 - TCEC Cup, Blitz y perfiles de competicion

### Objetivo

Preparar perfiles de motor segun control de tiempo.

### Perfiles

```text
Classical
Rapid
Blitz
Bullet
TCEC
```

Cada perfil puede variar:

- LMR.
- LMP.
- Null Move.
- Syzygy probing.
- BookVariant.
- Time management.
- NNUE inference mode.

### Bullet

Bullet requiere:

- Maximo NPS.
- Menor overhead UCI.
- Menor uso de Syzygy.
- Book mas practico.
- Search mas agresivo.

### Arquitectura .NET

```text
NoaChess.Engine/Profiles/
  EngineProfile.cs
  EngineProfileFactory.cs
  ClassicalProfile.cs
  BlitzProfile.cs
  BulletProfile.cs
```

### Criterios de salida

- Profiles configurables por UCI.
- Benchmark vs motores externos.
- SPRT por perfil.

---

## v4.0.0 - RLHead experimental

### Objetivo

Investigar una cabeza adicional que aprenda resultado esperado a largo plazo mediante self-play.

### Componentes

Modelo:

```text
Value Head
Policy Head
RL Head / Outcome Head
```

Target RL:

```text
win  -> +1
draw -> 0
loss -> -1
```

### Actor-Critic loop

Ciclo:

```text
self-play
-> generar training data
-> entrenar Value + Policy + RL
-> exportar modelo
-> test SPRT
-> promocionar si mejora
```

### Uso en search

Combinar:

```text
score = lambda * value + (1 - lambda) * rlOutcome
```

O usar RL para reforzar move ordering.

### Arquitectura .NET

```text
NoaChess.Engine/Evaluation/Experimental/
  RlHeadEvaluator.cs
  HybridValueEvaluator.cs

tools/selfplay/
  selfplay_generator_rl.py

tools/training/
  train_nnue_policy_rl.py
```

### Criterios de salida

- No se integra en main si no supera SPRT.
- Debe permanecer feature flag.
- Debe documentarse dataset y modelo.

---

## v4.1.0 - RL avanzado y curriculum learning

### Objetivo

Mejorar el entrenamiento self-play para evitar sobreajuste y mejorar progresivamente.

### Funcionalidades

- Curriculum learning.
- Muestreo progresivo por dificultad.
- Replay buffer.
- Mezcla de posiciones tacticas, tranquilas y finales.
- TTA experimental para Policy.
- Multi-phase RLHead.

### Arquitectura

```text
tools/training/curriculum/
tools/training/replay_buffer/
tools/training/datasets/
```

### Criterios de salida

- Mejora validada contra baseline.
- Sin degradacion en controles cortos.
- Reproducibilidad de datasets.

---

## v5.0.0 - Transformer Policy experimental

### Objetivo

Investigar una Policy Head basada en Transformer o bloques de atencion ligeros.

### Motivo

Un Transformer puede capturar relaciones globales del tablero, pero es mas costoso que una MLP simple. No debe asumirse que mejora hasta probarlo.

### Arquitectura neural

Opciones:

- Transformer muy pequeno.
- 1-2 capas.
- Embeddings por pieza/casilla.
- Policy output.
- Value head compartida o separada.

### Integracion

Debe integrarse detras de:

```csharp
IPolicyProvider
```

El search no debe conocer si la policy viene de MLP, NNUE o Transformer.

### Criterios de salida

- Benchmarks de latencia.
- SPRT.
- Comparacion contra Policy Head convencional.
- Si no mejora, se mantiene experimental.

---

## v5.1.0 - Arquitectura hibrida experimental

### Objetivo

Combinar las mejores senales disponibles:

- Value.
- Policy.
- RL / Outcome.
- Transformer Policy.
- Heuristicas clasicas.

### Componentes

```text
IPositionEvaluator
IPolicyProvider
IOutcomePredictor
IMoveOrderingStrategy
ISearchStrategy
```

### Riesgo

Complejidad excesiva. Esta version es de investigacion. No debe sustituir al motor estable salvo que demuestre mejora clara.

### Criterios de salida

- SPRT positivo.
- No perdida significativa de NPS.
- No degradacion en Bullet.
- Estabilidad en matches largos.

---

# 6. Pipelines y herramientas

## 6.1 CuteChess-cli

Uso para matches automaticos:

```bash
cutechess-cli \
  -engine name=NoaChess cmd=NoaChess.exe \
  -engine name=Baseline cmd=NoaChess_baseline.exe \
  -each tc=40/10 \
  -games 200 \
  -repeat \
  -pgnout result.pgn
```

## 6.2 SPRT

Usar para aceptar/rechazar cambios:

```text
elo0 = 0
elo1 = 5
alpha = 0.05
beta = 0.05
```

Decision:

- Accept: promocionar candidato.
- Reject: descartar.
- Continue: seguir jugando partidas.

## 6.3 Optuna

Tunear parametros:

- LMR_A.
- LMR_B.
- NullR.
- AspirationWindow.
- SEEThreshold.
- Phase weights.
- Policy weight.
- Temperature.
- Book weights.

## 6.4 Opening book pipeline

```text
PGN sources
-> build_opening_book_pro.py
-> NoaChessBook.bin
-> CuteChess SPRT
-> promote book if better
-> update book learning
```

## 6.5 Syzygy tuning pipeline

Tunear:

- SyzygyProbeDepth.
- SyzygyProbeLimit.
- Syzygy50MoveRule.

## 6.6 NNUE pipeline

```text
self-play / PGN / EPD
-> generate_nnue_training_data.py
-> train_nnue.py
-> export_onnx.py
-> validate with CuteChess
-> SPRT promote
```

---

# 7. Criterios generales para cerrar una version

Una version no se considera cerrada si solamente compila. Debe cumplir:

1. Compilacion Release correcta.
2. Tests unitarios correctos.
3. Perft correcto.
4. UCI compatible.
5. Matches automaticos sin crash.
6. Sin regresion clara contra baseline.
7. CHANGELOG actualizado.
8. README actualizado si cambia uso externo.
9. Artefactos versionados.
10. Tag Git creado.

---

# 8. Recomendacion final de implementacion

El orden real recomendado es:

1. Terminar v0.1 con correccion total.
2. Validar MoveGenerator con Perft antes de optimizar.
3. Pasar a v0.2 y medir contra motores debiles.
4. No introducir NNUE hasta que AlphaBeta, UCI y Time Management sean estables.
5. No introducir opening book antes de que el motor sea medible sin book.
6. No introducir Syzygy antes de tener search estable.
7. No introducir RL/Transformer hasta tener pipeline de SPRT y baseline robusto.

El objetivo principal no es implementar muchas features, sino asegurar que cada feature mejora o, como minimo, no degrada al motor.

