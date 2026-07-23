# CHANGELOG

## 2026-07-11 (v2.6.4) — time management: use the increment, adaptive horizon

**SPRT vs v2.6.3 (tc 10+0.1): PENDING** (revised design; see note). Free strength — no eval/search knowledge added, only better use of the clock.

- time: increment spent at 85% instead of 50% (`incrementMs * 85 / 100`). Folding most of the increment into the per-move budget is the main win: v2.6.3 banked half of every increment for no reason and finished games with ~1:50 unused on a 2+6 clock.
- time: adaptive horizon — the assumed remaining-move count follows a ply-scaled curve (`clamp(52 - pow(gamePly+3, 0.45)*2.2, 38, 52)`) instead of a fixed 25. Early in the game the clock is assumed to cover many moves (a small per-move slice on booked/simple openings); by the middlegame the horizon shrinks toward ~38, spending a slightly larger slice where the decisions matter. The divisor is deliberately conservative (~48 opening → ~38 middlegame) so the per-move budget stays a small fraction of the clock — matching what a strong engine's optimum formula produces (~2% of the clock in the opening). The game ply is derived in UciLoop from the board (2*(FullmoveNumber-1) + side).
- tests: TimeManagerTests — soft<hard<clock ordering, 85% increment share, adaptive horizon (middlegame > opening), near-exhausted clock never throws (Min/Max not Clamp), movestogo tightening (79 tests green).
- perf: no evaluation or node-count change.

**Design note — best-move instability extension tried and REVERTED.** The first cut of v2.6.4 also scaled the soft budget by a best-move-instability factor (`1 + 1.7*totBestMoveChanges`) plus a falling-eval factor, and dropped the predictive soft cut. It **regressed −5.7 ±11.8 Elo (H0 accepted, LOS 17%)** and, in bullet, spent up to ~16s on the first move of a 2+1 game: without an eval-complexity metric the instability factor fires hardest in the volatile opening (where any reasonable move is fine), multiplying an already-large base by 3-4x, burning the clock early and rushing the rest of the game. Removed. The instability / falling-eval / complexity time factors belong with the later search block that also ports the complexity signal, not here.

## 2026-07-11 (v2.6.3) — block 4D: shelter/storm + full king safety

**SPRT vs v2.6.2 (tc 10+0.1): +76.9 ± 31.2 Elo, LOS 100%, H1 accepted in 335 games, score 132-97-106 [55.2%]** — the largest single evaluation gain of the project since threats (+103). Well above the +15–30 estimate; king safety was a bigger gap than anticipated.

**Strength: 2800 ± 25 CCRL measured**, LTC gauntlet (tc=60+0.6, 420 games, 8 rivals rated 2780–2917; per-opponent anchored calculation excluding confirmed outlier Leorik-2780). Individual anchored estimates: 2761–2837 across the 7 most reliable opponents, mean ~2807; rounded conservatively to 2800.

- eval: full the reference engine king safety replaces the simple attack-units + pawn-shield scheme. The whole system is computed in RAW internal units (the danger formula, the quadratic transform danger^2/4096 and every table are jointly tuned) and converted to NoaChess centipawns (x0.48) once at the end. No re-centering needed: each side has exactly one king, so constant offsets cancel in the White-minus-Black subtraction.
- eval: shelter/storm (pawns.cpp evaluate_shelter) — ShelterStrength[4][8] per file distance from edge and pawn rank, UnblockedStorm[4][8] for enemy storm pawns, BlockedStorm when our pawn blocks theirs, KingOnFile[ourSemiOpen][theirSemiOpen], computed on the king file and both adjacent files.
- eval: pre-castling shelter (pawns.cpp do_king_safety) — while castling rights remain, the shelter takes the maximum (by MG value) of the current king square and the post-castling squares (g1/c1 relative), so the engine stops fearing phantom attacks on a king that can still castle away.
- eval: endgame king-pawn proximity — shelter minus (0, 16 x Chebyshev distance to the closest own pawn); the king must shepherd its pawns once the danger fades.
- eval: king ring — king attacks of the king square clamped to files b-g / ranks 2-7 plus the square itself, minus squares defended by two own pawns; enemy pawn attacks on the ring seed the attacker count.
- eval: king danger formula (all terms EXCEPT safe/unsafe checks — the v2.4.6 failure was a safe-check mask bug; they remain a possible future sub-block): attackersCount x attackersWeight (weights 76/46/45/14), 183 x weak ring squares, 98 x blockers for the king, 69 x attacks adjacent to the king, king flank attack^2 term, MG mobility difference, -873 when the attacker has no queen, -100 for a knight defender next to the king, shelter feedback, flank defense, +37 bias; penalty (danger^2/4096, danger/16) when danger > 100.
- eval: king flank terms — PawnlessFlank (19,97) when no pawns of either color live on the king's flank; FlankAttacks (8,0) per enemy attack (double attacks counted twice) on the flank inside our camp.
- eval: RookOnKingRing / BishopOnKingRing (stored x0.48: (8,0)/(12,0)) in the piece loop for rooks/bishops aimed at the enemy king ring without directly attacking it.
- perf: shelter cache — direct-mapped 16K-entry table keyed by pawn Zobrist key + king square + own castling rights + color (a strong engine caches king safety in its pawn hash entry and only recomputes when the king moves, ~20% of calls).
- tests: KingSafetyTests (shelter delta, storm delta, pawnless flank sanity, king-ring danger, no-queen discount, pre-castling rights >= no rights, mirror symmetry) — 74 tests green. The color-symmetry fuzz now mirrors castling rights too (the eval reads them since this version).
- bench: fixed-node per-node time +16% vs v2.6.2 in the search bench — partly a tree-shape artifact (the same bench overstated 4C by ~35% in the opposite direction); the SPRT arbitrates the real cost/benefit.

