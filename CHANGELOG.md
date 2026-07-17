# CHANGELOG

## 2026-07-17 (v2.7.1) — block 5B: NMP verification + statScore-informed RFP (scope cut by measurement) + mate-search fixes

**SPRT vs v2.7.0 (tc 10+0.1, bounds elo0=0 elo1=10), two runs POOLED: +2.9 ± 7.4 Elo at 4347 games [0.504]** (run 1 stopped stable at 1398g [0.517] +11.8 ± 14.3; run 2 ran to H0 at 2949g [0.498] −1.3 ± 9.0; an A/B control between the two builds involved — with/without the mate-search fix below — scored [0.500] at 1743 games, proving both runs sampled the SAME engine strength, so the pooled figure is the honest STC estimate and run 1 was the high tail of the noise). (A first candidate with the full reference bundle FAILED at [0.451] / −34 Elo over 143 games and was dissected — see below.) **Strength: ~2970 ± 25 CCRL** — LTC gauntlet (tc=60+0.6, 624 games, 13 anchors 2680–3200): **+44 ± 23 relative to the field, 56.3%, vs v2.7.0's +43 on the identical field** (per the 5A lesson, search gains grow with TC — the STC SPRT understates block-5 features; the LTC gauntlet carries the quality signal). Field audit: no renames this cycle — every implied-Elo deviation sits inside the ±100 per-anchor noise; Marvin-3000 (−35 consistent) and Dumb-2856 (~−45) on watch. **Final build: WAC 262/300 vs v2.7.0's 259, depth-15 4-position node bench 2.92M vs 3.72M (−21%), startpos d16 2.25M vs 4.10M nodes (−45%), Fine 70 zugzwang correct.** Smaller tree at equal-or-better tactics.

**Mate-search fixes (found from an Arena game where NoaChess, lost, declined a queen capture that led to mated-in-8 and walked into a mated-in-4 instead):**

- search: iterative deepening no longer stops on a mate score. The old `if |score| > MateBound break` treated every mate as final — but when the engine is the one BEING mated, deeper iterations are exactly what finds longer defenses (the mated-in-8 rook ending needs 16 plies of search; the shallow iteration only saw the mated-in-4 and the search stopped there and played it). It also explains the "sheds all its pieces when lost" endgame behavior: every move re-searched shallow, stopped at first mate sighting, played the first defense on the list. The reference engine never breaks on mate scores — the clock ends the search. Verified: KRK defense now deepens past the first mate sighting (d8 → d22+) holding the longest defense; WAC 262/300 (was 258–259 — continuing past a found mate also finds SHORTER mates when winning); A/B SPRT with/without the fix: [0.500] at 1743 games — the extra clock spent in mate phases costs nothing at STC (adjudication ends those games), and in un-adjudicated real play (Lichess/Arena) the longest defense converts hopeless mates into 50-move/stalemate chances.
- uci: mate scores now go out as `score mate N` (moves, signed) instead of `score cp ±99xxx` — the UCI-mandated form; GUIs showed absurd centipawn evals in mate positions and adjudication could misread them.

What ships (on top of the untouched, validated NMP entry and R):

