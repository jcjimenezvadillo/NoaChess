# CHANGELOG

## 2026-07-09 (v2.2.0-dev) — classical evaluation & search overhaul

- eval: tapered (middlegame/endgame) evaluation — every term now carries a MG and an EG value blended by game phase (PeSTO piece values + two-phase piece-square tables). Replaces the old flat single-phase material+PST.
- eval: king safety — enemy attacks on the king zone accumulate weighted "attack units" (plus a pawn-shield / open-file check) through a quadratic danger curve, applied as a middlegame penalty that tapers away in the endgame.
- eval: piece mobility — each minor/major piece scores by the number of squares it can reach, excluding squares covered by enemy pawns; centered so it does not inflate material.
- eval: bishop pair bonus, rooks on open / semi-open files; pawn structure (doubled/isolated/passed) is now tapered (passers endgame-heavy).
- eval: single-pass design — each piece's attack bitboard is computed once and feeds both mobility and king safety (no repeated piece scans).
- search: logarithmic Late Move Reductions (reduction from a log(depth)*log(moveNo) table) replacing the fixed 1-ply reduction.
- search: reverse futility pruning (static null move), futility pruning and late move pruning added to non-PV shallow nodes.
- tests: color-symmetry, starting-position balance, king-safety and bishop-pair sanity checks for the tapered evaluator.

## 2026-07-08 (v2.1.1-dev)

- fix: NNUE bullet time forfeit — Server GC (background collection instead of blocking stop-the-world pauses) and a synchronous JIT warm-up search on NNUE activation (`SetUseNnue`), so tiered-compilation recompiles happen at setoption time instead of stalling mid-game. Verified with a 3-game bullet (1+0) stress test: no time flags, all moves well under budget.
- models: added noa-v2-run3.noannue (250K-game overnight training run, promoted default).
- tools: NoaChess.DataGen — new `--model` flag lets self-play use a trained NNUE model instead of the classical evaluator, for reinforcement-style data generation (run4 onward).
- tools: overnight_training_run4.bat — self-play labeled by the run3 NNUE model, 350K games.

## 2026-07-07 (v2.1.0-dev)

- uci: pondering ("Ponder" option, `go ponder` / `ponderhit` / `stop`) — the engine now thinks on the opponent's time; on ponderhit the warm transposition table makes the timed re-search nearly free. Late bestmoves from searches interrupted by position/ucinewgame are suppressed.
- uci: startup banner (engine name, version, author, .NET/SIMD/core info) printed before the UCI handshake; engine identity centralized in constants (single place to bump versions).
- build: published output is now exactly one file — Release builds of Core/Engine no longer emit debug symbols, so the single-file exe ships without loose .pdb files.
- tools: overnight_training.bat — one-click chained pipeline (datagen 250K games at 15K nodes -> train -> validate -> export) with fail-fast between stages.

## 2026-07-07 (v2.0.0-dev) — NNUE infrastructure

- engine: complete NNUE evaluation runtime (Evaluation/Nnue/) — HalfKP feature schema (40,960 king-relative features per perspective, schema id 1), incremental accumulators with per-ply stack and lazy king-move refresh, integer-quantized inference with scalar reference and SIMD (Vector<T>) backends selected at startup.
- engine: NOANNUE1 versioned binary model format with strict validation (magic, format/schema/architecture ids, dimensions, payload SHA-256); incompatible or corrupt models are rejected with a descriptive info string and the classical evaluator stays active.
- engine: evaluator selector — IIncrementalEvaluator hooks wired into every make/unmake of the search (root, negamax, quiescence, null move); switching evaluators clears the TT.
- uci: UseNNUE and EvalFile options fully functional; model SHA-256 reported on load for reproducibility.
- tools: NoaChess.DataGen — multi-threaded self-play training data generator writing the NOADATA1 binary format (packed positions + side-to-move score and result labels, quiet-position filters, resign adjudication) with a reproducibility manifest.
- tools: Python training pipeline (tools/training/nnue): dataset reader, PyTorch model (architecture 1: FT 128, L1 32), trainer with lambda-blended score/result targets, quantization-aware export to NOANNUE1, and validation utility reporting quantization error.
- tests: 14 new NNUE tests — golden feature indices, deterministic features, make/unmake feature restoration, incremental == full refresh over random games (castling/en passant/promotions), scalar == SIMD, loader round-trip and corruption rejection.
- validated end to end: model trained in PyTorch loads in the engine (SHA match) and plays legal chess with UseNNUE=true.
- PENDING to close v2.0.0: full-scale training run and SPRT vs the classical evaluator (~2070 Elo baseline). The version does not promote until SPRT passes, per the technical roadmap.