## 2026-07-11 (v2.6.2) — block 4C: non-linear mobility, x-ray attacks, reference mobility area

**SPRT vs v2.6.1 (tc 10+0.1): +6.6 ± 11.5 Elo, LOS 87%, 2000 games (bounds not reached)** — kept: likely positive, no regression risk, and the 4C infrastructure (blockers, pins, x-rays, reference mobility area) is a prerequisite for blocks 4D/4E anyway. Smaller than the 4B jump by nature: it replaces an already SPRT-validated linear mobility term rather than filling a gap.

**Strength: 2780 ± 20 CCRL measured**, confirmed by two independent LTC gauntlets at tc=60+0.6: a 1900-game wide gauntlet (19 engines, 2550–3500 CCRL, ChatGPT-verified ratings) and an 811-game precision gauntlet (10 diverse engines rated 2750–2917; per-opponent anchored calculation over 9 engines after excluding Igel 1.6.0, which underperforms its 2750 label in both gauntlets). The previous ~2870 figure was an extrapolation from the 4B STC SPRT; eval gains shrink at LTC and the old 2580–2788 reference field had miscalibrated labels.

- eval: non-linear mobility — MobilityBonus[pieceType][attackedSquares] lookup tables (rescaled x0.48) replace the linear MobilityStep * (moves - baseline) model. The linear model underpriced the caged end of the curve: going from 2 to 3 knight squares matters far more than going from 7 to 8.
- eval: mobility tables RE-CENTERED — the raw reference tables carry a large positive offset at typical mobility counts (rook +59 eg, queen +63 eg) that the reference engine absorbs in its own tuned piece values; injected as-is it silently inflated NoaChess's texel-tuned material balance (first SPRT run: +2 ± 18 after 870 games, aborted). Each table now has the entry at the old SPRT-validated baseline count (knight 4, bishop 6, rook 7, queen 14) subtracted, keeping the non-linear shape with a ~zero average contribution.
- eval: reference mobility area (Evaluation::initialize) — excludes pawns that are blocked or on the first two relative ranks, the own king and queen, blockers for the own king (pinned pieces) and squares controlled by enemy pawns. Previously: everything not occupied by a friendly piece and not pawn-attacked. Also feeds the KnightOnQueen/SliderOnQueen safe filter in threats, which is now reference-exact.
- eval: x-ray attacks (Evaluation::pieces) — bishops see through queens of both colors; rooks see through queens and own rooks. Batteries now project their real pressure into mobility, threats and king attack accounting.
- eval: pinned-piece attack restriction — a piece that is the single blocker of an enemy slider line to its own king only attacks along the pin line (LineThrough[king][piece] mask). Applies before the attackedBy bookkeeping, so threats and king danger stop counting phantom attacks from pinned pieces.
- infra: LineThrough[64x64] / Between[64x64] static tables and ComputeBlockersForKing (blockers_for_king equivalent: enemy sliders aimed at the king with exactly one piece in between, either color).
- tests: MobilityTests — pinned bishop has zero attacks, pinned rook keeps only the pin line, rook x-ray through own rook but not through a knight, bishop x-ray through queen, mobility-area exclusions (K/Q/low pawns/enemy pawn control/pinned pieces), non-linear curve shape (66 tests green).
- bench: no NPS regression (the blockers computation is offset by the cheaper mobility lookup).