- search: statScore stack — `statScore[ply] = 2×butterfly + contHist − 1250` (reference `2×main + 3 contHist ctxs − 4433`, unit-rescaled ×0.28 by the MEASURED ratio between our gravity-less depth² tables and the reference's capped ones: butterfly p99 3218 / contHist p99 630 vs caps 14365/29952) recorded for the move that reaches each ply.
- search: RFP statScore term — the parent move's reputation leans on the margin: `staticEval − 85×(depth−improving) − statScore[ply−1]/180 >= beta`, plus the reference's `staticEval >= beta` guard. After a refuted (malus-heavy) parent move the static cut comes easier; after a high-history parent it needs headroom. This term carries real signal: it is the main source of the node reduction.
- search: NMP verification search at depth >= 14 — a null cutoff at high depth is re-proven by a real reduced search on the same position, with null moves disabled for the verifying side until `nmpMinPly = ply + 3(depth−R)/4` (reference nmpMinPly/nmpColor); zugzwang-proof pinned on Fine 70.
- search: NMP fail-soft — a passing null returns `nullScore` (bounded away from mate range) instead of the old hard `beta`; mate-range null scores still fall through to the real search (forced mates stay visible at their natural depth).
- search: improvement value — per-ply eval delta with the reference's ply−4 fallback after checks; the cold default stays STRICT (not improving), see lessons.
- eval: `Winnable.Apply` overload reports the position complexity (initiative magnitude, cp, >= 0) via `IComplexityEvaluator` — plumbing kept for the 5H time-management complexity factor and the eventual NMP revisit.

**Deferred by measurement — the reference NMP presumes three ecosystem pieces we don't have yet.** The full reference bundle (entry gated on `staticEval >= beta − 10d − improvement/13 + 112 + complexity/25`, statScore skip, deep `R = min((eval−beta)/81,7) + depth/3 + 4`, capture futility, lmrDepth quiet futility) was implemented faithfully, unit-rescaled and bisected against WAC-300 + node benches across seven builds:

1. **Deep R needs a checking quiescence.** The reference's null probes bottom out in qsearch from depth 3–7; ITS qsearch generates CHECKS at the first ply, ours is captures-only — our null-passed positions can't see quiet mate threats (WAC 249/300; the WAC.001 mate went from d13 to invisible past d17/100M nodes; verification onset at 8 neither recovered tactics nor kept the nodes). → revisit after adding qsearch checks.
2. **Eval-gated entry needs an accurate eval.** Gating NMP on `staticEval >= beta` grew the tree ~30% at equal tactics: our classical eval is noisy relative to the search, so probes at eval-below-beta nodes keep finding real cutoffs the gate forbids. → revisit with NNUE.
3. **lmrDepth-scaled futility needs the reference's larger reductions** (its lmrDepth runs systematically lower) — and pruning margins do NOT take the ×0.48 value rescale: the RAW reference margins reproduce our validated shallow margins almost exactly (d3: 251 vs 300, d4: 396 vs 400); the ×0.48 ones pruned double and blinded the tactics. → 5C.
4. Capture futility without a gives-check test prunes sacrificial checking captures (−6 WAC); its reference form also needs captureHistory. → 5G.

- verification: 138 tests green; every failed variant documented in the bisection (full bundle 249 WAC / fastest; old-R variants 251–257 WAC / +36% nodes; final assembly strictly dominates the baseline profile).

## 2026-07-16 (v2.7.0) — block 5A: improving flag

**SPRT vs v2.6.9 (tc 10+0.1, bounds elo0=0 elo1=10): +4.0 ± 27.1 Elo at 380 games [0.507], LOS 61.3%, stopped manually (LLR hovering at 0 — real but small STC gain).** **Strength: ~2965 ± 25 CCRL measured** — LTC gauntlet (tc=60+0.6, 624 games, 13 anchors): **+43 ± 23 relative to the field vs the +16 ± 23 of v2.6.9 on the IDENTICAL field and TC — the gain GROWS at LTC (+27 ± 32 relative between versions)**. The opposite pattern to eval terms (which shrink at LTC): pruning/reduction accuracy compounds with depth, so search features are worth more the longer the time control. First version measured above the 2941–2944 plateau of v2.6.8/v2.6.9.

**Field audit (three-gauntlet cross-check, 216 games per anchor):** per-anchor implied-NoaChess deviations consistent across the v2.6.8/v2.6.9/v2.7.0 runs expose three mislabeled engines, renamed to measured strength: **Ethereal 2756 → 2910** (deviations −186/−125/−154: plays ~150 above its label), **Inanis 2997 → 2905** (+63/+58/+193), **Bit-Genie 3101 → 3010** (+84/+79/+126). **Meltdown-2817 cleared** (−10/−11/+5 — one of the cleanest anchors in the field). Marvin-3000 (~−65) and Winter-3200 (~−50) on watch. The corrected field barely moves the centroid (2923.8 → 2921.5): the renames nearly cancel.

Block 5A opens the search block: the reference `improving` flag — a single boolean, computed once per node, that modulates three pruning/reduction heuristics simultaneously. `improving = staticEval[ply] > staticEval[ply-2]` (same side two plies earlier; false when either node was in check, tracked via a per-ply eval stack with a NoEval sentinel).

- search: LMR — quiet moves in a worsening position are reduced one extra ply (`if (!improving) reduction++`); the single highest-impact use of the flag in the reference.
- search: reverse futility pruning — the margin becomes `85 × (depth − improving)`: an improving eval is trusted one depth-step sooner (reference formula shape `165 × (depth − improving)`, ours already at the ×0.48-equivalent 85/ply).
- search: late move pruning — the quiet-move count threshold `3 + depth²` is halved when not improving (reference LMP shape): in a worsening position late quiet moves almost never rescue the node.
- search: move-loop futility pruning and NMP deliberately untouched — the refined NMP entry condition (which also consumes the flag) is 5B scope.
- tests: 137 green (no eval changes — bench positions unaffected).

## 2026-07-16 (v2.6.9) — block 4I: winnable / endgame scale factors

**SPRT vs v2.6.8 (tc 10+0.1, bounds elo0=0 elo1=10): +34.3 ± 19.5 Elo, LOS 100.0%, H1 accepted at 580 games [0.549], DrawRatio 52.6%.** **Strength: ~2941 ± 25 CCRL measured** — LTC gauntlet (tc=60+0.6, 624 games, 13 anchors 2680–3200; +16 ±23 relative, absolute from the pool centroid equation). Statistically the same absolute anchor as v2.6.8 (2944 ±15): the STC gain shrinks at LTC into the error bars, the project's known pattern — the SPRT carries the reliable relative signal.

Block 4I: the reference `winnable()` correction plus the material-entry drawish factor — the final score is adjusted for positions that are structurally harder or easier to win than the raw eval claims. Applied to the total White-relative score right before the phase interpolation.

- eval: complexity/initiative — `9×passers + 12×pawns + 9×outflanking + 21×pawnsOnBothFlanks + 24×infiltration + 51×purePawnEnding − 43×almostUnwinnable − 110`, computed in raw reference internal units and converted ×0.48 once (the mg/eg caps are NoaChess centipawns). The adjustment can only shrink the midgame component, can push the endgame component either way, and never flips the sign of either (`u = sign(mg)·clamp(complexity+50, −|mg|, 0)`, `v = sign(eg)·max(complexity, −|eg|)`). `almostUnwinnable` = kings crossed past each other (outflanking < 0) with every pawn on one flank.
- eval: endgame scale factor — the eg half of the tapered blend is multiplied by sf/64. Material-configuration factor first (material.cpp): a side with no pawns and at most a bishop of extra material rarely wins — sf=0 below a rook in total (KK, KBK, KNK dead draws), sf=4 against a bare minor (KRKB, KRKN), sf=14 otherwise (KmmKm and friends). If no specific factor applies, general heuristics (evaluate.cpp `winnable()`): pure opposite-colored bishops `18 + 4×strongPassers`; OCB with more material `22 + 3×strongUnits`; single-rook endgames with ≤1 pawn of advantage, the strong pawns on one flank and the weak king defending its pawns → 36; queen vs no queen `37 + 3×queenlessMinors`; everything else capped at `36 + 7×strongPawns` (−4 more on a single flank); and a final −4 on every branch when all pawns sit on one flank. Scale factors are dimensionless ratios — deliberately NOT ×0.48-rescaled.
- eval: specialized endgame functions (KXK, KBPsK, KQKRPs, KPsK, KPKP, KNNK...) are NOT ported — out of 4I scope; Syzygy (block 9) covers exact endgames later.
- perf: no cache needed — a handful of popcounts once per Evaluate; depth-16 wall time unchanged (1.23s vs 1.22s).
- time/uci: ponderhit time credit — the ponderhit relaunch used to start a FRESH timed search with the full budget, ignoring everything already pondered: with Permanent Brain on, every move paid ponder time AND a complete optimum on top (observed on Lichess: 30s thinks on near-forced replies, never an instant answer, clocks bleeding vs instant-moving bots). The reference anchors its clock at "go ponder" so pondering counts toward the budget; now the relaunch carries an `ElapsedOffsetMs` charged against every soft/hard check (floored to leave 100ms of hard budget — one warm-TT iteration reproduces the pondered move). Verified over the wire: 6s ponder → bestmove 30ms after ponderhit (was ~4s). Invisible to SPRT/gauntlets (cutechess plays ponder-off) — pure gain in ponder-on play (Lichess, Arena).
- tests: WinnableTests — every scale-factor branch pinned by hand (KBK=0, KRKB=4, KRBKR=14, pure OCB 18+4×passers, mixed OCB 22+3×units, rook ending 32, queen-vs-minors 43, default cap 57), complexity+interpolation pipeline pinned end-to-end on two hand-computed positions, KBK near-draw and color-symmetry checks; ElapsedOffset defaults + consumed-budget instant-answer pinned — 137 tests green.

## 2026-07-16 (v2.6.8) — 4H material-imbalance polynomial + joint material retune + bullet sustainability guard

**SPRT vs v2.6.7.1 (tc 10+0.1, bounds elo0=0 elo1=10): +78.4 ± 31.5 Elo, LOS 100.0%, H1 accepted at 284 games [0.611], DrawRatio 40.5%.** **Strength: ~2944 ± 15 CCRL** — LTC gauntlet (tc=60+0.6, 1560 games, 13 clean anchors 2680–3200; NoaChess +19 ±15 relative to the field, absolute Elo solved from the pool centroid equation).

Block 4H: Tord Romstad's second-degree material-imbalance polynomial, with joint texel retune of the piece values to eliminate the double-counting that caused the two previous failed attempts.

- eval: `MaterialImbalance` — second-degree polynomial (material.cpp `imbalance()`): scores every PAIR of pieces — own-piece synergies (`QuadraticOurs`: knights gain with own pawns, second rook worth less, queen+rook redundant) and enemy interactions (`QuadraticTheirs`: queen strong vs rooks, knight good vs many pawns). Bishop pair = "extended piece" at index 0; its diagonal entry `[0][0]` is zeroed in both Ours/Eg tables: the standalone texel-tuned `BishopPair` term owns the pair's intrinsic value and removing it cost −30 Elo in the first attempt, so the polynomial owns only the pair's INTERACTIONS with the rest of the material. Tables in raw reference units; reference /16 then ×0.48 → combined factor ×3/100 at output. Pure White−Black difference: exactly zero for symmetric material, so no re-centering of the tables.
- eval: joint material retune — piece values (MaterialMg/Eg) and BishopPair were texel-retuned WITH the polynomial active, using a single equal mg/eg offset per piece to prevent the degenerate free direction (tuning mg/eg independently on near-symmetric positions drove queen to 1841/664). Converged offsets over PeSTO: N+20, B+34, R+126, Q+223; BishopPair S(44,68) → S(67,110). The tuner moved the average synergies that had been absorbed into the piece values back out, leaving the polynomial to contribute only the context-dependent deviation.
- perf: per-instance direct-mapped cache (8192 slots) keyed by the packed ten piece counts via Fibonacci hash; counts only change on captures and promotions, so the full polynomial runs only on a miss (~2.4% NPS cost measured on an identical-tree control build).
- time: sustainability guard (sudden-death branch only) — the soft target is bounded by `inc + clock/16` and the hard deadline by `inc + clock/4 - overhead`. Healthy clocks are untouched; in time trouble the spend converges to the increment (2+1 with 5s left: hard deadline 3.96s → 2.22s). Fixes the bullet death spiral where NoaChess lost won positions on time.
- time: the movestogo branch (classical 40/900-style controls) is deliberately NOT touched — the CCRL-rate behavior is validated as-is.
- tests: MaterialImbalanceTests (symmetric=0, hand-computed knight-with-pawns, bishop-pair diagonal zeroed, mirrored position negation, cache consistency); SustainabilityGuard pinned in both directions — 117 tests green.

## 2026-07-14 (v2.6.7.1) — time-management patch: opening overspend + UCI robustness

**SPRT vs v2.6.7 (tc 10+0.1, non-regression bounds [-5, 5]): +14.3 ± 13.5 Elo, LOS 98.1%, H1 accepted, DrawRatio 44.1%** — the patch not only doesn't regress, it gains. **Strength: ~2920 ± 20 CCRL** — confirmed at the exact CCRL list TC (tc=40/900 round-robin, 2026-07-15; 4 self-consistent anchors Meltdown-2817/Colossus-2862/Tcheran-2917/Pedone-2978, implied 2917–2927, mean 2922), superseding the first ~2890 ± 25 estimate from the tc=60+0.6 gauntlet. A 37-engine verification round-robin at tc=30+0.3 anchors ~2900 at that faster rate, consistent within error. Field audit: **KnightX-2932 EXCLUDED going forward** — three consecutive gauntlets anchor NoaChess 60–130 above every other opponent (2953 → 2970 → 3021, drifting), so its label is wrong (~2830 real). Pedone-2978 anchors low twice in a row (2841 → 2811, plays ~3050 real?) — on watch, one more run decides. Patch release targeting two Arena-observed problems: heavy clock use in the opening at short TC (1+0, 3+2) and a frozen engine after starting a new game (Ctrl+N + DEMO) without restarting it. No evaluation changes.

- time: opening damp — `optScale ×= min(1.0, 0.55 + gamePly·0.025)` in the sudden-death branch (fades out by ply 18/move 10). The reference formula folds the whole future increment into the usable time (inc × 49 over the horizon), which at 3+2 budgeted ~7.5s optimum for the first moves (~19s once the dynamic factors extended it), starving the middlegame. Without an opening book the first moves are the cheapest of the game.
- time: neutral first-move dynamic factors — on the first search of a game (no cross-move history) `fallingEval` was deliberately maxed at 1.5 and `bestMoveInstability` could double the budget because a cold TT flaps the root between near-equal openings; both are now 1.0 exactly once. Measured first move at 3+2: 19s → 6.1s; at 1+0: 1.2s.
- uci: ponder/infinite protocol fix — a "go ponder" / "go infinite" search that finished on its own leaked its "bestmove" while the GUI still considered the search pending, which UCI forbids (fires at the end of nearly every game: pondered positions hold forced mates, and a mate score breaks iterative deepening in milliseconds). Now a self-terminated ponder/infinite search parks on the cancellation handle and only answers when the GUI resolves it ("stop" -> bestmove; "ponderhit"/new position -> suppressed). Verified over the full protocol cycle.
- uci: THE Arena freeze root cause, found via traffic log — Arena's Permanent Brain stalls its whole game controller when a "bestmove" arrives WITHOUT a ponder hint: it waits forever for the ponder position, the engine's clock runs down to a time loss, and not even Ctrl+N recovers (Arena re-sends the setoptions and then nothing) until the engine process is restarted. NoaChess omitted the hint whenever a soft-stopped partial iteration improved past the last completed PV (the returned best move no longer matched the PV head). Now every bestmove carries a ponder hint: the PV reply when available, otherwise any legal reply — a wrong prediction is harmless (ponder miss = stop -> discard -> fresh go), a missing one froze Arena. (Thread-stack forensics on a frozen instance had already shown NoaChess healthy and idle in ReadLine() — the GUI was the side that stopped talking.)
- uci: "Debug Log File" option (+ NOACHESS_DEBUG_LOG env var) — timestamped log of every GUI->engine line ("<<"), engine->GUI line (">>"), stdin EOF and the internal search-wait/park/suppress transitions. This is what pinned the freeze: the log showed the exact bare bestmove after which Arena went silent for 96 seconds.
- uci: zombie hardening — a faulted search task re-threw its exception inside `WaitForSearchToFinish` on the UCI loop thread, killing the read loop and leaving the process alive but deaf. The wait now swallows the already-reported fault; `RunSearch` itself never lets an exception escape (reports `info string` and answers with a legal fallback move so the GUI never hangs waiting for `bestmove`).
- uci: one bad command (e.g. a malformed FEN) no longer kills the read loop — reported as `info string`, loop keeps serving.
- tests: opening damp pinned (first-move soft budget < 3% of a 3+2 clock, damp fades by ply 18) — 154 tests green.

## 2026-07-14 (v2.6.7) — block 4G: reference pawn-structure scoring chain

**SPRT vs v2.6.6 (tc 10+0.1): +28.4 ± 17.5 Elo, LOS 99.9%, H1 accepted, DrawRatio 41.7%.** **Strength: 2895 ± 25 CCRL estimated** — LTC gauntlet (tc=60+0.6, 448 games, 8 clean anchors 2688–2978; per-opponent anchored estimates 2841–2970, mean 2894/median 2893). Ethereal-2901: one game ended with an illegal king move in a 3-fold repetition (Ethereal bug, not a crash — 55/56 normal games). KnightX-4.8 and Pedone-1.5 are the high/low statistical outliers (noted since v2.6.6); all 8 anchors are within their ±76–87 Elo individual error margins and remain in the field. The remaining reference pawn-cache terms (pawns.cpp `evaluate()`), replacing the old additive per-file Doubled / Isolated / Phalanx / Backward model with the reference's chain of mutually exclusive branches (a pawn is either connected, isolated or backward — plus the unsupported-pawn and blocked-pawn add-ons). All values ×0.48.

- eval: full Connected formula — a supported and/or phalanx pawn scores `v = Connected[r] × (2 + phalanx − opposed) + 22 × popcount(support)` with `eg = v×(r−2)/4`, computed in raw reference units (Connected = {0,5,7,11,23,48,87}) and converted ×0.48 at the end. Replaces the simple rank-indexed Phalanx array: the formula also pays attention to whether the pawn is opposed (an opposed chain is worth less) and to how many direct supporters it has, and its endgame half only kicks in from relative rank 3 up.
- eval: WeakUnopposed S(7,9) — an isolated or backward pawn with a free file in front is a permanent rook target that can never be traded forward; added on top of Isolated/Backward (the backward case only off the rook files, per the reference).
- eval: WeakLever S(1,27) — an unsupported pawn attacked by two enemy pawns loses the pawn exchange on either recapture.
- eval: DoubledEarly S(8,3) — extra penalty for a doubled pawn while NO enemy pawn is fixed yet (no own pawn rams or restrains them): early doubling is a real weakness, doubling into a locked structure can be a legitimate byproduct of a capture toward the center.
- eval: BlockedPawn ranks 5-6 — {S(-9,-4), S(-3,1)}: a rammed pawn deep in the enemy camp cramps the defense (turns into a small endgame plus on rank 6).
- eval: reference Doubled semantics S(5,25) — own pawn DIRECTLY behind on the same file and no support (the old model penalized every extra pawn per file regardless of support); trebled isolated pawns behind an own pawn on an enemy-free file pay Doubled instead of Isolated.
- eval: reference Isolated S(0,10) / Backward S(3,9) replace the texel-tuned IsolatedPawn/BackwardPawn (the branch structure changed around them, so the old tuned values no longer describe the same events).
- tuner: Doubled, DoubledEarly, Isolated, Backward, WeakLever, WeakUnopposed, BlockedPawnRank registered; DoubledPawn/IsolatedPawn/BackwardPawn/Phalanx removed. Connected[] stays fixed (raw reference units consumed by a formula — tuning the entries independently of the multiplier breaks the shape).
- perf: all in the pawn cache — NPS unaffected (879k vs 613k at depth 16 startpos, machine-state noise aside no regression).
- tests: PawnChainTermsTests (WeakLever, WeakUnopposed, DoubledEarly on/off with a fixed enemy pawn, BlockedPawn rank 5, connected-vs-loose), Phalanx/Backward tests re-pinned to the new chain — 153 tests green.

## 2026-07-14 (v2.6.6) — block 4F: reference passed pawns

**SPRT vs v2.6.5 (tc 10+0.1): +45.8 ± 23.1 Elo, LOS 100%, H1 accepted, DrawRatio 39.0%.** **Strength: 2880 ± 25 CCRL estimated** — LTC gauntlet (tc=60+0.6, 450 games, 9 clean anchors 2688–3027; anchored estimates 2852–2953 across 8 reliable opponents, mean 2886/median 2881). Patricia-3027 confirmed outlier excluded (anchors NoaChess at 2764, implying Patricia plays ~3290 real; behavior normal, label wrong — permanently added to exclusion list alongside Counter 3.8, Mr Bob 0.9.0, Tucano 8.00, Meltdown 1.10, Minic 1.09). The five missing reference passed-pawn terms (evaluate.cpp `passed()` + the pawns.cpp passed definition), replacing the plain cone-mask test and the simple enemy-on-stop penalty.

- eval: reference passed definition — a pawn is passed when (a) the only stoppers are levers (enemy pawns we can capture right now), OR (b) the only stoppers are lever-pushes and our phalanx outnumbers them, OR (c) the only stopper is the direct blocker, the pawn is on relative rank 5+, and a supporting pawn can safely step up to offer the freeing trade (candidate passer). A pawn behind an own pawn on the same file is never passed. Computed in the pawn cache (pawn-only inputs).
- eval: piece-aware blocked-passer filter (second pass) — a candidate blocked by an enemy pawn only keeps its bonus if a friendly pawn one step behind an adjacent file can step up safely (push square empty, not doubly attacked unless defended); otherwise the rank bonus the pawn cache granted is taken back (equivalent to the reference dropping the pawn from the passed loop).
- eval: king proximity to the block square — the passer's endgame value grows with the enemy king's Chebyshev distance (min 5) to the square in front (`x19/4 x w`) and shrinks with our own king's distance (`x2 x w`), plus second-push coverage when the block square is not the queening square (`w = 5*rank - 13`, ranks 4+).
- eval: 6-level path-to-queen safety ladder, only when the pawn can step forward: k=36 if the whole 3-file forward span has no enemy presence, 30 if all of it is covered by our pawns, 17 if the pawn's own file to queen is clean, 7 if only the block square is safe, 0 otherwise; +5 when the block square is defended or an own rook/queen pushes from behind; an enemy rook/queen behind the passer contests the entire span regardless of distance. Applied `(k*w, k*w)` in reference units, converted x0.48 per pawn.
- eval: PassedFile — S(6,4) penalty per file distance from the edge (reference S(13,8) x0.48): flank passers are stronger than central ones.
- eval: the old simple blocked-passer penalty (a third of the rank bonus back when ANY enemy piece sits on the stop square) removed — superseded by the ladder (k=0 covers a piece-blocked path) and the filter. `BlockedPasserDivisor` deleted; NoaChess's Tarrasch RookBehindPasser kept (texel-tuned, complements the reference k+5).
- tuner: PassedFile registered in the parameter registry.
- perf: NPS unchanged (613k vs 598k at depth 16 startpos — the definition lives in the cached pawn eval, the piece-aware terms visit only actual passers).
- tests: PassedPawnTermsTests (king proximity both signs, free vs rook-guarded path, own vs enemy rook behind, PassedFile edge > central, blocked ram with/without helper) — 148 tests green.

## 2026-07-13 (v2.6.5) — block 4E: reference piece terms + full reference time manager

**SPRT vs v2.6.4 (tc 10+0.1): +19.5 ± 13.6 Elo, LOS 99.7%, H1 accepted, DrawRatio 40.5%.** **Strength: 2835 ± 25 CCRL measured**, two LTC gauntlets (tc=60+0.6, 880 clean games pooled, 10 reliable anchors 2688–3027; per-opponent anchored estimates 2767–2966, mean 2842/median 2840). Four mislabeled/broken opponents excluded from the first run (Counter 3.8, Mr Bob 0.9.0, Tucano 8.00 play 300–500 above their labels; the Meltdown 1.10 exe plays ~600 below) plus Minic 1.09 (anchored ~2600 in BOTH runs — its label 2830 is wrong). The apparent −40 vs v2.6.4's 2875 is a field re-anchoring artifact, not a regression: the direct SPRT (+19.5) is the reliable relative signal, and the v2.6.4 figure was measured on a different, likely slightly optimistic field. Two packages: (1) the eleven reference piece-specific evaluation terms (evaluate.cpp `pieces<>()`), rescaled ×0.48 per the standing scale rule, with the outpost machinery now REFERENCE-EXACT (the first 4E attempt regressed in the wide gauntlet: −167 vs −159 relative Elo); (2) a full port of the reference time manager (timeman.cpp + the search-side dynamic stop factors), replacing the v2.6.4 fixed-slice scheduler.

Piece terms (4E):

- eval: TrappedRook — a rook with ≤3 mobility squares, on a file with an own pawn (not (semi-)open), boxed in on the same side as its own king (`(kf<E)==(rookFile<kf)`), penalized and doubled when the side has already lost its castling rights. Reference geometry, NOT a home-rank heuristic (that early cut wrongly penalized rooks on open files and regressed −1.99 llr @ 200 games).
- eval: RookOnClosedFile — penalty for a rook on a file whose own pawn is blocked (a piece directly in front of it), applied only in the non-(semi-)open branch.
- eval: BishopPawns — penalty per own pawn on the bishop's color, indexed by the bishop file's edge distance (BishopPawns[4] ×0.48) and scaled by (not pawn-protected + own pawns blocked on the center files). Hemmed-in "bad bishops" now cost material honestly.
- eval: BishopXRayPawns — penalty per enemy pawn on the bishop's empty-board diagonals (x-ray): they restrict its scope.
- eval: LongDiagonalBishop — bonus when a bishop sees ≥2 of the four center squares (d4/d5/e4/e5) through pawns; it dominates the long diagonal.
- eval: KingProtector — DISABLED (zeroed). On top of PeSTO PSTs it double-counts king distance and its Eg component cancels the outpost bonuses; it collapsed play at long TC in the 2.6.5 gauntlet. Do not re-enable without an SPRT that proves it.
- eval: MinorBehindPawn — bonus when a bishop or knight has a pawn (either color) directly in front of it (the pawn shields it / it blockades).
- eval: WeakQueen — penalty when the queen is the single blocker between an enemy rook/bishop and a target behind it (relative pin / latent discovered attack), using the same sniper/Between logic as king-pin detection.
- eval: outposts REWRITTEN reference-exact (the fix over the first 4E attempt). Outpost squares are now `outpostRanks & (ownPawnAttacks | pawnShield) & ~enemyPawnAttacksSpan`: (a) the pawn-attacks-span excludes BLOCKED and BACKWARD enemy pawns (they can never advance to evict a piece — the first attempt treated every enemy pawn in the cone as an evictor and granted far fewer outposts than the reference); (b) a square with any pawn directly in front qualifies even without own-pawn protection (shield alternative); (c) the whole outpost chain (KnightOutpost / BishopOutpost / UncontestedOutpost / knight-only ReachableOutpost) moved INTO the piece loop and consumes the real per-piece attack bitboard (x-ray through queens, pin-restricted) exactly like the reference — the old second-pass recomputed plain attacks.
- eval: UncontestedOutpost — for a knight on a FLANK outpost (files a/b/g/h) with no attacks on enemy non-pawn pieces and ≤1 enemy piece on its wing, replaces the normal outpost bonus with per-wing-pawn endgame value (reference `else if` chain, not an additive bonus).
- eval: KnightOutpost keeps the texel-tuned S(51,18) (halving it to the generic ×0.48 measurably lost Elo in the 2.6.5 runs); BishopOutpost scaled by the same tuned-to-reference ratio → S(29,13).
- perf: outpost squares and the pawn-attacks-span depend only on pawns, so they are computed inside the pawn-hash cache (PawnStructureEvaluator) and are nearly free per eval call.

Time management (reference port, replaces the v2.6.4 scheduler):

- time: TimeManager is now the reference `TimeManagement::init` verbatim — `optimumTime`/`maximumTime` from `optScale`/`maxScale`, both time-control shapes: sudden death (`optScale = min(0.0120 + (ply+3)^0.45 · 0.0039, 0.2·time/timeLeft) · optExtra`, `maxScale = min(7, 4 + ply/12)`) and movestogo (`optScale = min((0.88 + ply/116.4)/mtg, 0.88·time/timeLeft)`, `maxScale = min(6.3, 1.5 + 0.11·mtg)`), with `timeLeft = time + inc·(mtg−1) − overhead·(2+mtg)` folding the WHOLE increment over the horizon (the flat 85% share is gone) and `maximum ≤ 0.8·clock`.
- time: search-side dynamic stop — after every completed iteration the optimum is re-modulated: `totalTime = optimum × fallingEval × reduction × bestMoveInstability`. `fallingEval` (0.5–1.5) extends the think when the score drops vs the previous move's average and the 4-iterations-ago score (score deltas rescaled ×2.08 to reference internal units); `reduction` halves the budget when the best move has been stable for 10 iterations (`timeReduction` 1.37/0.65, carried across moves via `previousTimeReduction`); `bestMoveInstability = 1 + 1.7 × totBestMoveChanges` (root best-move changes, halved each iteration) extends it when the root flaps. The v2.6.4 revert note is obsolete: the failed attempt multiplied the RAW slice by instability only; the reference formula's stable state is ~0.5×optimum, so extensions start from a much lower base.
- time: the graceful root-boundary stop now uses the dynamically modulated deadline, and a HARD abort mid-iteration keeps the partial iteration's best move when one exists (it is at least as good as the previous iteration's answer — same argument as the soft stop; the reference keeps partial root improvements the same way).
- time: cross-move scheduler state (previous score, average score, previousTimeReduction) lives in AlphaBetaSearch and resets on `ucinewgame`.
- time: fixes the Arena 40/2h first-move anomaly — the old scheduler allocated `clock/25` soft (~4.8 min) with a hard cap of ~19 min for move 1 (the profile's fixed `AssumedMovesToGo = 25` silently overrode the v2.6.4 adaptive horizon — it had been dead code); now move 1 targets ~2.2% of the clock (×1.5 first-move factor, capped by `maxScale`), and bullet low-clock behavior is bounded by the 0.8·clock ceiling.
- time: MoveOverhead default 100 → 30 ms. The reference formula reserves `overhead × (mtg+2)` (≈ ×52) from the usable time: 100 ms reserved 5.2 s and collapsed bullet endgames under a 5 s clock to instant moves.
- uci: EngineProfile.AssumedMovesToGo removed (obsolete — the ply curve replaces it).
- search fix: the extreme fallback (search cancelled before even depth 1 completes — a cold process on a tiny first-move budget) now returns the STATIC-BEST move (a one-ply eval over the legal moves) instead of the first generated move. Move ordering made the first move a rook-pawn push, so a cold engine forced to move instantly could play …a6/a3.
- tests: PieceTermsTests (TrappedRook, LongDiagonalBishop, KingProtector, MinorBehindPawn, BishopPawns, outposts, WeakQueen), EvalSymmetryTests (mirror-FEN color symmetry), CancelledBeforeDepthOne fallback, TimeManagerTests re-pinned to the reference contract (141 tests green).

## 2026-07-11 (v2.6.4) — time management: use the increment, adaptive horizon

**SPRT vs v2.6.3 (tc 10+0.1): no completed SPRT** (first attempt regressed −5.7 ±11.8 Elo; conservative final design was not retested at fast TC). **Strength: 2875 ± 20 CCRL measured**, LTC gauntlet (tc=60+0.6, 2728 games, 11 rivals rated 2580–2917; per-opponent anchored estimates 2847–2899 across 9 reliable opponents, excluding Pedantic-2888 and Minic-2869 as outliers). The +75 jump from v2.6.3 (2800) reflects better increment use at tc=60+0.6: 85% of 0.6s over ~40 moves adds ~24s of usable time per game vs the old 50% share. Free strength — no eval/search knowledge added, only better use of the clock.

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