## 2026-07-06 (v1.1.1)

- **Measured strength: ~2070 +/- 50 Elo (CCRL-equivalent)** — 800-game gauntlet at 10+0.1 vs 8 engines with known CCRL ratings (TSCP 1600 ... GreKo 2490). Score 67.1% overall; beat Gaia (2400) 17.5% and GreKo (2490) 7% of the games. Zero crashes, zero illegal moves, zero time forfeits across all 800 games. This is the official baseline the NNUE version (v2.0) must beat.

- fix: TimeManager crashed (Math.Clamp with crossed bounds) when the remaining clock was nearly exhausted — an engine crash at zero clock is a guaranteed time forfeit. Likely contributor to the reported flags in won positions.
- evaluation: mop-up term for converting won endgames (drive the enemy king to the edge, bring the own king closer); fixes endless shuffling with K+R+B vs K (now mates in ~28 moves at 200 ms/move) that burned the clock and risked fifty-move draws.
- engine: instant reply when only one legal move exists (saves the whole budget in forced sequences).
- engine: repetition scan skipped when impossible (fewer than 4 reversible half-moves) — it ran at every node and cost O(halfmove clock), worst exactly in long endgames.
- engine: SEE short-circuit — capturing an equal-or-higher-valued victim can never lose material, so the full exchange computation only runs for "upward" captures (QxP, RxN...).
- time safety: MoveOverhead default raised 30 -> 100 ms and an absolute 150 ms reserve is never spent (GUIs add fixed per-move friction beyond the engine's own accounting).

## 2026-07-06 (v1.1.0)

- core: magic bitboards for sliding-piece attacks — O(1) table lookup replaces ray scanning; magics found deterministically at startup (fixed seed), validated by the full Perft suite.
- core: MoveList — reusable fixed-capacity move container; move generation in hot paths (search, perft) allocates nothing.
- core: captures-only move generation mode for quiescence search (quiet moves are never enumerated).
- engine: search uses one preallocated MoveList per ply; MovePicker sorts in place via the list's parallel score array (zero allocations per node).
- engine: EngineProfile (Default/Bullet) — tunable aspiration window, LMR thresholds and time-manager horizon; Bullet prunes sooner, avoids re-searches and spreads the clock over more moves. Selectable via the UCI "Profile" combo option.
- engine (fix): soft time budget was only checked between iterations, so iterations started near the limit ran up to the 4x hard cap, overspending on nearly every move and flagging in long games (reported: time losses vs TSCP/Grizzly from won positions). Now: predictive cut (no new iteration past half the soft budget) and graceful root-level soft stop that reuses the partially searched iteration; partial iterations are not stored in the TT.
- benchmarks: NoaChess.Benchmarks project (BenchmarkDotNet) — move generation, make/unmake, evaluation and search benchmarks with allocation tracking.
- measured: search speed ~580K -> ~1.6M nps (about 2.5x); bullet 1+0 full-game clock simulation completes with no flag.
- uci: publish produces a single self-contained .exe (no DLLs, no .NET runtime required), like native engines.

## 2026-07-06 (v1.0.0)

- engine: PVS (Principal Variation Search) — null-window probes for non-first moves with re-search on improvement.
- engine: Null Move Pruning with zugzwang guard (disabled without non-pawn material, in check, or twice in a row).
- engine: check extension (positions in check searched one ply deeper).
- engine: SEE (Static Exchange Evaluation) via the swap algorithm with x-ray support; used for capture ordering (losing captures last), pruning losing captures in quiescence and skipping clearly losing captures near the horizon.
- engine: repetition detection — a single repetition scores as a draw inside the search; threefold repetition added to GameState.
- engine: pawn structure evaluation (doubled, isolated, passed pawns) cached under a dedicated pawn hash; evaluation split into EvaluationParams / PieceSquareTables / PawnStructureEvaluator for future tuning.
- engine: TimeManager — soft/hard budgets from the clock (soft: stop starting iterations; hard: abort), MoveOverhead margin; node-limited search (`go nodes N`).
- core: MakeNullMove/UnmakeNullMove, pawn-only Zobrist key (incremental), CountRepetitions, HasNonPawnMaterial.
- uci: full basic protocol — asynchronous `go` (search on a background task), `stop`, `isready` answered while searching, `go infinite`, `setoption` with Hash (resizes TT), Threads, MoveOverhead and UseNNUE options declared and parsed.
- fix: move-ordering history scores could overlap the killer/capture bands; history is now clamped below the killer band.
- tests: null move state restoration, repetition counting, pawn hash consistency, zugzwang material detection, SEE exchanges (incl. x-rays), pawn structure terms.

## 2026-07-06 (v0.2.0)

- engine: quiescence search at the horizon (stand pat + MVV-LVA ordered captures and queen promotions), removing the horizon effect.
- engine: transposition table (Zobrist-keyed, depth-preferred replacement, Exact/LowerBound/UpperBound bounds, mate-score ply normalization).
- engine: aspiration windows around the previous iteration score, with full-window re-search on fail.
- engine: move ordering pipeline — TT move, MVV-LVA captures, killer moves, history heuristic (`MovePicker`, `KillerTable`, `HistoryTable`).
- engine: Late Move Reductions for late quiet moves, with null-window probe and full-depth re-search.
- engine: search limits and basic time management; default depth raised from 4 to 6 plies.
- uci: `go movetime N` and `go wtime/btime/winc/binc` (budget = clock/30 + increment/2); `info` lines include `time` and `nps`; `ucinewgame` clears engine state.
- gui: background analysis deepened to 12 plies and now warms the shared transposition table (real pondering); searches are serialized (cancel + await) so engine state is never mutated concurrently.
- tests: transposition table semantics, mate in two, horizon-blunder avoidance, time-limit compliance.

## 2026-07-05 (v0.1.3)

- core: bitboard+mailbox board, full legal move generation, FEN, Zobrist hashing, incremental make/unmake, game-over detection, Perft-validated.
- engine: alpha-beta (negamax) with iterative deepening and progress reporting; classical evaluation (material + piece-square tables).
- uci: console host with uci, isready, ucinewgame, position, go depth, quit.
- gui: WPF (MVVM) click-click play vs the engine, highlights, promotion dialog, board flip, Cburnett SVG pieces, live status bar with evaluation/depth, background analysis on the user's turn.

## 2025-06-04 (v0.1.0-alpha)

- project: repo created and initial documentation setup (README, LICENSE, CONTRIBUTING, CHANGELOG, CODE_OF_CONDUCT).
- infra: established branch workflow (`main`, `develop`, `feature/*`, `release/*`).
- infra: added LICENSE with legal disclaimer and Spanish notice.
- infra: added CHANGELOG.md, CONTRIBUTING.md, CODE_OF_CONDUCT.md (bilingual).
- infra: added .gitignore (Dotnet).
- doc: initial roadmap and project structure defined in README.