## 2026-07-10 (v2.6.1) — block 4B: the reference engine threat evaluation

**SPRT vs v2.5.0 (tc 10+0.1): +103 ± 35 Elo, llr 2.99, H1 accepted in 243 games, score 109-42-81 [64.4%]** — far above the +25-35 estimate; the largest single evaluation gain of the project (NoaChess had zero threat terms, the biggest gap identified in the reference-engine analysis).

- eval: reference values RESCALED by 100/208 = 0.48 — the reference engine works in internal units where PawnValueEg = 208 equals the 100 cp it reports over UCI, while NoaChess evaluates directly in ~centipawns (PeSTO). The first SPRT run used the raw reference numbers, which made every threat term twice as strong as intended, and trended negative (llr -1.09 after 200 games) before being aborted. Permanent rule: every value ported from the reference evaluation gets the 0.48 factor.
- eval: full the reference engine threat evaluation (evaluate.cpp threats()), all 10 terms. The core concept is "strongly protected" (pawn-defended, or defended twice and not attacked twice) versus "weak" (attacked and not strongly protected) — precisely what the removed v2.4.0 threat attempt lacked, which rewarded attacks on healthily defended pieces and distorted the material judgement.
- eval: ThreatByMinor[victim] / ThreatByRook[victim] — bonuses indexed by the attacked piece type (minors also score against defended pieces; rooks only against weak ones).
- eval: Hanging (weak and undefended, or a non-pawn attacked twice), ThreatByKing (endgame-heavy), WeakQueenProtection (weak piece whose only protector is the queen), RestrictedPiece (enemy moves restricted by our control).
- eval: ThreatBySafePawn (safe pawn attacking a non-pawn) and ThreatByPawnPush (pawn can push next move to a safe square and then attack a non-pawn), with the reference's exact push logic (single + double pushes, safety filters).
- eval: KnightOnQueen / SliderOnQueen — threats on the squares from which the enemy queen can be hit next move, doubled when the enemy queen is the only queen on the board (queen imbalance).
- tests: ThreatsTests — hanging vs defended, safe-pawn threat, minor-on-rook vs minor-on-pawn delta, pawn-push threat probed on the isolated term, doubled-rook battery against the queen, strongly-protected exclusion (59 tests green).
- bench: NPS cost ~3-5%.

## 2026-07-10 (v2.6.0) — block 4A: attackedBy infrastructure

**Evaluation-neutral enabler** (identical node counts on fixed-depth benchmark positions; NPS cost ~2-3%, within the 2-4% budget). Prerequisite for threats (4B), improved mobility (4C) and king safety (4D).

- eval: attackedBy[color][pieceType] + AllPieces union and attackedBy2[color] (squares attacked by two or more friendly units), rebuilt on every evaluate call, mirroring the reference engine's init pass. King and pawn attack sets (including pawn double attacks) seed the tables before the piece loop; each piece then accumulates its attacks into the per-type, the union and the double-attack bitboards.
- eval: x-ray attacks and pinned-piece attack restriction deliberately NOT included yet — they change the attack sets (and therefore mobility) and belong to block 4C, so this change stays strictly evaluation-neutral.
- tests: AttackedByTests — per-type union, pawn double attacks, rook overlap, single-attacker exclusion, king+pawn overlap, no stale state between calls (53 tests green).
- docs: v2.5.0 strength updated to ~2768 CCRL from the 392-game LTC precision gauntlet (67.5% vs 2580-2788 field); old NoaChess_ROADMAP.md removed (superseded by ROADMAP.md).

## 2026-07-10 (v2.5.0) — speed block: staged move generation + lazy legality + PEXT

**SPRT vs v2.4.5 (tc 10+0.1): +101.3 ± 36.8 Elo, LOS 100%, score 91-32-85 [64.2%], H1 accepted in 208 games** — the largest single-version gain since the v2.3.0 search overhaul. Same engine knowledge, dramatically less work per node: depth 15 from the start position now takes 2.9s instead of 4.7s (-39%). **Precision gauntlet (tc=60+0.6, 392 games, 7 rivals rated 2580–2788 CCRL): 67.5% score, +127 Elo over the 2641-average field → ~2768 CCRL-equivalent.**

- search: lazy legality — Negamax generates PSEUDO-legal moves and validates each one at its only make (the scheme quiescence already used). The old up-front legality filter paid a full make/unmake per generated move, and the search loop then paid it again for every move it visited.
- search: staged move generation — the TT move is served first without generating anything (vetted by the new MoveGenerator.IsPseudoLegal), then captures/promotions (sorted, winners first), then quiet moves, with losing captures sinking to the very end. A node that cuts off early never pays for the moves it does not reach. Served order is identical to the previous full-sort ordering.
- movegen: MoveGenerator.IsPseudoLegal(board, move) — exact predicate for "would the generator emit this move", used to vet TT moves before making them (a Zobrist collision could otherwise hand the board a corrupting garbage move). Guarded by an exactness fuzz test (random game paths, 200 random 16-bit probes per position, stale-TT-move scenario) which caught a real bug pre-release: undefined flag encodings (6/7) slipping through as pawn captures.
- movegen: AppendCaptureMoves / AppendQuietMoves staged generators; set-equality with the one-shot generator is fuzz-tested.
- perf: PEXT (BMI2) sliding-piece attack lookup with a CPUID guard — enabled only on Intel and AMD Zen3+ (family 0x19+), where the instruction is fast. On AMD Zen1/Zen+/Zen2 (family 0x17) PEXT is microcoded (~18 cycles) and LOSES to magic lookups, so those CPUs keep the magic path. Decided once at startup via a constant-folded static readonly bool; both paths are cross-validated by tests on any BMI2 machine regardless of which one production uses.
- eval fix: backward pawns no longer ignore a same-rank neighbour — a phalanx member defends the stop square directly, so it is never backward. The old strictly-behind support mask made every phalanx whose front was contested pay the phalanx bonus and the backward penalty at once.
- eval fix: backward is now exclusive of isolated — an isolated pawn trivially has no support and already pays its own (larger) penalty; stacking both double-counted the same weakness.
- note: king safety overhaul (graduated shelter, pawn storm, safe checks) was implemented and REJECTED on this cycle: -77 Elo with pawn-only safe-check masking (phantom checks flooding the quadratic danger curve), statistically zero after fixing the mask and after isolating shelter/storm alone (~900 games total). King-safety units feed a quadratic curve and are not texel-tunable, making each value iteration cost hundreds of games — shelved until after the NNUE block per the pre-agreed decision rule.

## 2026-07-10 (v2.4.5) — phase A eval: tempo + phalanx + backward pawns + retune

**SPRT vs v2.4.0 (tc 10+0.1, 1300 games): +12.2 ± 15.2 Elo, LOS 94.2%, LLR +1.2** — positive trend, SPRT non-conclusive at stop; retune on fresh data confirms the new terms are absorbed cleanly.

- eval: tempo bonus — the side to move receives a flat +14 cp bonus, always positive for the evaluee. Applies after tapering (pure negamax constant, not tunable). Handles initiative asymmetry that the static evaluator cannot otherwise express.
- eval: phalanx (connected pawns) — a pawn with a friendly pawn on the same rank and adjacent file earns a rank-indexed bonus (rank 2: 3/0, rank 5: 44/34, rank 6: 64/54 MG/EG). Computed inside the pawn hash; zero search-speed cost.
- eval: backward pawns — a pawn whose stop square is attacked by an enemy pawn AND has no friendly pawn on adjacent files behind it (no support coming) is penalized (-12, -6). Computed inside the pawn hash; zero search-speed cost.
- tuning: full retune (tools/NoaChess.Tuner) on 2.02M positions from 50K fresh 2.4.5-strength games (seed 20260710); all 3 new scalar/array terms plus 736 PST cells re-optimized jointly. Phalanx and BackwardPawn both moved in the expected direction from hand values.
- tests: Phalanx_BonusIsApplied, Backward_PenaltyIsApplied, Tempo_SideToMoveScoresHigher added (46 tests green). Starting-position balance test updated: symmetric position now correctly evaluates to exactly Tempo (not 0).

## 2026-07-10 (v2.4.0) — evaluation terms + full texel tuning

**SPRT vs v2.3.0 (tc 10+0.1, 2000 games): +13.0 ± 12.6 Elo, LOS 97.8%, score 728-653-619 [51.9%], LLR +1.93** — a real, statistically solid improvement (~2723 CCRL-equivalent estimated; gauntlet pending).

- eval: knight outposts — a knight on relative ranks 4-6, protected by a friendly pawn, on a square no enemy pawn can ever attack, earns a permanent-asset bonus.
- eval: advanced passed-pawn logic — blocked passers (enemy piece on the stop square) give back a third of the rank bonus; connected passers on adjacent files earn an endgame escort bonus; a rook behind its own passer earns the Tarrasch bonus.
- eval: rook on the 7th rank — endgame-heavy bonus per rook on the opponent's second rank (cuts the king off, eats the pawn chain from behind).
- eval: space — per safe central square (files c-f, relative ranks 2-4, not occupied by a friendly pawn, not attacked by enemy pawns).
- eval: threats REMOVED — a bonus for attacking enemy pieces (pawn/minor/rook attack tables) was implemented and rejected after repeated SPRT failures: the term is tempo-blind, rewarding "attacks" the opponent resolves with its very next move, which distorts the material judgement. Hand-tuned SPRT attempts of this block scored between -10 and -2 Elo vs v2.3.0 — hand-picked values are noise at this level; the block's Elo had to come from automated tuning.
- tuning: tools/NoaChess.Tuner — texel tuning by coordinate descent, now covering the full piece-square tables (736 cells) plus all positional terms (776 tunables). Tuned on 2.02M quiet positions sampled from 4.42M records / 50K self-play games generated by the v2.3.0-strength engine at 10K nodes/move (seed 20250709); optimal K = 0.9125, MSE 0.085570 -> 0.083798 over 3 coordinate-descent passes. The old run3/run4 datasets (v2.0-era engine) were discarded as poisoned.
- tuning: mobility is deliberately EXCLUDED from texel tuning — every run, on old and fresh data alike, converges to negative endgame mobility for the minors (spurious correlation: the winning side simplifies and restricts enemy mobility), which plays disastrously. The hand values (SPRT-validated in v2.2.0) stay fixed.
- perf: the new terms initially cost ~13% nps, which silently ate their Elo across six neutral SPRT runs. Fixed: passer bitboards are now cached in the pawn hash (the piece-dependent passer terms no longer rescan every pawn per eval) and the per-call pawn-attack array allocation was removed.
- tests: outpost, rook-on-7th, blocked-passer, rook-behind-passer and connected-passers sanity checks added to the evaluation suite.

## 2026-07-09 (v2.3.0) — search core overhaul

**Measured strength: ~2710 Elo (CCRL-equivalent)** — 231-game LTC precision gauntlet (tc=60+0.6) vs 7 engines rated 2580–2788 CCRL; scored 59.5% (+67 Elo over the ~2642 field average), up from 44.4% for v2.2.0 against the same field (~110 Elo real-play gain). The long-standing Black-side weakness is gone: wins 54 White / 52 Black, losses 32 / 32 — fully symmetric. SPRT vs v2.2.0 had passed H1 earlier: +91 ± 34 Elo, LOS 100%, score 106-43-96 [62.9%] over 245 games at 10+0.1.

- search: counter-move heuristic — the quiet refutation of the opponent's last move is remembered per (piece, destination) and ordered right after the killers.
- search: continuation history — a second history table conditioned on the previous move (prev piece/destination x current piece/destination, ~2.3 MB), blended into quiet-move ordering. Learns "after THIS, THAT reply refutes" — far sharper than the global butterfly history.
- search: history maluses — quiet moves searched before a beta cutoff are punished in both history tables, so failed moves sink in the ordering instead of lingering.
- search: singular extensions — when the TT move's stored score is trustworthy (depth >= 8, entry depth >= depth-3, lower/exact bound), all other moves are verified shallower against a lowered window; if none comes close the TT move is "singular" and searched one ply deeper. Excluded-move searches skip TT cutoffs/stores and prunings.
- search: history-informed LMR — the log-formula reduction is decreased for quiet moves with a good history score (and killers/counter moves) and increased for disliked ones.
- search: progressive aspiration widening — on a fail high/low the window is re-centered on the failing score and doubled instead of jumping straight to a full-width re-search.
- search: Internal Iterative Reductions — nodes at depth >= 4 with no TT move are searched one ply shallower (bad ordering is not worth full depth; a later visit finds the TT move waiting).
- search: ProbCut — at non-PV depth >= 5 nodes, a non-losing capture that beats beta + 150 in a quiescence probe and then in a depth-4 verification search cuts the node immediately.

## 2026-07-09 (v2.2.0) — classical evaluation & search overhaul

**Measured strength: ~2600 Elo (CCRL-equivalent)** — 350-game LTC gauntlet (tc=60+0.6) vs 7 engines rated 2580–2788 CCRL; scored 44.4% overall. SPRT vs v2.1.1 at tc=60+0.6 **passed H1** in 160 games: +429 ± 88 Elo, LOS 100%, score 140–5–15 [92.2%].

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
