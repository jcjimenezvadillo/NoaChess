# NoaChess - Master Development Roadmap

> **Single reference document.** It holds the complete history, the detailed plan through to the end of the project, and the technical decisions we do not want to repeat. Update it whenever a block is closed.
>
> **Golden SPRT rule:** one term per SPRT, TC 10+0.1, 8192 games, elo0=0 elo1=10. Never tune mobility (spurious endgame signal). Watch NPS before and after every evaluation change. One term = one SPRT, always.
>
> **Golden reference-scale rule:** EVERY value copied from a reference engine (evaluate.cpp, pawns.cpp) is multiplied by **100/208 ˜ 0.48**. The reference engine works in internal units where PawnValueEg=208 is the 100 cp it reports over UCI; NoaChess evaluates directly in ~centipawns (PeSTO). Copying the numbers raw doubles the weight of every term (lesson from 4B: llr -1.09 over 200 games with unscaled values).
>
> **Golden provenance rule (added 2026-07-31):** a claim about what went INTO an experiment must be
> machine-checked, never asserted. Blocks 7-8 spent five generations and a shipped version on the
> belief that the datagen was seeded from human openings; every manifest on disk says
> `"openingPlies": "8-9 random legal"`. The pipeline was correct, the book existed, the flag was
> simply never passed - and the resulting conclusion ("self-play is exhausted") went into the
> ROADMAP, the README and the release notes as established fact. **If the manifest does not prove
> it, it did not happen.**
>
> **Golden coupling rule (added 2026-07-31):** "one term = one SPRT" is correct for EVALUATION terms
> and wrong for tightly coupled SEARCH features. A feature worth +2-5 Elo alone is invisible to an
> 8,000-game SPRT, and search features are worth more together than apart because each depends on
> the others being present. Measure coupled search work as a bundle; ablate only after it passes.

---

> **CORRECTION (2026-08-11): the NNUE capacity axis is NOT closed.** v4.5.0 recorded width 512 at -76 and -93 and eight output buckets at -15.2, and concluded "the NNUE capacity axis is closed in both directions". That measurement predates feature factorization by two days, when 85.6% of the transformer quantised to exactly zero - and widening makes precisely that defect worse, because the same signal spread over more neurons gives smaller per-weight magnitudes and small weights are what rounding removes. A 512-wide net under that defect is not more capacity, it is more capacity discarded, which is the shape of the -93. The same broken instrument produced the "self-play is exhausted" conclusion, and re-running the other experiment it invalidated was worth +128 in the field. A second unexplained contradiction points the same way: those eight buckets scored -15.2 here and **+20.1 with LOS 99.8%** in v4.2.0 on a different corpus. **[RESOLVED 2026-08-25: the clean rerun - both arms arch 3, QA=127, 60 epochs each, one variable - measured -49.7 [-76.7, -23.3] H0 over 366 games. The +20.1 is superseded; buckets are closed.]** Width 256 and 512 are back in the training queue, 256 first - the net is measured to be data-starved, so if 256 gains and 512 does not, the ceiling is the corpus rather than the architecture. **[RESOLVED 2026-08-25: both converged and both measured. 512 wins +38.4 at fixed nodes and loses -27.0 at the clock; 256 loses -31.9 at the clock. Capacity is closed; the binding constraint is speed, calibrated at ~65 Elo per NPS doubling at 180+2.]**
>
> **NEXT MAJOR ATTACK: threat features.** The reference no longer evaluates from HalfKA alone; it carries a second feature set of 60,720 dimensions with 128 simultaneously active, encoding which piece attacks which. Our HalfKA schema matches theirs exactly (22,528, 32 active), so this is not a correction but a whole input the network has never had - evaluation content rather than capacity. Cost is weeks, not a night: incremental updates in the C# hot path at four times the active features, the encoder on the Python side, a new file schema, and C#/Python parity verified before anything ships.

## ACTIVE CAMPAIGN - BLOCK 12, NNUE architecture overhaul (branch `4.6.2`)

> **Golden training-pipeline rule (added 2026-08-10):** a training flag is only real if the CALL
> SITE reads it. `--max-records` had a documented default and no effect whatsoever in the streaming
> loader, and a memory note asserting otherwise cost a six-hour control run and framed a whole
> campaign around an axis that was already closed. **Read the call site, not the argument default.**
>
> **Golden baseline rule (added 2026-08-10):** a candidate inherits its dataset list and its export
> architecture from the baseline CHECKPOINT, never from a glob or from memory. One run differed from
> its baseline in three ways at once - epochs, export arch and a silently dropped shard - and
> measured -108.6 Elo for reasons unrelated to what it was testing. Two of the three came from a
> script, which is why `list_checkpoint_data.py` exists.
>
> **Golden validation rule (added 2026-08-10):** validation loss is a sanity check, not a ranking.
> Two nets matched on loss, correlation and quantisation error and were 108 Elo apart. Only an SPRT
> ranks nets.

Everything below v4.0.0 is history. The current work is the **v4.x campaign**: the network is 8×
narrower than reference-class nets and trained on ~100× less data, which is why five consecutive
generations landed flat. Order is **foundation → data → capacity → search**, and no width increase
is attempted before the evaluation profile exists. Full plan in **BLOCK 12**.

**Retired on 2026-07-31:** further self-play generations at 13 M positions, lambda sweeps, NNUE
eval-scale recalibration (measured -61.7), and the competition opening book (deferred to v4.9.0).

---

## Current status

| Version | CCRL Elo | Status |
|---------|----------|--------|
| **5.0.2.1** | **Reproduced on the exact position from a real bot game, and the fix verified against the arm with no tablebases at identical node counts** | **In a LOST position the tablebase root filter left one move, and it was the wrong one.** On `4r3/1P1K4/8/8/6p1/q6k/8/8 w` the white king on d7 stands next to an undefended rook on e8, and `Kxe8` also clears b8 for the pawn; the engine played `Kc6`. Same binary, only the option changed: **limit 7 gives `Kc6` and mate in 6, no tablebases gives `Kxe8` and mate in 8** - mated two moves sooner AND unable to see the alternative, because the filter removed it from the root list. The slack band is `bestRank > MaxDtz / 2 ? (90 - halfmoveClock) / 8 : 0` and that condition means "won", so a lost root keeps exactly ONE move; DTZ being the distance to the next IRREVERSIBLE move, maximising it while losing means **refusing to capture**. Every line of that filter was written to protect a win. A plain loss is now unfiltered, with **`BlessedLoss` as the safe boundary** - there the fifty-move rule really can save the game, so DTZ still rules. Switching the in-search tablebase scores off on the way out is required too, or every losing continuation returns the same flat number. Won-position guarantees unchanged: K+Q+Q vs K mates in 3 without hanging a queen, and a high fifty-move counter still pushes the pawn. **`SyzygyProbeLimit` returns to 7, withdrawing 5.0.3**: its 4.4x probe cost was measured with the tables on this machine's mechanical drive while the bot's sit on a PCIe SSD, so a fix for one machine's disk had been applied to another that never had the problem - the bot's record agrees, with the opponent flagging 45 times against this engine's 2 in ~700 games. Byte-identical to 5.0.2 on the shipped net (2,803,122 nodes over 150 positions). 376 tests. |
| **5.0.3** | **Reported from real games, and the fix is measured on those exact positions.** Still open: even 5-man tables measured -20.2 Elo against none at 10+0.1, faster than the bots play | **The engine was giving away a queen to get into the tablebases sooner.** A tablebase win is scored `TbWin - ply`, so entering the tables SOONER scores HIGHER, and the way to enter sooner is to take pieces off the board - the position is not better for having fewer pieces, only the scoring says so. The ply term is borrowed from mate scoring, where reaching mate sooner genuinely is better; here it measures distance to ENTERING THE TABLE rather than distance to winning, and one sacrifice from a 7-man position buys a proven win worth ~19,987 against a heuristic +1,500. `SyzygyProbeLimit` now defaults to 5: on the two reported positions, limit 7 plays Qxa5+ (queen for a pawn) and keeps Bxf5 in the PV, limit 5 plays Qe3 and Kf2 with a normal +18 evaluation. **Not a storage question** - it would happen the same on the fastest disk - but it removes a **4.4x probe cost** as a side effect, the 6-man set being 160 GB against 0.98 GB for everything up to five men, confirmed as I/O by repeating one search and watching it go 109k, 260k, 443k, 783k nps as the cache warms. **Warning attached to every old measurement:** those tables sit on the mechanical drive and eleven SPRT scripts point at them; with tablebases enabled the engine lost **37 of 95 games ON TIME** against zero without them, an apparent -190 Elo that measures the disk. The tell is time forfeits in the PGN. 370 tests. |
| **5.0.2** | **Byte-identical node counts, 7.7% less time.** Not an SPRT question: the search behaviour is unchanged, so the identity check settles it | **The evaluation was paying for thirty-two divisions and thirty-two horizontal sums it never needed.** Out of the first CPU profile of the SEARCH this project has run, which also inverted a received number: `EvaluateInt16` is 23.8% of all search time and the whole accumulator path about 7.6%, where per-call microbenchmarks had said 73.8% transformer to 26.2% L1 - they measured a cold refresh the search almost never pays for. `hidden[o] / net.QB` compiled to a real integer division because QB is a field the JIT cannot fold, thirty-two per evaluation; the shift that replaces it is exact, differing only on negatives that the following clamp maps to zero regardless. And one horizontal reduction per output row became one per four rows, which also reads the activation vector once instead of four times and lets four accumulator chains issue in parallel. Paired per-position timing over 168 positions: **+7.70%, 95% [+5.56%, +8.71%], 131 of 164 faster, sign test p = 0.00000**, with node counts byte-identical to v5.0.1 (8,522,941 over 60 positions, same best move in every one). Measured twice by instruments that could have disagreed: a second profile attributes 4.5% to the kernel and the stopwatch 4.6%. Looked at and left alone: the rest of the search has no non-constant divisions, `[SkipLocalsInit]` is under one Elo against uninitialised memory in the evaluation, the 4.1% in GC polls is not allocation pressure, and move ordering at 12.5% cannot be sped up without risking the move order itself. 370 tests. |
| **5.1.0** | **THREATS ARE CLOSED AND WIDTH IS OPEN, both by measurement.** Threats win the evaluation outright (**+47.8 Elo [+21.6, +74.5] at fixed nodes, H1**) and lose the game (**-14.9 [-30.1, +0.1] at 180+2 over 723 games, H0**), so they do not ship. Width 512, reburied for weeks on a -30.3 that compared a brand cut at EPOCH 5 OF 60 against a converged one, wins **+38.4 [+18.7, +58.3] at fixed nodes over 637 games, H1** once trained to convergence; its clock SPRT concluded on 2026-08-25: **-27.0 [-48.0, -6.3] at 180+2**, stopped by hand at 97% of the way to H0 with the whole interval negative - the evaluation gain does not survive its speed. With width 256 at -31.9 (converged, at the clock) and clean output buckets at -49.7 (both arms arch 3, 60 epochs), **the capacity axis is now closed by measurement in every direction**. **The byproduct is worth more than either: crossing +47.8 at fixed nodes with -14.9 at the clock at 0.510x NPS calibrates, for the first time in this engine, that a doubling of speed is worth ~65 Elo at 180+2** - a constant previously borrowed and guessed anywhere between 0.58x and 0.70x. It reframes every capacity question as a question about CHEAP capacity, and it prices width 512 at 0.655x against a 0.664x break-even, i.e. exactly on the line | Architecture 5 measured and REJECTED at -32.8 [-56.5, -9.3] over 575 games. Nothing else here is measured, and node counts stay byte-identical for every net without threat features, so no published engine moves | Every change below is either behind a pending SPRT or inert on existing nets. Node counts are byte-identical for every net without threat features, so no published engine moves | **Three buried search blocks rebuilt, MEASURED, and REVERTED - plus the threat feature path built end to end and made incremental.** The search half is a negative result and the branch no longer carries it: the package measured **-22.6 [-41.7, -3.7] H0**, 5E alone **-13.1 H0**, 5G multi-level **-40.9 H0**, and the bonus-shape fix ALONE **-5.8**, neutral - so the levels are what cost, not the shape. Reverting restores v5.0.1 node for node (257616 / 447929 / 472466 at depth 12), the three numbers written down BEFORE the work began. 5E is withdrawn with a named precondition rather than buried: multi-cut prunes 44% of eligible nodes and does not respond to the margin or the gate, which says the TT move is often not uniquely good - a move ordering fact - so it gets re-measured after the killers and counter reform, with the knobs kept on branch bisect-5x. What follows is the record of what was built and why it was worth building even though it lost. An audit of every negative this project had accepted found that **five of six do not survive four questions** - did the arms converge, was the configuration in the regime where the thing is known to work, was there a positive control, and what difference with the reference remains. 5E singular, 5G multi-level history and 5C statScore were all measured against an engine whose move ordering was corrupt (killers returned before history was read, fixed in v4.4.0 for +8.0) and none was re-run afterwards, although the plan already said they would only "have somewhere to bite" after that reform. Reading the source showed the implementations were also wrong: **singular had one of its four outcomes** - the margin was an invented `2 * depth` against the reference's `(59 + 66 * ttPv) * depth / 63`, so the extension almost never fired, and multi-cut, the negative extensions and the double/triple tiers were absent entirely. **Continuation history had one level** against the reference's six written and five read. **statScore could not have been ported faithfully at all**, because its formula reads TWO continuation levels and this engine had no `contHist[1]` until those tables existed. **Four defects found auditing that same day's work before measuring it**, none caught by the compiler or by 361 tests: singular extended one ply too many (newDepth computed after the `depth++`); statScore priced every capture by its own attacker (`VictimIndex` read after `MakeMove`); a double integer division left the far continuation levels learning **nothing** at shallow depth; and the same product overflowed 32 bits at the ply cap, which would have taught the tables the opposite of what happened. Continuation history also moved to `short`, halving six tables from 14.2 MB to 7.1 MB at byte-identical node counts. **THREAT FEATURES, whole path, nothing measured in Elo.** The schema was settled against the source and the previous encoder was not a half-scale version of it but a differently shaped space: the factor of two is the COLOUR OF THE ATTACKED PIECE, and the `from < to` bit is not a dimension at all but a deduplicator for symmetric relations. 60,720 dimensions, verified by enumeration - 54,092 relations, 54,092 distinct indices, zero collisions, and the 6,628-index gap accounted for exactly. **Arch 4** appends a second transformer summed into the same accumulator, with the header untouched because the row count is a constant of the schema and is asserted rather than read. **The accumulator now updates threats INCREMENTALLY**, which was the reason a threat net could be measured and never shipped: a full refresh costs 3,000-3,700 ns per node against ~1,000 for an entire evaluation. It could not reuse the lazy machinery, because that replays a recorded (piece, from, to) without the board and a threat feature depends on the whole position, including every discovered relation where a slider's ray opened or closed - so the delta is taken across TWO calls, one on each side of the move, with each side using its own affected set because the pre-move board is gone by the second. The affected set is provably complete: a feature changes only if its attacker, its target or its ray meets a changed square, and all three land in attackersTo of one - plus the pawn one square behind, because a pawn stopped by a pawn ahead is the one threat relation that is NOT an attack. **The parity test took two attempts to be worth anything**: the first checked every node and passed a deliberate sabotage, since asking for an evaluation materialises the level and quietly guaranteed every parent was ready before its child was pushed. Checking only at the leaves reproduces the chain the search really builds. NPS is NOT measured yet - laziness was worth +3.1% and this gives some back - so the fixed-nodes SPRT remains the plan and speed is a separate question. **A probe with a positive control** (feature factorization, worth +128 Elo in the field, moves validation loss 14.88%) reports threats gaining **3.96% at width 128, 5.35% at 256 and 1.68% at 512** with six arms converged, while widening ALONE buys 0.11% - input and capacity are coupled, which reinterprets the -30.3 that closed the width axis. Parity checked at every joint: encoder against python-chess, C# indices against Python on 256 cases, model file against loader, and the engine's evaluation against the file's own arithmetic (79/79, 70/70, 91/91). Also here: the WPF title bar follows the Windows theme, which it never did because the DWM paints it light unless a window asks. 370 tests. |
| **5.0.1** | **Not measured in Elo; no SPRT run.** What is established: the flagrant waste is gone, the cut fires rarely, and the total time over 40 real positions is unchanged (+0.2%) | **The engine spent six seconds recapturing a queen it had to recapture.** Found in a real game (lichess E6wD3ggu, move 47, 180+2, 43 s on the clock), not by a test: eleven pieces, the opponent takes the queen, and the only capture on the board takes it back. The easy-move cut required a **decisive score** (`|score| >= 700`), so at -1.6 pawns nothing fired and a forced move was paid for at the full rate. **A move being obvious and a position being won are different things.** The first fix cut the clock on a stable best move and the measurement refuted it: a quiet endgame is stable too, and it changed the move in two middlegames where the engine had been thinking nine seconds and was right to. What separates a forced move is **effort**: on a forced move every alternative is refuted at once, so the root now measures the share of each iteration spent on the move it chooses and the cut requires 90% of it, on top of the move being settled since depth 4 and never changed. The first deployment was still too timid and the same complaint came back from a live game with `share=1.000` in the trace, so a unanimous search (share >= 0.95) now gets the same 12% the decisive-score case does: rook recapture with two legal moves **6.30 s to 0.81 s**, queen recapture 4.08 s to 0.74 s, same move. Over 40 real positions it fires on 13 with the **move unchanged in every one** and 6.8% less time overall; the noise floor is 3 of 40 (the same binary against itself, one position swinging 12.40 s to 1.22 s). Deployed to the Mac bot on 2026-08-13. 354 tests. |
| **5.0.0** | **Not an Elo change: the engine is untouched.** The version constant is shared with the UCI `id name`, so the engine reports 5.0.0 with no search change behind it | **The desktop board stops being a test harness.** Drag and drop with a shadowed piece under the cursor, dots on empty destinations and rings around capturable ones; **premoves** held while the engine thinks and played the instant the turn returns, discarded in silence when the position that arrives makes them illegal; navigation on buttons and on Home/Left/Right/End with a move list in **figurine notation**, where playing from a rewound position replaces the continuation; **engine output as a table** of completed iterations with the principal variation replayed on a copy of the position to be written in real notation; an **evaluation bar** on the win-probability curve, which turns over with the board. A **New Game dialog** for the side and the time control, with **real chess clocks** (increment, losing on time, measured by stopwatch rather than by counting timer ticks) alongside a fixed per-move budget and a fixed depth; a **board editor** that refuses positions that are not positions and says why; **PGN** open, save, copy and paste, where every token is resolved against the position it appears in so an impossible move stops the load instead of skewing it; a **whole-game review** at fixed depth (one search per position, not two) that marks inaccuracies, mistakes and blunders and scores each side's accuracy; **every legal move ranked** in its own subtree at equal depth, with the best few drawn on the board as arrows on a green-to-orange scale (analysis only: in a game they would be telling the player what to play); **Syzygy tablebases** as a remembered setting; and a **decision-point report** that finds the positions where the choice was worth something (the gap between the best move and the second best) and crosses them with the time actually spent - a question no other chess program asks. **Any UCI engine on the machine can be loaded and given a colour**, accepted only if it answers `uciok` and refused with what it did say if it does not, so a wrong file is a sentence rather than a hang; setting both colours to an engine starts a game that plays itself, with the evaluation bar and the search table following whichever engine is thinking, and a pause that reaches it. An engine that answers with a move that is not legal in the position it was given is not asked again. **The space bar plays the move for whoever is to move**, both sides included, so a position set up by hand can be walked one press at a time, and the arrows, Home and End walk the game wherever the focus is; the bare keys had to be taken back from the focused control first, because a button handles space as a click and a list handles the arrows as its own navigation, both before the window's input bindings are consulted. **In analysis mode the evaluation no longer waits on the ranking of every legal move**: that pass ran first and left the panel empty for 2727 ms after every move, and now the analysis leads (71 ms) with the ranking deferred behind it. The engine no longer analyses before the first move. Dark theme throughout, five board colour schemes, coordinates, check as a glow, captured material, automatic NNUE loading, persisted settings. **Seven defects found building it, none of them by reading the code**: two searches could run in the same engine at once - cancelling is itself an await, so requests arriving before the first resumed all saw an idle engine, and driving the board fast crashed it with an index out of range inside the move loop; requests now queue on a gate - engine progress could reach the bound collections on the wrong thread; closing the window while the engine was thinking disposed the gate under a live search and threw on a thread nobody catches, which in analysis mode is almost every close; pausing a two-engine game silenced only the built-in engine; a move refused as illegal did not stop the game the status line said it had stopped; the opening-name struct handed out null strings in its default value, crashing the PGN of any game set up from a position; and the evaluation was queued behind a ranking of every legal move. **`San.Format` joins `San.TryParse` in the Core**, the exact inverse of the parser, 20 tests. 354 tests. **Design review pending: not committed.** |
| **4.7.0** | **SPRT pending on two separate axes**: single-thread (`sprt_463_vs_462.bat`) for the fail-low change, four-thread (`sprt_463_vs_462_smp.bat`) for the vote change, which does not exist below two threads. Expected neutral; the vote defect is rare and catastrophic, not a drip, so Elo is a poor instrument for it | **Two ways the engine could choose a move it had already refuted.** Found by a harness that replays a lost bullet game speaking the ponder protocol **and spending the opponent's real think time** - the part five earlier attempts missed, since the engine charges the ponder's wall clock against the next search and an instantly-resolved ponder leaves a full budget, under which the engine always found the right move (game: 10 ms; every reproduction: 5 s). **The SMP vote treated being MATED as decisive**: `Math.Abs(score) >= MateScore - 1000` is satisfied by a forced loss, and that test short-circuits the vote ahead of any weighting, so one worker announcing its own defeat took the move outright; and once it held the lead the branch below preferred the larger magnitude, which among losses is the **shortest mate**. It passes the 4.6.2 depth filter cleanly, since a lost worker can be deep. Decisive now means winning. **A soft stop inside a fail-low returned an unproved score**: every root move at or below alpha, an upper bound awaiting a re-search, recorded anyway - and that figure is both what `info score` prints and what the vote weighs the worker by, since **the weight is the score**. A large ponder credit makes that stop the normal case. Last completed iteration now survives; fail-high untouched. **The harness still does not reproduce that game**, so neither fix is claimed to cure it. 248 tests. |
| **4.6.2** | **NET: +195.4 +/-57.5 Elo, LOS 100%, SPRT H1 in 102 games** at 10+0.1, same binary both sides, only `EvalFile` differing. **MEASURED 3271 +/-40 CCRL** (75.7% over 600 games at 60+0.6, `Threads=1`, ponder off, no tablebases, twelve-engine field 2862-3281): **+128 over v4.5.0's 3143 +/-31**, per-opponent performances 3157-3350, no single result carrying the figure. Predicted at 35% to pass and it was wrong, the second failed prediction of this shape after output buckets | **The training pipeline was the bottleneck, not the search - and it was measured before it was fixed.** Quantising the shipping net against its own float weights: **85.6% of the feature transformer rounds to exactly zero**, **2,221 of 22,528 features dead**, max quantised weight 52 of 127. Stage-by-stage attribution over 4,000 real positions: **38.77 cp of error from the transformer against 4.9 cp from the head**, on a 231 cp mean evaluation, so the engine played **16.6% away from the net that was trained**. Structural cause: each HalfKAv2_hm feature fires only when the king sits in one of 32 regions, so it is rare, its weight never grows, and quantisation removes it. **Feature factorization** adds 704 virtual (piece, square) features at training time and folds each into its 32 copies at export - **exactly**, since the accumulator's sum is unchanged, so the engine and the file format are untouched. One axis against the baseline: zeros **85.6% → 21.3%**, quantisation error **38.79 → 17.63 cp**, dead features 2,221 → **exactly 1,024**, and those 1,024 are the structurally impossible ones (pawns on ranks 1 and 8), so **no legal feature is ignored**. **Three engine fixes.** The **SMP vote had no depth**: a helper at depth 1 outvoted the main worker at depth 12 by **656 to 14**, which played **two mates in one in four minutes** on Lichess while every `info` line printed the correct move - the months-old "PV does not match bestmove" residue was this, and it decides moves. **Helper threads are now a persistent pool** (24 threads: **34.1 ms → 2.2 ms** per move, curve flat), which is also **what exposed the vote bug**, since parked helpers start instantly and began returning depth-1 results where before they returned nothing. **Ponderhit keeps the deeper answer** when the relaunch is 5+ plies shallower and disagrees. Implemented and verified but **not yet measured in games**: quantization-aware training (trained net and engine agree to **0.47 cp** instead of 31) and per-layer weight decay. **Axis closed by correction**: `--max-records` is inert in streaming, so every net had already seen all 324,297,032 positions - the -108.6 Elo run that seemed to test data volume had moved three variables at once. **v4.6.0 (node prologue, measured 0.0%) and v4.6.1 were never released.** 245 tests. |
| **4.5.0** | **MEASURED 3143 +/-31 CCRL** (62.4% over 580 games at 60+0.6, `Threads=1`, ponder off, no tablebases, vs the field averaging 3042; per-opponent 3062-3210). Against v4.4.0's **3124 +/-30 this is +19 and NOT significant** - the difference carries about +/-43 - so it establishes no regression, with the right sign and size for +10.6% nps. **Performance is now solved properly** by `audit/gauntlet.py` (find R whose expected scores sum to the score made) instead of "average opponent + Elo(score)", which biases on a field spread 2862-3281; the series recomputed identically is **v4.3.1 3116 +/-57, v4.4.0 3124 +/-30, v4.5.0 3143 +/-31**, and the older ~3110/~3114 are the same games under the old formula. Directly measured: **+10.6% nps**, byte-identical node counts in every comparison (34,690,140 nodes position by position, plus a control of the bench against itself). An SPRT was run anyway and confirmed why it is the wrong instrument here: 2,091 games gave **+5.2 Elo, 95% [-5.1, +15.4]**, LLR +0.056 against bounds [0, 10], i.e. **2% of the way to H1** - on the order of 100,000 games to conclude | **A speed release where every step is provable without games.** **The NNUE accumulator was eager** (7.39% self time plus 2.49% in refreshes): both perspectives copied and the feature math done on every `MakeMove` whether or not the position was ever evaluated, made worse by v4.4.0's quiescence transposition work because far more children now return without calling the evaluator. Now records the update and materialises on demand from the nearest computed ancestor; no king squares stored, because along any chainable sequence that perspective's king cannot have moved. **The first cut was SLOWER than eager with byte-identical nodes and every test green** - collapsing the chain onto the top level leaves intermediate levels uncomputed, so an unevaluated parent (in check, where the static eval is skipped) is replayed by every child: 1,087,813 replays against eager's 1,029,978. Materialising each level brings it to 36.7% of eager. **+3.6%**. **Move generation**: delegate removed from the jumper helper, `HasLegalMove` scanning backwards because every search caller reaches it in check (the result is a bool, so order is unobservable), pawn-loop invariants hoisted. **+0.6%**, and honestly reported - the pooled sign test split 148/287, so only the clean pair (+0.57%, p = 0.028) counts. **Multi-dimensional arrays flattened**, the real find: history, killers, piece bitboards, Zobrist keys, counter moves, LMR and correction tables all moved from `T[,]`/`T[,,]` to `T[]`, plus `MoveList` exposing its moves as a raw array. .NET cannot give multi-dimensional arrays the single-dimension fast path. **+6.12%, 397/439 positions faster, p ~ 0.** The profile said `ScoreAndSortQuiets`; the cost was the layout underneath it, not the algorithm. **Method**: `bench_time.py` (two passes of the same binary differ 4.2%, so totals measure the machine; pair by position and distrust a mean whose sign test disagrees) and `clock_curve.py`, which **refuted** the front-loading hypothesis - the field spends 73% of its clock by move 25 against this engine's 78%, and the only real defect is a ~10% late-phase underspend worth 1-2 Elo. `PollGC` and memory zeroing in the profile are a harness artifact (`ucinewgame` clears 64 MB before every bench position). 316 tests |
| **4.4.0** | **MEASURED ~3114 CCRL** (60.1% over 600 games at 60+0.6, `Threads=1`, ponder off, no tablebases for anyone, vs the 12-engine field averaging 3043; per-opponent performances tight at 3052-3215). Against v4.3.1's ~3110 this is **statistically indistinguishable** - the gauntlet resolves about ±20 and the release is worth roughly +7 - so it reads as **position confirmed, no regression**. What IS measured directly: **6.8% fewer nodes, 4.4% higher nps, 10.7% less time to the same depth** over 150 positions from real games. Ordering change alone **+8.0 ±14 over 1125 games** | **BLOCK: killers/counter reform steps 1-2, plus the transposition table in quiescence.** Killers (3,000,000) and the counter move (2,900,000) were returned from `MovePicker.Score` **before history was read**, every history-scored quiet clamped at 2,899,990 - up to three moves per node ordered by a constant. That was the documented ceiling on 5G (four builds) and the butterfly-history LMR term (three builds). They are now bonuses (4096/3072/2048) in the same additive score, non-exclusive. The v2.8.2 SPRT that selected the bands is superseded: at v2.8.2 the butterfly table was numerically broken (gravity truncating to zero, 25% of entries positive), and the rails were rebuilt afterwards. Bonus magnitudes are **not** bench-derived - a paired 150-position bench put 0/4096/8192/16384 within ±2% of each other with every band crossing zero. Removing the bands made killers pay per-move check detection (-3.8% nps), repaid by precomputing check masks per node (exact, node-identical). **Quiescence was not using the TT at all** - no probe, no store - so every node paid a full network evaluation for its stand-pat; profiling put 28.7% of engine time in the forward pass and 31.6% in quiescence. It now reuses and caches the static eval, cuts on a covering bound off the PV, uses a stored score as a better stand-pat floor, and saves its result as a bound (never exact). `TTEntry.Depth` is a byte, so the reference's negative `DEPTH_UNSEARCHED` is carried by `BoundType.None` instead; the shallow ProbCut needed `entry.Depth >= 1` or a captures-only score would have stood in for a verification search at depth 4. **Rejected on measurement**: a second contHist distance (0.9% nps, nothing gained), the reference time constants (neutral at 20+0.2, worse under ponder), a threshold-only SEE (~70k equivalence comparisons passed and node counts still moved 1.8%), width 512 and 8 buckets - **the NNUE capacity axis is closed both ways**. Time management investigated at length and **not broken**: 1.32 s/move against the field's 1.31 (ratio 1.01) over 168 games. The bot's unused clock is **ponder**, measured for the first time at **+150.3 ±79.1**; the binary stability cliff halves the budget on ~half of pondered moves and is filed for step 3. 309 tests |
| **4.3.1** | **MEASURED ~3110 ±45 CCRL** (59.7% over 165 games at 60+0.6, `Threads=1`, ponder off, no tablebases for anyone, vs the 12-engine field averaging 3043; crossover ~3150). One formula across every gauntlet the project has: gen5 3050, gen7 3098, this 3111 - **the +13 is inside the error bar**, so this fixes a band (3070-3155) rather than proving a gain. `plies=16` and tablebases-off-for-all differ from the earlier runs, so read it as a cleaner series starting here. The code changes themselves are a reporting fix plus three correctness fixes, found by scanning 221 games and 9,570 annotated moves, not by tests. **gen8 was measured and rejected on three independent signals**: the SPRT against gen7 stopped at H0 (198 games); with the identical binary and only the net swapped, the avoidable material-loss rate in real bot games tripled (0.23 to 0.72 per 100 moves, p˜0.017) while the score fell from 80.5% to 75.8%; its gauntlet was abandoned once the first two agreed. Its training curve explains why - validation loss never flattened, the largest single drop was the LAST epoch, and `CosineAnnealingLR` is built with `T_max=args.epochs`, so training stopped because the schedule ran out, not because the model converged. Numbering follows the gen6 precedent: the number is consumed by the attempt, not the promotion, so gen8 keeps its row and the retrain (60 epochs on the same corpus) is gen9 | **The engine played one move and reported another on 2% of all moves.** v4.3.0.4 closed one PV/bestmove mismatch from a single game; running the same check over every game since 2026-08-04 showed the rate barely moved - 2.62% before the fix, 1.91% and 2.15% after - so the root-move bound was never the whole story. The mismatches concentrate on the moves worth auditing: `mv60` played `Qxf7+` while the PV said `a4` at `eval -9.50, d20`, and an independent static material scan had already flagged that same move as dropping a queen with `a4` as the best alternative. **Cause**: only completed iterations report progress, but the stop handling in `FindBestMove` replaces `best` with the interrupted iteration's result and breaks without reporting, and the static fallback does the same - so the caller keeps the PV, depth and evaluation of a **different move**. A final report is now emitted whenever the returned move is not the one last announced. **The move selection itself was correct and untouched**: an interrupted iteration only lets a move take the lead after it beats alpha in a full-window re-search, and the loop breaks before an unvalidated scout score can be accepted. The mismatch/blunder correlation is a shared cause (time pressure) rather than causation, so this is **worth no Elo** - it is worth shipping because reading the annotated PGN is how the last two real bugs were caught and the channel was lying exactly where it mattered. Alongside: **the root filed fail-low scores as `Exact`** (two-way bound test where the inner nodes already used three, so an aspiration fail-low was stored as a proved score); **ProbCut keyed continuation history off the destination piece after the move**, which for the queen promotions it admits files under Queen what every other path reads under Pawn; **tablebase verdicts went out as `cp 98872`**, about 988 pawns, into the eval bar and the bot's resign and draw-offer rules. 308 tests |
| **4.3.0.4** | **Not an Elo change; four correctness fixes.** Found by auditing 150 real games and reading the bot's own annotated PGN, not by tests | **The ponder credit was starving the search.** Blunders in the audit shared an impossible signature: **depth 1 with a full clock** (`RZwdbv4z` move 23 `Qh3`, depth 1, 41 s left). The credit clamped against the HARD budget while iterative deepening runs on the SOFT one, so a ponder longer than that budget began the relaunched search past its deadline. At 60+1, pondering 0.5/2/5/10 s gave depth **16/15/11/1**: the longer the opponent thought, the shallower the reply. Capped at half the soft budget; depth now holds at 15-18. **DTZ filter unified**: branching on pawns (4.3.0.3) left K+Q vs K with no gradient. The filter now ranks by DTZ everywhere and keeps a **band** of ranks that narrows as the fifty-move counter climbs, so mating moves survive alongside the DTZ-"optimal" queen sacrifice and the search takes the mate. **Decided endgames capped at 300 ms**: K+Q vs K 3129 → 528 ms, K+N+N vs K 1869 → 503 ms, middlegame untouched. **The root could return a move nothing had validated**: against Ichy-Fish ([lichess.org/ZkymFiQ5](https://lichess.org/ZkymFiQ5)) move 51 dropped a bishop with 1:07 left, and the annotated PGN showed the PV starting `Be4` while the move sent was `g5`. Non-first root moves are scouted with a null window, so their score is a **bound** promoted only by the re-search that fires when they beat alpha; on a fail-low iteration that re-search never runs, yet such a bound could still take the lead while the PV kept the real best move. Fail-low root moves are now excluded. **40+ bench searches never reproduced it** - the annotated PGN was the only evidence. 308 tests. Open: K+N+N vs K still scores +5.58 instead of 0; and **the NNUE is not SIMD-invariant** (-776 / -792 / -799 cp for the same position on AVX2 / no-AVX2 / scalar; **RESUELTO en v4.7.0, verificado 2026-08-12: las tres rutas dan puntuacion, jugada y recuento de nodos identicos**), which a parity test across the three paths should close. That defect does **not** reach the bot: the macOS host was assumed to be Apple Silicon under Rosetta 2 without AVX2, and it is an **Intel** Mac (the arm64 build gives `bad CPU type in executable`), so `go depth 20` at `Threads=1` returns identical nodes, scores and PV on both machines - **12,066,208 nodes, cp 46, the same engine bit for bit** |
| **4.3.0.3** | **Not an Elo change; a correctness one.** Found by a real game, not by a test | **The endgame filter fix, fixed.** v4.3.0.2 ranked root moves by WDL below a halfmove-clock threshold to stop DTZ giving material away, which created the opposite bug. Against zipfile_chess-bot ([lichess.org/HUAC6sVf](https://lichess.org/HUAC6sVf)), K+N+3P vs a bare king: **twenty moves of knight and king going nowhere** from move 123, then the counter crossed the threshold, DTZ engaged, and f6-f7-f8=Q mated in five. WDL keeps every winning move and says nothing about which one advances, so the search had no gradient. **The distinction is pawns, not the clock**: DTZ is exactly right when there is a pawn to push (the push zeroes the counter and advances towards promotion) and only harmful with no pawns of ours and nothing of theirs to take, where the sole way to shorten it is to be captured. Now: own pawns present → DTZ always; no pawns → WDL until the counter bites. First time all three are right together: K+N+pawns mates in 5, K+Q+Q mates in 3, K+P queens. 308 tests |
| **4.3.0.2 follow-up** | **Settled by instrumentation, not by argument** | **The time-manager question, answered the opposite way to the hypothesis.** `NOA_TM_DEBUG=1` prints the target, every factor and the actual spend (off by default). Traced across consecutive middlegame moves at 3+1: **2591-13052 ms against an optimum near 5500, averaging ~107% of target**, with the factors swinging the budget fivefold between a quiet move and a dangerous one. **The scheduler is not under-spending.** The same trace shows the SMP cap earning its keep - `total=11630` against `soft=5815` is exactly the new 2x bound, and the old 1x bound would have discarded that whole extension. The earlier reading came from measuring **moves 1-20 only**, which is the opening damp by design. The real clock loss is configuration: `MoveOverhead` reserved ×52 (600 vs 30 costs a quarter of the bullet budget, optimum 3189 → 2345 ms) plus lichess-bot's own `move_overhead`; both corrected on the bot, no engine change warranted. Tablebase probing now disabled only for a **decisive** root (a drawn one reported +531 for K+N+N vs K, which the draw-offer rule reads). Reverted unmeasured: falling-eval floor 0.5 → 0.65 moved the median spend 5 ms |
| **4.3.0.2** | **+9.8 Elo, 95% CI [-1.9, +21.6], LOS 95.0%** (193-162-739 over 1094 games at 60+1 vs v4.3.0, `Threads=4` because the SMP fix is a no-op on one thread). The interval touches zero and the SPRT was stopped before concluding, so this is not a proven gain - what it rules out is the risk that extending the SMP budget to 2x costs something. 67.6% draws: three of the four fixes act only in rare endgames | **Three defects where a feature made the engine play worse than its absence, all found by watching the bot on Lichess and all reproduced on the bench first.** (1) Flat tablebase scores: the in-search probe returns `TbWin - ply` for every winning continuation, so a queen promotion and a rook promotion scored identically; skipped now when the root is already resolved (`_rootInTb`), tbhits 53 → 1. (2) **The DTZ root filter was giving material away** - DTZ is distance to the next *irreversible* move, so with no pawns and nothing to capture the only way to shorten it is to let the opponent capture one of ours; K+Q+Q vs K is mate in 3 with the tables OFF and a queen sacrifice with them ON. Ranking is now by WDL, DTZ only past halfmove clock 50. (3) `MoveOverhead` is reserved ×52, so 600 in a 30+0 game clamped the budget to **1 ms** and an ultrabullet game was played in seven seconds; capped at half the clock, sane configurations bit-identical. (4) **The Lazy SMP cap made the time manager one-sided** - clamping at the optimum meant every reduction applied and every extension was discarded, so 49 games finished with **73-98% of the clock unused, zero losses on time**; cap now 2× the optimum in one tunable constant. 308 tests |
| **4.3.0** | **+25.7 ±16.4 Elo vs v4.2.0, LOS 99.9%, SPRT H1** (280-212-429, 921 games, 10+0.1, LLR 2.97). Same gen7 net both sides → **the first real Elo of the v4.x campaign, against the ~3080 engine**. STC figure; CCRL number pending the gauntlet | **BLOCK 12 search part 1: six correction histories instead of one.** Pawn (validated v2.8.2) plus minor, major, non-pawn per colour, and a continuation table keyed by the move that reached the position. Three new incremental Zobrist keys in `Board`, add/remove sharing one toggle path. Combined by weighted average, **pawn weight = divisor**, so the pawn-only case is arithmetically identical to v4.2.0 and the five new tables can only add on top, bounded at ±320 cp - asserted by a test, because plain averaging would have shrunk a validated behaviour sixfold and made a failed SPRT unattributable. **The coupled-bundle half was WITHDRAWN** on the repository's own evidence (see BLOCK 12). 308 tests. |
| **4.2.0** | **Output buckets MEASURED +20.1 ±14.0 Elo, LOS 99.8%, SPRT H1** (658-560-474, 1692 games, 10+0.1). Two nets, identical 84.7M corpus, identical width and hyperparameters, only `--out-buckets` differs. **Engine itself unchanged - embedded net still gen7, exactly 193,746 nodes as in 4.0.0/4.1.0.** ⚠️ Contradicted a prior prediction of ~0: the "net is data-starved" finding was treated as a blanket argument against all capacity, but head capacity and input capacity are different constraints | **BLOCK 12 capacity: output buckets + width pricing.** **Arch 3** replicates the head per bucket, selected by piece count - **almost free at runtime** since only one bucket is evaluated per call (head table 16 KB → 128 KB against a 5.5 MB feature transformer). **Verified across languages, not assumed**: bucket formula identical C#↔Python over piece counts 0-40 × 1-16 buckets; a Python-exported arch-3 net evaluates **18/80/62** in the engine and **18/80/62** in the new `verify_export.py` (which reproduces the integer forward pass from the exported FILE) across buckets 7/0/5; gen7 still re-exports **byte-identically**. **`nnuewidth`** prices width without training anything, since cost is a property of shapes not weights: **ft 256 = 1.51×, 512 = 2.64×** - SUB-LINEAR, a better trade than the "wider is counterproductive" assumption of v3.2.0. The first sweep reported 256 as faster than 128 (impossible); estimator now takes min-of-5 and prints its own sanity check. **Buckets are measurable now on existing data** (`Noa-Buckets.ps1` + `sprt_buckets.bat`, two arms differing only in `--out-buckets`). Also fixed: a mistyped subcommand used to launch a 500-game datagen against the default path - it bit me during this work. 228+71 tests. |
| **4.1.0** | **No strength change yet - infrastructure release; the embedded net is still gen7. The +80/+150 target is gated on 2-3 days of datagen, not on code** | **BLOCK 12 data scale: the pipeline for 300-500M positions.** Feature decoding was the wall - **13,816 rec/s meant 6 hours for 300M**, paid again on every mix change; `decode_block` vectorises it to **169,366 rec/s (12×, 29 minutes)**, asserted equal to the scalar reference over 50,000 real records. **Sharded datagen** (`--shard-size`, `--resume`, `--positions`) removes the crash cliff of a multi-day run: each shard is finalized as it fills and is independently trainable, and shards roll between games so the train/val tail cut stays valid. **`corpus` audit** verifies every shard off disk and reports composition by provenance - against the current data it prints, in one line, that all **84,697,234 positions are `8-9 random legal`**. **`Noa-DataScale.ps1`** runs phases 0→4, where **phase 0 tests the campaign's own premise** (6k vs 28k nodes at equal position count, ~4h) before committing days to it, and phase 3 trains at **unchanged width** so the data axis is isolated. `sprt_datascale.bat` + `sprt_datascale_calib.bat`. 293 tests. |
| **4.0.0** | **No strength change (foundation release) - shipped net unchanged, 193,746 nodes on a fixed-depth suite before and after** | **BLOCK 12 foundation: the cost model was measured and it was wrong.** `nnueprofile` reports **L1 dot product 26.2%, feature-transformer row traffic 73.8%** - `NnueInference.cs` had asserted for two versions that the dot product was "THE cost of NNUE eval", which is what justified keeping the net narrow in v3.2.0. **int8 L1 (arch 2)**: eval 1039.9 → 774.7 ns (−25%), NPS 243.7k → 268.7k; QA must drop 255 → 127 because `VPMADDUBSW` saturates its int16 lane (64,770 > 32,767 vs 32,258 < 32,767) - a correctness bound the loader and exporter both enforce. Arch 1 stays supported and re-exports **byte-identically**; the embedded net stays arch 1 because QA=127 changes search (193,746 → 140,008 nodes) and that needs an SPRT. **Accumulator cache**: refresh 4407 → 110 ns (40-52×), 99.6% cached. **Accumulator updates**: 1.9-2.5× faster in isolation after replacing bounds-checked `Vector<short>` loads - but **end-to-end wall time did not move**, because the bottleneck is memory latency on a 5.5 MB table, not instruction count (reported as measured). **Streaming dataset**: the 120M-record RAM ceiling is gone; verified 0 duplicates, 0 val leakage, val 0.052585 vs in-RAM 0.052003. **Provenance gate**: `--require-book` + a mandatory `PROVENANCE:` line, turning the BLOCK 8 failure into a machine check. Plus a pre-existing bug fixed that saved checkpoints with `"model": None` whenever validation was smaller than one batch. 222/222 tests. |
| **3.3.0** | **Mate-stop strength-neutral (+3.3 ±23.1, 523 games, stopped for non-convergence) - shipped for behavior. NNUE scale alignment CUT (−61.7 ±20.7, H0, 666 games)** | **Proven-mate stop + NNUE/classical scale alignment (candidates, on top of 3.2.1).** (1) **Mate-stop**: the loop breaks once a completed iteration proves mate in ≤3 plies for us or that we are mated in ≤2 (the reference's exact rule); it is the NARROW exception to "never break on mate scores" (long mates still deepen). Measured at 5+5: mate-in-1 **1074 ms → 22 ms** single-threaded and **1253 ms → 112 ms** at 30 threads; normal positions identical. Clock-mode only → fixed depth byte-identical. (2) **NNUE scale - MEASURED AND CUT.** The mismatch is real (regression over 6000 real positions: slope 0.783, mean ratio 0.84 = the validate's own 0.840 slope → the net is COMPRESSED), but correcting it **LOSES**: 1250 permille measured **144-261-261 [0.412] over 666 games, −61.7 ±20.7 Elo, LOS 0.0%, H0 accepted**, negative from the very first sample. Reason: the margins were already calibrated in practice to the compressed net (gen3→gen7 were all validated with it), and **inflating the eval makes pruning MORE aggressive** (RFP fires on `staticEval − margin ≥ beta`) → unsound cutoffs. A compressed eval against fixed margins is equivalent to LARGER margins = safer pruning, and the engine prefers that. Knob removed. ⚠️ Calibrating it on artificial material positions previously gave a CONFIDENT BUT FALSE reading of "1.29× inflated" - always measure by regression over REAL positions. |
| **3.2.1** | **No strength change (robustness patch)** | **Bot stability.** Diagnosis: the engine was NOT hanging (47+ games in a row, 9-13/hour); the failure was CPU oversubscription - with `Threads: 30` the process carries ~70 OS threads (ServerGC adds ~1 per core), which starved lichess-bot's Python/network thread and the next game's engine startup (**550 `protocol.initialize()` timeouts against a 60 s limit on 07-29**; 210 dropped connections on 07-30). Fixed in the bot config (`Threads` 30 → 24). Matchmaking time controls left as they were, bullet included: on Lichess a player's clock does NOT start until AFTER their first move, so the 2.5-7 s startup process costs no game time. Two REAL engine defects fixed here: (1) iteration depth had no cap in unlimited searches, so in a repetition position with a warm TT the loop spun uselessly (**depth 22→26 in 30 ms**) burning a core for the opponent's whole think time → now bounded by `MaxPly`; (2) a stalled search froze the command loop **with no trace at all** → it now emits `info string search still stopping after Ns` every second. 281/281 tests. |
| **3.2.0** | **gen7 (NNUE-0.7) ~3080 ±40 CCRL (gauntlet 57.9% over 240 games vs a 2862-3281 field). vs gen5 +3.7 ±10.2 LOS 76.2% (parity); vs classical +28.5 ±13.0 LOS 100%** | **NNUE gen7 + human-opening pipeline.** **CORRECTED 2026-07-31 - the human-opening seeding described here NEVER RAN.** `selfplay-gen7.noadata.manifest.json` records `"openingPlies": "8-9 random legal"`, as do all six earlier datasets; `books/human.fens` existed but `-Book` was never passed to the pipeline. **Therefore the conclusion "human seeding did not raise strength over gen5, so self-play is exhausted" is VOID** - human seeding was never tested and exhaustion was never shown. What survives: the `pgnbook` tooling is real and reusable, gen7 itself is a genuine net trained on genuine (random-opening) data, and the measured **+28.5 ±13.0 vs classical** and **~3080 ±40 CCRL** gauntlet results stand. The real cause of five flat generations is network capacity and data volume - see BLOCK 12. Includes the 3.1.2 time fix. 210/210 tests. |
| **3.1.2** | **SPRT vs v3.1.1 −5.0 ±27.7 LOS 36.3% (283 games, 5+0.05) - neutral; justified by the direct 8.5s→1.68s** | **Time fix + easy-move.** Mid-iteration cap extended to single-thread; easy-move fires at `\|score\|≥700` stable for ≥6 iterations. Measured: 8.5s→1.68s in a decisive endgame at 5+5, equal positions unchanged. 210/210 tests. |
| **3.1.1** | **~3050 CCRL (no Elo change)** | **Cold-start fix (ReadyToRun AOT).** `PublishReadyToRun=true` removes the per-process cold JIT: startup ~25 s → ~7 s (uci+isready cycle ~13 s cold / ~8 s warm). NNUE warmup depth 6 → 1. No search, eval or Elo change - a latency fix for lichess-bot. |
| **3.1.0** | **The embedded gen5 net measures ~3050 ±40 CCRL (gauntlet 51.0% over 240 games vs a 2862-3281 field, single-threaded - the NNUE's 1st CC calibrationRL). Lazy SMP: `Threads=1` byte-identical to 3.0.0 (1,307,077 nodes exactly); node scaling ~7.6× at 8 threads; SMP measures +253 Elo `Threads=30` vs `Threads=1` (20+0.2, LOS 100%, CCRL field gauntlet pending)** | **Lazy SMP discard parallel search.** N workers search the same root sharing ONE transposition table (lock-free by benign roots: 32-bit key verification + pseudo-legality vetting torn reads); search stack, histories, board (`Board.Clone()`) and evaluator are per-thread (NNUE shares the read-only network with its own accumulators; classical gets a fresh instance). The main worker owns time management and reports `info`; at the end the workers vote on the move (score-weighted, with mate handling). ICU `Threads` 1-32. **SMP time fix** (instability averaged over the pool + bounded soft deadline + node-level mid-iteration cap at 1.5× the soft budget): bounds a weighthit clock spike over a warm TT (forced recapture 22-37s → ≤~5s, max 5.2s over 10 runs). Verified: no crashes and legal moves at 1-32 threads with Classical and NNUE. 205/205 tests |
| **3.0.0** | **gen3 +4.5 ±11.4 vs classical (1002-968-680 [0.506] 2650 games, LOS 77.8%, exhausted positive); LTC gauntlet pending** | **HalfKAv2_hm NNUE: the neural evaluation beats the classical one.** Feature transformer schema 2 (InputSize 22528, kings as features, 32 buckets), topology FT 22528→128 ×2 → L1 32 → 1, quantization QA=255/QB=64/OutputScale=400. AVX2 SIMD inference (VPMADDWD, precomputed clipped activation, fused MoveFeature): 312k→446k NPS. Incremental accumulator verified by parity. `NoaChess.DataGen` with WDL mixing + Syzygy/resign/draw adjudication. **Critical bug fixed:** the node-limit hard stop returned Score 0 on the first root move, zeroing 57% of the labels (57.6%→2.1%). Generational self-play: gen2 +1.9 Elo, gen3 +4.5 Elo vs classical. The `noa-gen3` net is embedded in the exe. 276/276 tests |
| **2.8.4** | **+9.2 ±9.1 vs 2.8.3 (3000 games exhausted positive, LOS 97.5%, LLR 1.91); LTC gauntlet pending** | **LMR ttCapture and ttPv adjusters on the fixed-point pipeline.** ttCapture `r += 1079`: when the TT move is a capture, late stills are reduced ~1 ply more. ttPv `r -= 1024 + adjustments`: nodes that were on the previous PV are reduced ~1 ply less. Each screened individually (>93% LOS) and validated together vs v2.8.3 at the SPRT checkpoint. cutNode threaded through Negamax as infrastructure (isolated term CUT at 4026 and 1536). ContinuationHistory bound corrected to 8192. Dead LMR history term removed. 276/276 tests |
| **2.8.3** | **+24.4 ±17.5 vs 2.8.2 (835 games, interval excludes zero); LTC gauntlet +112 ±24 relative** | **History gravity that actually acts.** v2.8.2's "bounded gravity" was **numerically inert**: `score×|bonus|/MaxScore` truncates to 0 with a 2²⁰ bound against values ​​near 7,000. The butterfly bound was sized at 7183 like the reference: mean +71.8 → +13.5, tail 6086 → 3134. LMR pipeline moved to fixed point (1024ths), **verified neutral** by identical node counts. **Cut along the way:** statScore as the LMR history term, re-measured at the real time control, −18 Elo H0 - closing the evidence gap from 5C, which had judged it at 5+0.05. ⚠️ **The SPRT was stopped by hand at LLR 2.61 against a 2.94 bound: it is not a formal H1.** 276/276 tests |
| 2.8.2 | **SPRT H1 +28.0 ±17.2 vs 2.8.1 (834 games); LTC gauntlet +94 ±23, ~3013 ±30 CCRL** | **Validated classical-search audit, without pulling NNUE/SMP forward.** Pawn correction history; ProbCut with depth>=1 verification and promotions exempt from the simplified SEE; fixed aspiration window with fail-low recentering; proven killer/counter bands kept; not unconditional check extension; explicit-only UCI logging. 276/276 tests. ⚠️ This row also credited "continuation history by gravity": **corrected on 2026-07-23, that gravity is numerically inert** (`6086×169/2²⁰` truncates to 0) and the +28.0 came from the rest of the package. |
| 2.8.1 | **SPRT +14.1 ±10.8 H1 over 2175 games · LTC gauntlet +75 ±23 · ~3000 ±25 CCRL** | **Syzygy bugfix + 5G ordering.** Two critical bugs in 2.8.0: (1) the root filter was nullified - `SearchRoot` regenerated all moves AFTER `FilterRootMovesByTablebase`, discarding the filter; (2) DTZ ranking scored irreversible moves before they happened and chose the fastest loss in lost positions. Both fixed. TT safety: `CanReuseTtScore` blocks reuse of TB-band scores when `halfmoveClock > 0`. `SyzygyTable` migrated to `MemoryMappedFile` + `long` offsets (removing the 2 GB limit for 6/7-man files). Ordering: `_captureHistory` integrated into the main search (7×victim + history); partial quiet sort (`-3000×depth`, `MoveRangeToFront` guarantees QUIET before BAD_CAPTURE); `CheckBonus +16 384` for safe direct checks; escape/enter bonus and penalty on minor-piece threats. X-ray mobility: sliders see through their own queen only. ICU: `Ponder` option declared. Portable Syzygy tests via `NOACHESS_SYZYGY_PATH`New tests: `CaptureHistoryTests`, `UciSearchLimitsTests`New tooling: `NoaChess.DataGen`, `NoaChess.Tuner`, Python NNUE pipeline. 268 tests discovered (193 executed with the Syzygy files absent) |
| 2.8.0 | ❌ never validated - two critical bugs fixed in 2.8.1 | **Block 9: Syzygy tablebases.** Exact results at ≤5 men: WDL probing inside the search (gated on the fifty-move counter, verdicts in their own band below the mate range) and **filtering** of root moves by WDL and DTZ. At the root it is a filter and NOT an early return, so v2.7.1's mate announcement is not broken. **A ~1250-line managed port, not P/Invoke**: there is no C compiler here and a DLL would break the single-exe requirement. Verified against an independent prober over **3000 endgames with zero discrepancies**; it caught 3 bugs (symbol-tree base cached per table instead of per PairsData → hung with pawns; off-by-one in the DTZ remap; bare kings with no 2-piece table). Measured: a won KPvK converts in 15 plies vs 25. Cost 1.1% NPS after reordering the guard by selectivity (was 3.5%). 208/208 tests |
| (ProbCut audit) | **ProbCut re-audited and shipped in 2.8.2** | The 2.8.2 revision guarantees normal depth>=1 verification and keeps the measured rework. |
| 2.7.4 | **SPRT -2.1 ±9.9 (H0, 2347 games) · LTC gauntlet +52 ±23 vs 2.7.2's +48 · ~2975 ±25 CCRL UNCHANGED** | **Quiescence rework - a CORRECTNESS release, not a strength one.** In check: no stand-pat, ALL moves, zero pruning, mate detected. Stalemate guard, fail-soft, all 4 promotions. The reference's pruning block ported whole (futility 147, SEE -36, capture history with gravity + 7×victim). **Fixes the root hang** on mate/stalemate, present since forever. -5.7% nodes, time-to-depth -9.0%/-12.6%, **WAC 269/300 record**, 192/192 tests. Both instruments agree on equality: it ships for the bugs, not for Elo |
| (2.7.3) | ❌ CUT WITHOUT RELEASE 2026-07-19 | A double campaign: 5E singular (4 SPRTs, all ≤ equality, −19.7/−12.5 the worst) and 5G multi-level history (4 builds, −33.9→−10.9→[0.496]→−4.2; per-distance tables + gravity + depth≥6 gate all built and tested, the final zero caused by the hard killer/counter bands). Engine remains = 2.7.2. Both blocks closed |
| 2.7.2 | **+37.9 ±15.0 pooled SPRT over 1103 games · +48 ±23 rel LTC · ~2975 ±25 CCRL** | 5D (was 5F) TT redesign: 4×16B clustering per cache line, aging by generation (depth-8×age), cached static eval (+24% nps), sticky ttPv flag with no consumer yet; -19% nodes, WAC 265/300 record |
| (5C) | ❌ CUT 2026-07-18 · **CLOSED 2026-07-23** | The reference LMR suite + statScore. At the real time control: bundle −9.7, rebuild −25.7, statScore machinery −10.8; statScore-in-LMR re-measured at 10+0.1 over the 1024ths pipeline **−18 Elo, 47.4%, H0**. What DID survive and ship: the fixed-point pipeline (v2.8.3) and the ttCapture/ttPv adjusters (v2.8.4). Permanent findings: 1024ths granularity was an unidentified prerequisite (now converted and verified neutral), and the butterfly table is skewed by construction (mean +71.8 vs median −8) |
| 2.7.1 | **+2.9 ± 7.4 pooled SPRT over 4347 games · +44 ± 23 rel LTC · ~2970 ± 25 CCRL** | 5B scope-cut: NMP verification at depth=14 (nmpMinPly), fail-soft NMP, statScore term in the RFP margin + eval=beta guard, statScore stack ×0.28 measured; WAC 262/300 with -21% nodes at d15 / -45% at d16. Plus mate fixes: ID no longer breaks on mate scores (a longer defense when losing), UCI `score mate N` |
| 2.7.0 | +4.0 ±27.1 SPRT (stopped at 380 games, LLR ~0) · **~2965 ±25 CCRL measured** (624 games, **+43 ±23 relative vs 2.6.9's +16 on an identical field/TC - the search gain GROWS at LTC**) | Improving flag (5A): static eval per ply, `eval[ply] > eval[ply-2]` modulates LMR (+1 ply when worsening), RFP (margin ×(depth-improving)) and LMP (threshold halved when worsening) - Previous |
| 2.6.9 | +34.3 ±19.5 SPRT · **~2941 ±25 CCRL measured** (624 games, +16 ±23 relative; same anchor as 2.6.8 - the STC gain shrinks at LTC) | Winnable / endgame scale factors (4I): complexity, almostUnwinnable, OCB, rook endings, queenless, pawnless material factor - Previous |
| 2.6.8 | +78.4 ±31.5 SPRT · **~2944 ±15 CCRL measured** (1560-game gauntlet, 2680-3200 field, +19 ±15 relative) | Material imbalance polynomial (Romstad, bishop-pair diagonal zeroed) + joint retune of piece values ​​WITH the polynomial active (N+20 B+34 R+126 Q+223, BishopPair 67/110) + sustainability bullet guard - Previous |
| 2.6.7.1 | +14.3 ±13.5 SPRT · **~2920 ±20 CCRL** (round-robin at the exact CCRL 40/15 rate, 10-engine field; clean anchors Meltdown-2817, Colossus-2862, Tcheran-2917, Pedone-2978 → 2917-2927; KnightX excluded, Pedone confirmed clean, Velvet-2880 and Ethereal-2901 mislabeled) | Timeman patch (opening damp, neutral first move) + hardened UCI protocol (guaranteed weight hint - Arena freeze resolved) - Previous |
| 2.6.7 | +28.4 ±17.5 SPRT · **2895 ±25 CCRL estimated** | The reference's pawn-structure chain (4G) - Previous |
| 2.6.6 | +45.8 ±23.1 SPRT · **2880 ±25 CCRL measured** | The reference's passed pawns (4F) - Previous |
| 2.6.5 | +19.5 ±13.6 SPRT · **2835 ±25 CCRL measured** | Piece terms (4E, exact outposts) + the reference's full timeman - Previous |
| 2.6.4 | **2875 ± 20 MEASURED** (2728-game precision run, 2580-2917, 11 opponents; anchored estimates 2847-2899 over 9 reliable opponents excl. Pedantic/Minic outliers) | Previous |
| 2.6.3 | **2800 ± 25 MEASURED** (420-game precision run, 2780-2917, 8 opponents excl. Leorik-2780; per-opponent anchored estimates 2761-2837) | Previous |
| 2.6.2 | **2780 ± 20 MEASURED** (2 independent LTC gauntlets: 1900 games over a 2550-3500 field + 811 games precision 2750-2917 per opponent) | Previous |
| 2.5.0 | ~2670 (back-estimated: 2780 - 103 SPRT; the old 392-game gauntlet against a 2580-2788 reference field gave ~2768 but that field was poorly calibrated) | Previous |

---

## ✅ BLOCK 1 - Search (v2.3.0)

**Status: DONE · Branch: `2.3.0` · SPRT: passed (+91 ±34 Elo vs 2.2.0)**

The biggest Elo jump in a single iteration. It turned NoaChess from a basic engine into one with a competitive search.

- **Continuation history + counter-move history** - improves move ordering; every move learns from the context of the two preceding moves.
- **Singular extensions** - detects the "only good move" and extends it a ply; avoids premature cutoffs in critical positions.
- **History-tuned LMR** - the Late reduction Move scales with the accumulated history score rather than a fixed value.
- **Aspiration windows with progressive widening** - narrow initial window; widened geometrically on a fail.
- **Internal Iterative Reductions (IIR)** - reduces depth at nodes with no TT move, to force a quick TT move.
- **ProbCut** - speculative pruning with a reduced search; avoids searching deeply into positions that are already clearly bad.

---

## ✅ BLOCK 2 - Base classical evaluation (v2.4.0)

**Status: DONE · Branch: `2.4.0` · SPRT: passed (+13 Elo vs 2.3.0)**

- Knight outposts (rank 4-6, protected, not attacked by an enemy pawn).
- Advanced passed pawns: blocker, connected passers, rook behind the passer.
- Space (center control weighted by phase).
- **Full texel tuning** - our own coordinate-descent tuner over self-play games. PeSTO values ​​as a starting point, adjusted to the engine. 50K games / 4.42M positions, seed 20250709, K=0.9125.

**Permanent lessons:**
- Mobility is NEVER texel-tuned - it converges to negative endgame values ​​through spurious correlation (the winning side simplifies). Permanently excluded from ParameterRegistry.
- Watch NPS after every new term - the new pieces cost ~13% NPS until the passed-pawn bitboards were cached in the pawn cache.

---

## ✅ BLOCK 2.5 - Fine classical evaluation (v2.4.5)

**Status: DONE · Branch: `2.4.5` · SPRT: passed (+12 Elo vs 2.4.0)**

**Phase A (implemented and tuned):**
- **Tempo** - bonus for being on move.
- **Phalanx / connected pawns** - bonus for a friendly pawn on the same rank and an adjacent file; indexed by relative rank.
- **Backward pawns** - penalty when the stop square is attacked by an enemy pawn and there is no friendly pawn level with it or behind it on adjacent files; mutually exclusive with isolated (fixed in 2.5.0).

**Phase B - DISCARDED (v2.4.6):**
King safety. Result: -77 Elo (safe checks with a pawn-only mask flooded the danger curve). Strict mask: 0 Elo. Permanent decision: implement shelter/storm in Block 4D with reference code, no safe checks until re-evaluated.

**Phase C - DISCARDED before attempting:**
TrappedRook + material imbalance. Moved to Blocks 4E and 4H.

---

## ✅ BLOCK 3 - Speed ​​/ Movegen (v2.5.0)

**Status: DONE · Branch: `2.5.0` · SPRT: passed (+101 Elo vs 2.4.5)**

The project's biggest Elo jump to date.

- **Staged move generation:** TT moves first (validated with IsPseudoLegal), then captures, then quiets, then losing captures last.
- **Lazy legality:** pseudo-legal generation + checking when the move is made. Removes the up-front legality check.
- **PEXT / BMI2 with a CPUID guard:** PEXT enabled on Intel and Zen3+ (family = 0x19). Disabled on Zen+ / Zen2 (family 0x17 - Threadripper 2950X) where PEXT is microcoded and slow. Saved via `ComputeUsePext()`.

**Evaluation bugfixes included in 2.5.0 (post-SPRT):**
- Backward: `supportMask` now includes the same rank (a phalanx member is never backward).
- Backward: mutually exclusive with isolated (avoiding a double penalty).

---

## ✅ BLOCK 4 - Reference-level classical evaluation (v2.6.x) - COMPLETE

**Status: COMPLETE (2026-07-16) · 4A (v2.6.0) · 4B threats (v2.6.1, +103) · 4C mobility (v2.6.2, +6.6) · 4D king safety (v2.6.3, +76.9) · 4D.5 timeman (v2.6.4) · 4E piece terms (v2.6.5, +19.5) · 4F passed pawns (v2.6.6, +45.8) · 4G pawn chain (v2.6.7, +28.4) · 4G.1 timeman/UCI (v2.6.7.1, +14.3) · 4H imbalance (v2.6.8, +78.4) · 4I winnable (v2.6.9, +34.3) · Block total: ~2670 → ~2941 CCRL measured**

Goal: systematically replicate a reference classical evaluation. Each sub-block is an independent SPRT. Never mix more than one term per SPRT unless they are prerequisites of each other.

**Estimated total Elo for the block: +120-160 Elo**

---

### ✅ 4A - attackedBy infrastructure (v2.6.0) ⚠️ MANDATORY PREREQ

**Status: DONE · Branch: `2.6.0` · Evaluation-neutral (identical node counts), NPS cost ~2-3%**

**Elo: an enabler, not direct · Effort: Medium**

Prerequisite for threats (4B), king safety (4D) and improved mobility (4C). Nothing else in block 4 can be implemented without it.

- Add an initialization pass at the start of `ClassicalEvaluator.Evaluate()` (equivalent to the reference's `initialize<Us>()`).
- `attackedBy[color][pieceType]` - bitboard of squares attacked by each piece type of each color.
- `attackedBy2[color]` - squares attacked by **two or more** pieces of the same color (double attack). Essential for threats and king security.
- These bitboards are reused by every term that follows; the cost amortises quickly.
- Benchmark NPS before/after: ~2-4% expected cost. If it is higher, investigate.

---

### ✅ 4B - Threats (v2.6.1)

**Status: DONE · Branch: `2.6.1` · SPRT: passed (+103 ± 35 Elo vs 2.5.0, llr 2.99, H1 at 243 games, 64.4%)**

Far above the estimate (+25-35): the project's biggest evaluation jump. Critical lesson folded into the golden rules: reference values ​​are ALWAYS rescaled ×0.48 (the first attempt with raw values ​​trended to llr −1.09).

**Estimated Elo: +25-35 (current: +103) · Effort: Medium**

NoaChess has **zero threat terms**. It is the single biggest evaluation gap.

| Term | Ref value | Description | Priority |
|-----------|---------|-------------|-----------|
| `ThreatBySafePawn` | S(167, 99) | Safe friendly pawn attacking a non-pawn enemy piece | HIGH |
| `Hanging` | S(72, 40) | Weak and undefended enemy piece | HIGH |
| `ThreatByMinor[victim]` | up to S(81,163) | Minor (knight/bishop) attacking a defended or weak piece | HIGH |
| `ThreatByRook[victim]` | up to S(60.39) | Rook attacking a weak piece | MEDIUM |
| `ThreatByKing` | S(24, 87) | King attacking a weak piece in the endgame | MEDIUM |
| `ThreatByPawnPush` | S(48, 39) | Safe pawn advance threatening an enemy piece next move | MEDIUM |
| `RestrictedPiece` | S(6, 7) | Enemy moves restricted by our control | LOW |
| `WeakQueenProtection` | S(14, 0) | Weak piece defended only by the queen | LOW |
| `KnightOnQueen` | S(16, 11) | Knight forking or threatening the queen | LOW |
| `SliderOnQueen` | S(62, 21) | Sliders double-attacking the enemy queen | LOW |

Implement in order of impact: ThreatBySafePawn → Hanging → ThreatByMinor → the rest.

---

### ✅ 4C - Non-linear mobility (v2.6.2) - DONE

**Status: DONE (reference tables ×0.48 re-centered, x-ray, reference mobility area, pin restriction) · SPRT vs 2.6.1: +6.6 ± 11.5 Elo, LOS 87%, 2000 games (bounds not reached; kept anyway: it is prerequisite infrastructure for 4D/4E)**

**Lesson (additional golden rule):** reference tables carry a large positive offset at typical mobility (rook +59 eg, queen +63 eg) which the reference absorbs into its jointly-tuned piece values. Every ported reference table must be RE-CENTRED (subtract the entry at the typical count) so it does not inflate NoaChess's texel-tuned material balance.

**Estimated Elo: +20-30 · Effort: Medium**

The current linear model (`MobilityStep * (moves - baseline)`) loses Elo because going from 2→3 squares for a knight is worth 5× more than going from 7→8. The reference uses a 32-entry lookup table per piece (MG+EG).

- Replace with `MobilityBonus[pieceType][moveCount]` -an array indexed by square count.
- **Improve the mobility area:** also exclude our own king's and queen's squares, and pieces pinned to the king.
- **X-ray attacks:** bishops "see through" their own queen; rooks see through their own queen and their own rooks.
- **Do NOT tune** these values ​​with the texel tuner - copy them straight from the reference's `MobilityBonus[]`.

---

### ✅ 4D - Shelter / Storm + full King Safety (v2.6.3)

**Status: DONE · Branch: `2.6.3` · SPRT: passed (+76.9 ±31.2 Elo vs 2.6.2, LOS 100%, H1 at 335 games)**

**Estimated Elo: +15-30 · Effort: Medium-High, `pawns.cpp:231-297`**

The earlier attempt (v2.4.6) failed on a safe-checks bug. These components are implemented without safe checks in this version.

**Shelter/storm components (cacheable in the pawn hash):**
- `ShelterStrength[4][8]` - score table by distance from the shelter pawn to the king, for each relative file (0..3) and rank (0..7).
- `UnblockedStorm` - penalty for an unblocked enemy pawn storm, indexed by rank.
- `BlockedStorm` - reduced penalty when the storming pawn is blocked.
- `KingOnFile` - penalty when the king sits on a semi-open or open file with an enemy pawn.
- **Pre-castling evaluation** - compute shelter in the post-casting position and take the maximum with the current one.
- **EG king-pawn proximity** - shelter - 16 × minPawnDist in the endgame (the king must approach his pawns).

**Additional king-safety components (outside the pawn cache):**
- `attackedBy2` in the king zone - `kingAttacksCount` (double attacks on the king zone).
- `Weak squares in king zone` - 183 × popcount of weak squares in the king zone.
- `King flank attack / defense` - 3 terms: flank attack, flank attack², flank defense.
- `Blockers for king` - +98 per blocking (pinned) piece that shields the king.
- `PawnlessFlank` - penalty when the king is on a flank with unfriendly pawns.
- `FlankAttacks` - penalty scaled by attacks on the king's flank.
- `Knight adjacency bonus` - -100 danger units per defending knight near our own king.
- `BishopOnKingRing` - +24 MG per enemy bishop aiming at the king zone.
- `RookOnKingRing` - +16 MG per enemy rook on the same file as the king zone.
- **No-queen discount** - reduces danger by -873 units when the attacker has no queen.

⚠️Benchmark NPS before/after. If it costs >5% NPS, find which components are the expensive ones and isolate them.

---

### ✅ 4D.5 - Adaptive time management (v2.6.4) - DONE AND **SUPERSED BY v2.6.5**

**Status: CLOSED. This is not pending work and it is NOT to be redone.** It carried a - the only one in the document, against 16 ✅ and 2 ❌ - left over from when it was still in the air. Corrected on 2026-07-23.

**It is not marked plain green because not one line of this sub-version survives in the code** (verified 2026-07-23): the 85% increment was replaced by the reference's full folding (`inc * (mtg - 1)`; `TimeManager.cs` itself says *"instead of the flat per-move percentage of earlier versions"*), the per-ply adaptive horizon **never actually executed** and its sabotaging constant `AssumedMovesToGo` no longer exists, and the instability extension was reverted to -5.7 Elo before v2.6.5 brought in the reference's dynamic factors, which do implement it and do measure. Redoing it would mean reinstalling a home-made scheduler on top of a measured reference port: a step backwards.

**Branch: `2.6.4` · No completed SPRT (see note) · LTC gauntlet: 2875 ± 20 CCRL measured (2728 games, 2580-2917 field)**

**Estimated Elo: +0-10 · Effort: Low · Files: `TimeManager.cs` + `AlphaBetaSearch.cs`**

The previous time manager left ~1:50 unused in 2+6 games and used only 50% of the increment. Final changes:

- **85% of the increase** - `inc / 2` → `inc * 85 / 100`. This is the main improvement: the remaining 15% is safety margin. v2.6.3 banked half the increment for no reason.
- **Conservative adaptive horizon** - per-ply divisor `clamp(52 - pow(ply+3, 0.45)*2.2, 38, 52)` (≈48 in the opening → ≈38 in the middlegame) instead of a fixed 25. The per-move budget is a small fraction of the clock (~2%), the same as a strong engine's optimal formula produces.

**REVERTED - best-move instability extension (first attempt).** It multiplied the soft budget by `1 + 1.7*totBestMoveChanges` (+ falling-eval) and removed the predictive cut. **It regressed -5.7 ±11.8 Elo (H0, LOS 17%)** and in bullet spent ~16 s on the 1st move of a 2+1: it multiplied an already-large base (the fixed `clock/horizon` slice) by factors that, in the reference formula, start from a steady state of ~0.5×optimum.

**SUPERSED BY v2.6.5:** the reference's complete manager (timeman.cpp + search.cpp dynamic factors) replaces this scheduler. Post-mortem note: this version's per-ply adaptive horizon NEVER actually executed in real games - `EngineProfile.AssumedMovesToGo = 25` (fixed) silently overrode it in UciLoop, so 2.6.4 always played with `clock/25 + 85% inc` (hence the multi-minute first move at 40/2h: soft ~4.8 min, hard ~19 min). The measured gain (+75 at LTC) came from the 85% increase.

---

### ✅ 4E - Missing piece terms + reference timeman (v2.6.5) - DONE (REVISED)

**Status: DONE AND REVISED · SPRT vs 2.6.4: +19.5 ±13.6 Elo, LOS 99.7%, H1 accepted · 2835 ±25 CCRL measured (2 LTC gauntlets, 880 clean games) · 141 green tests**

**Estimated Elo: +15-20 → measured +19.5.** Note on the absolute anchor: the 2835 lands ~40 below 2.6.4's 2875 despite the positive SPRT - that is a field re-anchoring artefact (5 opponents in gauntlet A had false labels and were excluded: Counter 3.8, Mr Bob 0.9.0, Tucano 8.00, Meltdown 1.10, Minic 1.09). The reliable relative signal is the SPRT.

**Revision (2026-07-13).** The first attempt landed BELOW 2.6.4 in the wide gauntlet (-167 vs -159 relative). Causes found and fixed against evaluate.cpp:

1. **Unfaithful outposts.** The first attempt treated ANY enemy pawn in the cone as an evictor; the reference uses `pawn_attacks_span`, which **excludes blocked and backward enemy pawns** (they can never advance to evict) - the old version granted far fewer outposts. The shield alternative was also missing (`shift<Down>(pawns)`: a square with a pawn in front counts even when not protected by a friendly pawn), and the outpost was computed in a second pass with flat attacks instead of using the real bitboard from the piece loop (x-ray through queens, pin restriction). It is now exact, and outpost squares + span are computed in the pawn cache (pawn-only inputs, ~0 cost).
2. **KingProtector disabled (long-gauntlet evidence):** over PeSTO PSTs it double-counts distance to the king and its endgame component cancels the outposts. Do not re-enable without an SPRT.
3. **KnightOutpost keeps the texel value S(51.18)** (lowering it to the generic ×0.48 lost measured Elo); `BishopOutpost` scaled by the same ratio → S(29.13).
4. **The reference's complete timeman** (pulled forward from 5H, explicitly requested): `TimeManagement::init` verbatim (optimum/maximum, both TC shapes, the whole increment folded into the horizon) + per-iteration dynamic factors (`fallingEval` with deltas ×2.08 into internal units, `timeReduction` 1.37/0.65 carried across moves, `bestMoveInstability`). The formula's steady state is ~0.5×optimum - which is why the 2.6.4 attempt (multiplying the fixed slice) regressed and this one did not. MoveOverhead default 100→30 (the formula reserves it ×52, and at 100 it collapsed bullet endgames into instant moves). `AssumedMovesToGo` removed from the profile.

| Term | Ref value | Piece | Description |
|---------|---------|-------|-------------|
| `TrappedRook` | S(55,13) × (1 + !canCastle) | Rook | Rook with =3 move squares - a serious positional error |
| `RookOnClosedFile` | S(10,5) penalty | Rook | Rook on a file blocked by a friendly pawn |
| `BishopPawns` | -3 to -24 MG per pawn | Bishop | Friendly pawns on the bishop's color × distance to the edge |
| `BishopXRayPawns` | S(4,5) per pawn | Bishop | Enemy pawns on the bishop's diagonal |
| `LongDiagonalBishop` | S(45,0) | Bishop | Bishop seeing both center squares through pawns |
| `KingProtector` | S(7,9) / square | Bishop, Knight | Penalty by distance to our own king |
| `MinorBehindPawn` | S(18,3) | Bishop, Knight | Bonus when a pawn sits directly in front of the minor |
| Bishop outpost | S(31.25) | Bishop | Bishop on an outpost (rank 4-6, protected, no pawn attack) |
| `ReachableOutpost` | S(33,19) | Bishop, Knight | Minor that can reach an outpost next move |
| `UncontestedOutpost` | S(0,10)/pawn | Knight | Knight on a wing with no enemy targets |
| `WeakQueen` | S(57,19) penalty | queen | Queen attacked by sliders or pinned |

**Implementation notes (final):** faithful `BishopPawns` = `BishopPawns[edgeDist] × sameColourPawns × ((notPawnProtected?1:0) + ownBlockedPawnsOnCentralFiles)`. `WeakQueen` reuses the snipers/`Between` logic from king pins (queen = the only blocker between an enemy rook/bishop and a target). `UncontestedOutpost` is knight-only, on a wing (a/b/g/h), endgame, per pawn (of either colour) on that wing. The whole outpost chain lives inside the piece loop (using the real attack bitboard, x-ray + pins) and the outpost squares + `pawnAttacksSpan` are computed in the pawn cache.

---

### ✅ 4F - Improved Passed Pawns (v2.6.6) - DONE

**Status: DONE · SPRT vs 2.6.5: +45.8 ±23.1, LOS 100%, H1 accepted · 2880 ±25 CCRL measured (450g, 8 reliable anchors; Patricia-3027 confirmed outlier ~3290 and excluded) · 148 green tests · NPS unchanged (613k vs 598k)**

**Estimated Elo: +12-18 (actual: +45.8) · Effort: Low-Medium**

Implemented (true to the reference, evaluate.cpp) `passed()` + pawns.cpp):

- **Definition of a reference pass** (in the pawn cache): (a) only lever-stoppers, or (b) only lever-pushes with a phalanx that equals/exceeds them, or (c) a blocked candidate on a relative 5+ rank with a supporting pawn that can safely advance. Replaces the simple cone mask test. Never a pass if there is one's own pawn in front on the same file.
- **Blocked passer filter** (second pass, piece-aware): The blocked candidate only retains the bonus if a friendly pawn can be offered in exchange (empty advance square and not doubly attacked except for self-defense); otherwise, it returns the rank bonus granted by the cache. Replaces the simple enemy-on-stop penalty (BlockedPasserDivisor removed).
- **Proximity of kings to the blockade** - `+prox(Them)·19/4·w - prox(Us)·2·w` (e.g.), more coverage of the second advance if blockSq is not the crowning square. `w = 5·rank - 13`, rows 4+.
- **Safety ladder of the path** - k = 36/30/17/7/0 (+5 if blockSq defended or own rook/queen behind); enemy rook/queen behind the past disputes the entire span. `(k·w, k·w)` in reference units, ×0.48 per pawn at the end.
- **`PassedFile`** - S(6,4) times distance to the edge (S(13,8)×0.48), recorded in the tuner.
- The Tarrasch is preserved `RookBehindPasser` NoaChess's texel-tuned (complements k+5).

---

### ✅ 4G - Additional Pawn Structure (v2.6.7) - DONE

**Status: DONE · SPRT: +28.4 ±17.5 Elo, LOS 99.9%, H1 accepted · 153 green tests · NPS unchanged (all in pawn cache)**

**Measured Elo: +28.4 Elo SPRT · 2895 ±25 CCRL estimated (8 anchors 2841-2970, average 2894)**

Implemented (true to the reference, pawns.cpp) `evaluate()`, all ×0.48). The key: the reference pawn scoring is a chain of MUTUALLY EXCLUSIVE branches (connected / isolated / backward), not a sum of independent terms - the entire chain was ported and the old additive terms (DoubledPawn per column, IsolatedPawn, Phalanx[], BackwardPawn texel-tuned) were removed.

| Term | Ref. Value | Status | Description |
|---------|---------|--------|-------------|
| Complete Connected Formula | `Connected[r]·(2+phalanx−opposed) + 22·support`, e.g. `v·(r−2)/4` | ✅ DONE | In raw reference units, ×0.48 at the end; replaces simple Phalanx[] |
| `WeakUnopposed` | S(15,18) → S(7,9) | ✅ DONE | Over Isolated/Backward with free column in front (backward only outside a/h) |
| `WeakLever` | S(2,57) → S(1,27) | ✅ DONE | Unsupported pawn attacked by two enemy pawns |
| `DoubledEarly` | S(17,7) → S(8,3) | ✅ DONE | Doubled while no enemy pawn is fixed |
| `BlockedPawn` rows 5-6 | {S(−19,−8), S(−7,3)} → {(−9,−4),(−3,1)} | ✅ DONE | Blocked advanced own pawn limits opponent |
| `Doubled` semantics ref | S(11,51) → S(5,25) | ✅ DONE | Own pawn RIGHT behind and unsupported (not the column count) |
| `Isolated` / `Backward` ref | S(1,20)/S(6,19) → S(0,10)/S(3,9) | ✅ DONE | The old texel values ​​described other events (different branches) |

---

### ✅ 4H - Material imbalance (v2.6.8) - DONE

**Status: IMPLEMENTED · SPRT vs v2.6.7.1: +78.4 ±31.5 Elo, LOS 100%, H1 accepted @ 284g [0.611] · Gauntlet LTC: ~2944 ±15 CCRL (13 anchors 2680-3200, 1560g, +19 ±15 relative)**

Two previous attempts failed (SPRT a: −30 @ 440g, b: ±0 @ 250g) because the texel-tuned piece values ​​had absorbed the polynomial's mean synergies. The documented rescue path: retune the texel set of piece values ​​WITH the active polynomial, so that the tuner splits the work between them. Run with a single, equal offset per piece (mg=eg) to avoid the degenerate valley (queen → 1841/664 on the first free attempt). Converged offsets over PeSTO: N+20, B+34, R+126, Q+223; BishopPair S(44,68) → S(67,110).

- Romstad polynomial of the second degree: proper synergies (`QuadraticOurs`) and enemy interactions (`QuadraticTheirs`). Pair of bishops = "extended piece" index 0 with diagonal entry `[0][0]` zeroed (the explicit term `BishopPair` The textex-tuned still owns the pair's value; the polynomial diagonal only adds the pair's interactions with the rest of the material).
- Combined factor ×3/100 (reference /16 × ×0.48 NoaChess). Pure White-Black difference: exactly zero for symmetric material.
- Cache direct-mapped 8192 slots with Fibonacci hash over the ten piece counts; only recalculated on captures and promotions (~2.4% NPS).

### ✅ 4H.1 - Timeman Patch: Bullet Sustainability Guardrail (v2.6.8)

**Status: IMPLEMENTED · No regression confirmed (cut @ 420g [0.509], +6.9 ±23.7 Elo, LOS 71.7%)**

Death spiral in Arena bullet (2+1): fast in the opening, 3-4s/mov as the opening brake fades (bleeding 2-3s net per move against +1s increment), and 1-2s/mov (hard deadline ~4s!) with 5s on the clock - time losses in winning positions. Root cause: the reference formula folds 49 future increments into the usable time and its only brake is the 20% remaining clock cap, which lets the clock decay geometrically instead of stabilizing the expenditure around the increment.

- Guardrail (sudden-death branch only): target ≤ `inc + reloj/16`, hard deadline ≤ `inc + reloj/4 − overhead`Healthy clocks remain intact (thresholds stay above the reference curve until the clock falls); in distress, spending converges to the increase (2+1 with 5s: deadline 3.96s → 2.22s).
- The movestogo branch (classic 40/900 type controls) is NOT touched - the CCRL rhythm behavior is validated as is.

---

### ✅ 4I - Scaling Factors / Winnable (v2.6.9) - DONE

**Status: DONE (2026-07-16) · SPRT vs 2.6.8: +34.3 ±19.5 Elo, LOS 100%, H1 accepted @ 580g [0.549] · Gauntlet LTC: ~2941 ±25 CCRL (624g, +16 ±23 relative - same absolute anchor as 2.6.8, STC gain shrinks to LTC within error) · 135 green tests**

**Estimated Elo: +8-15 (at the end) · Effort: High**

Port Fiel de `winnable()` (evaluate.cpp) + the drawish factor of the material entry (material.cpp), applied to the total White-relative score just before phase interpolation:

- **Complexity metric** - `9·pasados + 12·peones + 9·outflanking + 21·ambosFlancos + 24·infiltración + 51·finalPuroDePeones - 43·almostUnwinnable - 110`In raw reference units and converted ×0.48 once (mg/eg caps are NoaChess centipawns). Can only reduce mg; pushes eg in both directions; never changes the sign of either.
- **`almostUnwinnable`** - crossed kings (outflanking < 0) with all pawns on one flank → −43 complexity.
- **Scale factors** (e.g., the mix is ​​multiplied by sf/64, dimensionless ratios SIN × 0.48): material factor first - strong side without pawns and ≤ one bishop advantage: sf=0 under a rook (KK/KBK/KNK), 4 against a minor side only (KRKB/KRKN), 14 otherwise (KmmKm). General heuristics if not: pure OCB `18+4·pasadosFuertes`OCB with more material `22+3·unidadesFuertes`; single rook endgame with ≤1 pawn advantage, strong pawns on one flank and weak king defending → 36; queen vs no queen `37+3·menoresSinDama`; rest of chapter `36+7·peonesFuertes` (−4 extra on a single flank); −4 final on any branch with all pawns on one flank (the default branch accumulates −8, verified character by character against the source of the reference).
- **Out of scope (documented):** specialized endpoint functions (KXK, KBPsK, KQKRPs, KPsK, KPKP, KNNK) - will be covered by Syzygy (Block 9).
- **Perf:** without cache - some popcounts per Evaluate; wall time depth 16 identical (1.23s vs 1.22s).
- **Tests:** all branches of the scale factor fixed by hand + pipeline complexity/end-to-end interpolation + KBK near-tables + color symmetry.
- **Included in v2.6.9 - time credit in ponderhit:** The relaunch after ponderhit started a new search with a FULL budget, ignoring what had already been weighted (in Lichess: 30s for almost forced responses, never an instant response, the clock bleeding against bots that move instantly). The reference anchors its clock to the "go ponder"; now the relaunch carries `ElapsedOffsetMs` Discounted from each soft/hard check (hard floor of 100ms). Verified by wire: 6s weight → best move 30ms after weight hit (previously ~4s). Invisible to SPRT/gauntlets (cutechess plays without weight) - pure gain in games with weight.

---

## BLOCK 5 - Reference level search (v2.7.x)

**Status: CLOSED in v2.8.4; the killer/counter band decision was REOPENED and REVERSED in v4.4.0.** It shipped 5A, 5B (trimmed), 5D, the quiescence rework, 5F ProbCut, history gravity (v2.8.2/v2.8.3), the fixed-point LMR pipeline (v2.8.3), and the ttCapture/ttPv adjusters (v2.8.4). In 2.8.2, continuation history adopts gravity while retaining the killer/counter hard bands (replacing them with continuous bonuses failed H0). **v4.4.0 made that swap successfully (+8.0 ±14 over 1125 games)**: the 2.8.2 test ran against a butterfly table that was numerically broken, so it was measuring a fixed prior against a table that did not work. The rest of the reference search suite does not port to this classic engine. **The next block is NNUE (block 6, v3.0.0).**

---

### ✅ 5A - Improving flag (v2.7.0) - DONE (+4.0 ±27.1 SPRT STC · +43 ±23 relative in gauntlet LTC, ~2965 ±25 CCRL)

**Estimated Elo: +5-8 · Effort: Very low**

A Boolean variable that the reference passes to multiple sites simultaneously. Minimal code, multiple impact.

```
improving = staticEval[ply] > staticEval[ply-2]  // false if anyone was in check (sentinel NoEval)
```

Implemented (2026-07-16):
- **LMR** - if not improving, reduce one more ply (the use of the flag with the greatest impact).
- **RFP (futility margin)** - `85 × (depth - improving)`: the reference form `165 × (depth - improving)`, already in its equivalent ×0.48 (85/ply).
- **LMP** - threshold `3 + d²` halfway through if not improving.
- **NMP** - deliberately NOT touched: the refined entry condition (which also consumes the flag) is a range of 5B.
- Static evaluation per ply in `_stackEval[]` with sentinel `NoEval` for nodes in check.

**Result:** +4.0 ±27.1 STC (SPRT stopped at 380g with LLR ~0 - real but small gain at 10+0.1), but **+43 ±23 relative in the LTC gauntlet vs the +16 of 2.6.9 in identical field and CT**: search gain INCREASES with CT (opposite pattern to the terms of evaluation). ~2965 ±25 CCRL - first version measured above the plateau 2941-2944.

**Lesson (2026-07-16):** Search features are best validated on LTC-the SPRT STC undervalues ​​pruning/reduction improvements because its accuracy is compounded with depth. For the rest of Block 5: Don't dismiss a feature based on a flat STC without looking at the gauntlet.

**Field Audit (3 cross gauntlets):** renamed Ethereal 2756→2910, Inanis 2997→2905, Bit-Genie 3101→3010 (consistent deviations across all three rolls); Meltdown-2817 cleared; Marvin-3000 and Winter-3200 under observation.

---

### ✅ 5B - NMP and futility refined (v2.7.1) - RANGE TRIMMED BY MEASUREMENT - DONE

**SPRT vs v2.7.0 (two GROUPED runs): +2.9 ± 7.4 Elo at 4347 games [0.504]** (run 1 stopped stable at 1398p [0.517] +11.8; run 2 to H0 term at 2949p [0.498] −1.3; the A/B control between both builds - with/without mate fix - gave [0.500] at 1743p: same engine, the grouped one is the honest figure and run 1 was high tail of the noise). **Gauntlet LTC: +44 ± 23 relative to the field (56.3%, 624 games) → ~2970 ± 25 CCRL** - per the lesson of 5A, the quality signal of a search block is the LTC, not the STC. No field names this cycle (Marvin-3000 and Dumb-2856 under observation).

**In addition, two mate-hunting arrangements found in Arena game (Lost Noa refused to capture a queen leading to mate-in-8 and got into mate-in-4):**
- The iterative deepening broke as soon as an iteration returned a mate score (`|score| > MateBound → break`With mate against you, the deep iterations are precisely those that find the longest defenses (the rook mated-in-8 endgame needs 16 plies). It also explained the "give away the pieces when you're lost" principle. The reference never ends with mate: the clock ends the search. Verified: the KRK defense continues deepening d8→d22+; WAC 259→262 (continuing after a mate found also shortens one's own mate).
- UCI reported mates as `score cp ±99xxx` instead of `score mate N` (protocol violation; absurd evaluations in GUI and risk in adjudication).

**Original Elo rating estimate: +5-8 · Lesson learned: the reference NMP is NOT yet portable**

**History (2026-07-17):** The full reference bundle was faithfully implemented (input condition with margin improvement/complexity, statScore filter, deep R, verification, capture futility, quiet futility by lmrDepth - all ×0.48 where appropriate and recalibrated ×0.28 measured at history thresholds) and SPRT failed it: **[0.451], -34 Elo at 143 games**. Dissection with WAC-300 + node benches across seven builds identified THREE ecosystem dependencies:

1. **Deep R needs reliable quiescence.** Reference nulls land in qsearch from depth 3-7, and ours was returning false scores there (WAC 249/300; WAC.001 mate went from d13 to invisible >d17/100M nodes; verification from d8 neither recovers tactics nor preserves nodes). → resume after the quiescence correction block.

   ⚠️ **CORRECTION (2026-07-19, user audit):** This point stated that "YOUR qsearch generates JACKS in the first ply." **This is FALSE** and the premise propagated to five subsequent decisions (5E multi-cut gates, 5F small ProbCut, etc.). `search.cpp` The reference literally says, "We presently use two stages of move generator in quiescence search: captures, or evasions only when in check": out of check it generates captures, in check it generates complete evasions. **It does not generate quiet checks.** The real difference was another: in check it starts `bestValue = -VALUE_INFINITE`This leaves its entire pruning block dead and forces it to search for ALL evasions (including quiet ones) and detect mate-which is exactly what ours was lacking. Never implement a silent check search claiming reference fidelity, because it wouldn't be true.
2. **Entry conditioned by evaluation requires an accurate evaluation.** Require `staticEval >= beta` The tree inflated by ~30% to the same tactic: our classic eval is noisy regarding the search and probes with eval<beta continue to find real cuts. → resume with NNUE.
3. **The futility by lmrDepth requires the large reference reductions** (its lmrDepth is systematically lower) - and **pruning margins do NOT carry ×0.48**: the crudes reproduce our validated margins (d3: 251 vs 300; d4: 396 vs 400); the scaled ones double prune and blind. → 5C.
4. Capture futility without check test prunes capture sacrifices (−6 WAC); its reference form also requires captureHistory. → 5G.

**What v2.7.1 DOES include** (regarding the old, untouched NMP):
- **StatScore Stack** - `2×butterfly + contHist - 1250` per ply (thresholds ×0.28: ratio measured between our gravity-free depth² tables and the reference gravity-capped tables; probe: butterfly p99 3218, contHist p99 630 vs caps 14365/29952).
- **statScore term in RFP** - `staticEval - 85×(depth-improving) - statScore[ply-1]/180 >= beta` + guard `staticEval >= beta`After a refuted move by the father, the static cut comes sooner; after a move of high reputation, it demands margin. **Main source of node savings.**
- **NMP verification at a depth ≥ 14** with `nmpMinPly = ply + 3(depth−R)/4` (anti-zugzwang, Fine 70 ✓).
- **NMP fail-soft** (returns nullScore, not beta) + preserved mate guard (mate range falls to actual search).
- **improvement** with fallback ply-4 after checks; STRICT cold default (the +173 reference relaxes LMR/LMP on every cold node and inflates the tree by +36% measured).

**Measured result:** WAC 258-265/300 vs 259 of 2.7.0 (equal within noise ±5), nodes d15 2.92M vs 3.72M (−21%), startpos d16 2.25M vs 4.10M (−45%), mate WAC.001 to d13 (parity), Fine 70 ✓, 138 tests.

---

### ❌ 5C - LMR adjuster suite + statScore (era v2.7.2) - MEASURED AND CUT (2026-07-18)

**All 5C content measures NEGATIVE at the actual rate. Cut by the project decision rule (as king-safety Phase B). The search was reverted to the exact 2.7.1 and version number 2.7.2 is free for the next block.**

The numbers (so as never to try it again without its ecosystem):

| Candidate | Content | vs 2.7.1 |
|---|---|---|
| Full reference bundle | base 20.26·ln + delta/rootDelta + 8 adjusters + statScore/13628 without clamp | **-9.7 ±13.8** (SPRT 10+0.1) |
| Conservative rebuild | validated 2D base + 6 adjusters + statScore clamp; nps equal, -23% nodes, WAC 263 | **-25.7 ±20.0** (SPRT 10+0.1) |
| Va: adjusters only | cutNode/ttCapture/moveCount>7/cutoffCnt/singularQuiet/threats | **-11.5 ±16.0** (1000p 5+0.05) |
| Vb: machinery only statScore | 4 components (fix contHist ply2/4) in RFP + reprieve futility | +17.4 @5+0.05 but **-10.8 ±14.3 @10+0.1 (SPRT H0)** |
| Vc: Vb + statScore at LMR | the star consumer of the reference | **-6.9 ±16.3** (1000p 5+0.05) |
| **Vc bis (2026-07-23)** | statScore in LMR, **at the real pace** and on pipeline in 1024ths | **˜ -18 Elo**, 47.4%, LLR -1.85, **H0 vs v2.8.2** |

### Reopening and closing of Vc (2026-07-23)

Va and Vc had been cut off with games at **5+0.05**, a rate that the first golden lesson of this same block declares incapable of predicting the sign at 10+0.1 - and Vb proved it by going from +17.4 to -10.8. Vc was remeasured at the actual rate. **It confirms the cutoff**, now with valid evidence.

**Three findings from the diagnosis, more valuable than the experiment:**

1. **Granularity was an unidentified ecosystem.** The reference carries the ENTIRE LMR pipeline in 1024ths of a ply (`reductionScale`, `-delta*577/rootDelta`, `+982` basic, `r -= 2179` in TT play, `r += r*276/(256*depth+268)` in allNode) and divides only at the end; each of its adjusters is a FRACTION of a ply. Ours was integer and the table was already truncated when constructed, so eight adjusters of ±1 integer ply cause swings that the reference never applies. **Converted to fixed point and verified neutral** (identical nodes in 6 positions; `floor(a)+k == floor(a+k)` (for whole k). It does not explain the failure on its own, but it is a real prerequisite and it has already been done.

2. **The term "history" in LMR had been dead for some time.** It was `Clamp(butterfly/16384, ±2)`Calibrated for a table that reaches its rescale of 2²°. Actually measured: butterfly p99 2840, max 6086. That division yielded **0 in over 99% of the quiet plays**.

3. **The cause of the failure: the butterfly table is biased by construction.** Measured signed distribution: mean **+71.8**, median **-8**, p10 -156, p90 +75, only 25% positive entries, tail up to 6086. Subtracting that in LMR does not discriminate - it exempts from reduction to a few plays repeatedly throughout the tree (+15-20% of measured nodes, which at fixed TC is just the lost Elo). `AddBonus` It was growing with global rescale on the positive track while `AddMalus` I cut out individual pieces from the negative.

**New golden lesson: formula fidelity ≠ semantic fidelity.** The reference consumes RAW StatScore in LMR because its own is centered at zero (gravity-bounded and symmetric tables). Copying "uses the raw value" onto a biased table introduces a bias that the reference does not have. Before porting a consumer, measure the DISTRIBUTION of what they consume, not just its magnitude.

**Methodological corollary:** Node counting does not calibrate the divisor either. A sweep yielded +19.2 / +1.2 / +24.9 / +23.7 / -1.2% with verified deterministic searches-genuine chaos versus a pruning parameter, not measurement noise.

**New golden lessons:**
1. **Benchmarks do NOT validate search changes**: the rebuild had the best profile ever measured (-23% nodes, WAC 263, same nps) and lost 25 Elo playing. Only games at the REAL pace validate; hyper-fast matches (5+0.05) can reverse the sign compared to 10+0.1.
2. **The reference LMR suite presupposes its ecosystem** (reduces from move 2 with captures, ttPv in TT, qsearch with checks, its history dynamics). Each subset loses to our validated quiet-only LMR. Same kind of failure as the 5B NMP bundle. Closed in v2.8.4 with ttCapture/ttPv embedded; the rest of the suite does not transfer to this classic engine and is not being revived.
3. **The actual fix found** (the ply-2/ply-4 contexts of the contHist were never written - single parity keys, detected with a probe that read exact zeros) is implemented and archived with its measurements; it will be resumed in 5G when the history update rule is the reference one (bonus/gravity), which is what makes those readings reliable.
4. ttPv -2 with proxy PvNode exploits the PV subtree +220% - needs the flag on TT (done in 5D/v2.7.2; LMR consumer will be tested per game).

**Improved Base Formula:**
- reference: `(20.26 + log(threads)/2) * log(i)` stored in a 1D array `Reductions[i]`
- NoaChess: `0.75 + log(d)*log(m)/2.25` (2D table) - the reference reduces more aggressively
- **Delta adjustment**: `-delta*1024/rootDelta` This makes positions with an adjusted window reduce less (adapts LMR to the current aspiration state)

**Additional adjustments to the base reduction:**

| Adjustment | Delta ref | Current status (updated 2026-07-23, v2.8.4) |
|--------|---------|---------------|
| cutNode | +2 (r += 4026) | ❌ CUT at both magnitudes at the actual rate (−4.0 H0 at 4026; −7.1 H0 at 1536); the yarn remains (neutral, used by the following) |
| PvNode / ttPv | −1 − 11/(3+depth) / −2 | ✅ **EMBARKED v2.8.4** as ttPv ×0.34 (+7.5 screen) |
| ttCapture in play TT | +1 (r += 1079) | ✅ **EMBARCADO v2.8.4** raw (+7.1 screen) |

**statScore** - ⚠️ **THE FORMULA BELOW WAS OUTDATED. Corrected against the source on disk on 2026-07-23.**

What this section said (4 components in plies 1/2/4, offset -4433, `r -= statScore / 13628`**does not match `search.cpp`**. This is the same kind of error that already cost the NMP dynamic R port: my own notes citing an old revision of the reference. What the actual source says:

```cpp
// search.cpp:1322-1325 - ONLY plies 1 and 2, no offset, with weights
ss->statScore = (2252 * mainHistory[us][move.raw()]
               + 1126 * (*contHist[0])[movedPiece][move.to_sq()]
               + 1093 * (*contHist[1])[movedPiece][move.to_sq()]) / 1024;

// search.cpp:1328 - consumption in LMR, with r in 1024ths of a ply
r -= ss->statScore * 439 / 4096;
```

Differences that matter:
- **Two contHist contexts, not four.** Plies 1 and 2. Ply-4 and ply-6 do not exist in LMR consumption.
- **No recentering offset.** The -4433 belongs to ANOTHER consumer; our `StatScoreOffset` It exists for the RFP guard. Putting it in LMR is a mistake (I almost made it).
- **The real divisor is `439/4096` about `r` in 1024ths**, no `/13628`.
- **That raw consumption is only safe if the statistic is centered at zero**, which is the case for the reference and NOT for us. See the Vc closing above.

**RULE: Do not carry from this table. Read `search.cpp`, `movepick.cpp` and `history.h` on the disc before touching anything.**

Other consumers of the reference (also verify before use):
- NMP guard: `(ss-1)->statScore < 17139`
- Futility: `history/52` added to the evaluation

---

### ✅ 5D - Improvements in TT (v2.7.2) - DONE (was 5F, renumbered to the actual execution order after the 5C cut)

**Estimated Elo: +5-8 · ACTUAL: +37.9 ±15.0 SPRT pooled at 1103 games [0.554]** (two nearly identical H1 runs: +38.3 own at 546p and +37.6 confirmation at 557p, both LOS 100%) - the largest search gain since v2.3.0. **LTC Gauntlet: +48 ±23 relative (56.8%, 624p) → ~2975 ±25 CCRL** (LTC anchor saturates between adjacent versions; SPRT carries the increment). **Field Audit: Renamed Dumb-2810 and Marvin-2960 VALIDATED** (deviations −16/−56 after systematic −45/−35); **BitGenie-3010 on watch** (implied −130 on this roll after a clean cycle - volatility of a roll, unrenamed).

**Why it was brought forward (2026-07-18):** After 5B and 5C it was demonstrated that reference heuristic CONSTANTS do not transfer without their ecosystem; 5F is pure INFRASTRUCTURE and transferred to the first one.

- **Clustering** ✅ - exact 16-byte entry (key32 + score int32 + eval int32 + move16 + depth8 + genBound8) → **4 entries per 64B cache line** (the reference puts 3×10B into 32B with int16 scores; our ±100k mates maintain int32 and the 4-byte cluster compensates without rescaling the mate scale).
- **Aging / Generation** ✅ - 5-bit generation (cycle 32) at the beginning of each "go"; replacement by `depth − 8×edad_relativa` (exact formula of the reference); a hit refreshes the generation.
- **Static evaluation in TT** ✅ - Hit serves the cached evaluation without an evaluator; miss saves evaluation-only input (bound None: never cuts off, never evicts real results). **+24% nps measured.**
- **PV flag in TT** ✅ stored and sticky - **NO consumer in LMR on purpose**: the ttPv −2 was measured at 5C by exploding the PV subtree; with the actual flag already stored, that adjuster will be tested PER GAME in a later block.
- Reference overwrite rule (Exact fresh always; bound >4 plies more superficial never; best move and PV mark survive).
- Benchmark: −19% nodes, +24% nps, WAC 265/300 (record), Fine 70 ✓, KRK ✓, 184 tests (7 new TT tests).

---

### ❌ 5E - Double extensions + earlier singular - MEASURED AND CUT (2026-07-19, campaign v2.7.3)

**Four SPRTs at 10+0.1, all negative or in equity: -19.7 (full port) / [0.492] (trigger only) / -12.5 ±15.0 (+ evasion rework in qsearch) / [0.476] (+ guard `!is_loss`). Cut by the project's decision rule.**

**Root cause:** Reference extensions are only stable alongside reductions in their caliber (r += 4026 cutNode, +1079 ttCapture in 1024ths; our entire LMR table caps near 4 and doesn't fire before move 4). The accelerator needs the brake.

**Also measured and rejected along the way:** `depth++` singular (tree explosion), faithful margins `(28+32)*depth/63`, multi-cut (WAC 265→245), and **TT probe/store on qsearch at depth 0** (depth-0 entries flood the clusters and evict those from the main search: d15 nodes GO UP 1.35M→1.75M, nps −11%).

**Closed. Not to be resumed before the NNUE:** All these variants live or die with the ecosystem of reductions/eval of the reference, which block 6 replaces. Candidate 5 code archived.

---

### ✅ 5F - ProbCut rework + capture history - RE-AUDITED AND ONBOARDED IN v2.8.2

**Final state (2026-07-21, v2.8.2):** Reimplemented on top of the already corrected quiescence and with a strict floor of a normal search ply: no cutoff rests solely on quiescence. Isolated A/B test against the frozen 2.8.1 executable: **59-51-90, 52.0%, +13.9 ±35.8 Elo, LOS 77.7%**. Ships as part of the complete candidate; component Elo values ​​are not summed.

**Onboard Contents:**
- ProbCut entry from depth 3 on **any node type** (previously: only non-PV from depth 5). Reference guard: if the TT score is already below the bar, it is not attempted.
- **Margin sensitive to improving**: `beta + 150 - 40×improving`The base remains OUR game-validated 150 - the reference 241 measured worse here in nodes because its margin presupposes its eval/qsearch accuracy (same scaling lesson as 4B/4C/5B). The subtrahend is its 64 rescaled to our base (64 × 150/241 ˜ 40).
- **Verification depth also sensitive to improving**: `depth-5` improving / `depth-3` if not (before a `depth-4` (flat). A more reliable evaluation buys a CHEAPER margin and pays for a DEEPER test; the two controls move in opposite directions on purpose.
- **SEE filter of the reference**: the exchange must cover the gap between the static eval and the bar with material only.
- **Fail-soft return** with the discounted margin + store of the verified fail-high in the TT to `probCutDepth+1` (lower bound). The values ​​in the mate range do not trust a reduced search and continue scanning captures.
- **Small ProbCut** (`beta + 428` on a lower bound of the TT to =4 plies) **restricted to `!inCheck`**, diverging from the reference on purpose: unrestricted it cost **16 WAC points** (255 vs 271) because our quiescence is capture-only and their lower bounds in check cannot withstand a blind cut.
- **New table `CaptureHistory`** `[piece][to][victimType]` with gravity update (`entry += bonus - entry×|bonus|/4096`- the hard-won lesson in 5G. Powered by cutoff bonuses, penalties to sister captures, and fail-low bonuses to the parent capture. **Only read by the ProbCut sorting** for now; capture futility and the main capture sorting are later (v2.7.5).
- **`cutNode` propagated throughout the search** exactly like the reference. Only consumer today: ProbCut verification (same deliberate pattern as the 5D consumerless ttPv).

**Discarded: the IIR form of the reference.** `depth-1` For PV/cut nodes only, starting at depth 6, we measured **+22% of nodes at the same depth** in isolation; an intermediate variant (same node filter, starting at depth 4) still resulted in +4.8%. Our validated approach (depth = 4, all node types) remains: with our weakest ordering, reducing nodes without information *everywhere* is a structural burden. Same ecosystem lesson as the 5C reduction suite; it is not revisited before the NNUE.

---

### ✅ 5G - History and Sorting - CAPTURE HISTORY IN v2.8.1; GRAVITY IN v2.8.2, CONTINUOUS BANDS CUT

**The "quiet scoring multilevel" half was attempted in four builds at 10+0.1 and failed: -33.9 (shared table) / -10.9 H0 at 1180p (separate tables) / [0.496] at ~1900p (+ gate depth=6) / -4.2 H0 at 2000p (+ gravity). The last two are exact fairness: the correct infrastructure neither loses nor wins.**

**REAL defects found and fixed along the way (fixes are tested and archived):**
1. **A shared table corrupts levels** - the bonus written for "the move 2 plies ago" falls on the same key that another node reads as "1 ply ago". With tables separated by distance, a control that reads only level 0 replays v2.7.2 **bit by bit**.
2. **The blend should not reach the statScore** - the RFP thresholds (offset 1250, divisor 180, transfer ×0.28) describe a one-level signal.
3. **Blend everywhere costs -9.9% NPS** (5 random probes over 14 MB per quiet); with gate to depth = 6 it gains in nodes Y nps (-11.5/-14.0% real time to depth).
4. **Gravity instead of clamp** - the table never decays within the game (18M entries, impossible to sweep it like the butterfly); with hard clamp frequent pairs are stuck on the rails ±2^20 and a stuck level 0 entry puts ±1M in the statScore. `entry += bonus - entry·|bonus|/2^20`, O(1), invisible in bench by design.

**Historical Zero Hypothesis:** Killers and counter-moves occupy fixed hard bands (3.0M / 2.9M) above the historical level. The definitive test of v2.8.2 refuted that removing them was an improvement to this engine: continuous bonuses were part of the complete candidate that lost **-13.1 ±15.2 Elo, H0**, while the RC2 that restored the bands gained H1. The bands remain as a validated design; they are not reopened without an isolated A/B test of sufficient resolution.

**Final State:** Capture history, check/threat bonus, and partial quiet sort were implemented in v2.8.1. In v2.8.2, continuation history uses gravity, but killers/counter retain the absolute bands. The short A/B test of continuous bonuses (**50-48-102, +3.5 ±33.8**) did not resolve anything; the SPRT in the final package did, and it selected the bands. The multilevel blend that had already measured fairness was not added. **Superseded by v4.4.0**, which removed the bands once the history rails were rebuilt; a second continuation distance was retried on top and still measured nothing (0.9% nps for no gain), so 5G stays closed on its own merits rather than on the ceiling.

---

### ✅ 5H - Aspiration, draw detection, check extension - RE-AUDITED AND TRIMMED IN v2.8.2

**Final Result (2026-07-22):** The short adaptive initial window + fail-low recenter A/B (**63-47-90, +27.9 ±35.8**) and check extension (**-1.7 ±32.5**) included zero. The first complete candidate incorporating them lost **-13.1 ±15.2 Elo, H0 at 1115 games**. The final RC2 restored the fixed initial window and removed the extension, retaining only the beta recenter after fail-low; H1 passed with **+28.0 ±17.2**. Imminent repetition detection is retained.

---

## ✅ BLOCK 9 - Syzygy Endgame Tables (v2.8.0 - ADVANCED, before the NNUE)

**Status: Completed in v2.8.0 and fixed in v2.8.1.**

**Why before the NNUE (decision 2026-07-18, exact order of the reference):** The reference integrated Syzygy (2014) years before the NNUE (2020), and its data pipeline exploits this: datagen games are **awarded** upon entering with =6 pieces, and endgame positions are **re-labeled** with the exact WDL from the tablebase-the noisiest part of the dataset now has perfect labels. With our historical weakness in endgames (mate bug in 2.7.1), this improves play NOW and the quality of the dataset AFTERWARDS. The competitive opening book (block 10) is NOT brought forward: the reference doesn't have its own book-what the datagen needs is a SEED book of varied positions (UHO style, see 6B), not a winning book.

Syzygy gives the perfect result (WDL + DTZ) for positions with = 7 pieces.

### ✅ Implementation - DONE in v2.8.0, corrected in v2.8.1

⚠️ **Point 1 of this plan was NOT fulfilled, and that was the right decision.** The original plan and what was actually shipped are noted, because the index suggested that the block had not yet been started.

1. ~~**P/Invoke by Fathom**~~ → **✅ MANAGED C# PORT of ~1250 lines.** There is no C compiler on this machine and a native DLL would break the requirement of a single self-contained executable. Since a wrong index returns an *erroneous but plausible* result that the search is believed to be true, the port was validated **differentially against an independent prober over 3000 random 3- to 5-piece endings: zero discrepancies in WDL and zero in DTZ**. That harness caught three bugs that would have gone unnoticed (symbol tree base cached by table instead of by `PairsData`, which hung the engine with pawns; off-by-one in the DTZ remapping; and captures that left naked kings failing instead of returning draws).
2. **✅ Root ranking** - deliberately implemented as a **filter and NOT as an immediate return**: returning the verdict directly would replace "mate in 3" with a generic TB win in the UCI start and undo the mate announcement from v2.7.1. Two critical bugs fixed in v2.8.1: the filter was overridden because `SearchRoot` It regenerated the moves after applying it, and the DTZ ranking scored irreversible moves before they occurred and chose the fastest defeat in lost positions.
3. **✅ WDL probe on Negamax** - after the TT probe, conditioned on the 50-move counter, with verdicts on its own side below the mate range. TT safety added in v2.8.1: `CanReuseTtScore` blocks the reuse of scores in band TB when `halfmoveClock > 0`. Guard sorted by selectivity (piece count first, against a cached limit that is zero without tables): cost 1.1% NPS in positions that never probe, versus 3.5% for readable order.
4. **✅ UCI options** - `SyzygyPath`, `SyzygyProbeDepth`, `SyzygyProbeLimit`, `Syzygy50MoveRule`, the four declared.

**Measured:** A won KPvK converts to 15 plies versus 25 without draws, while KRvK and KQvK convert the same with or without them - the gain is where the heuristic fails, not where the material already decides. `SyzygyTable` migrated to `MemoryMappedFile` with offsets `long` in v2.8.1, removing the 2 GB cap `byte[]` for 6/7 piece files.

**Known debt:** there is a `NullReferenceException` intermittent in `SyzygyTable.U8` during `ProbeDtz`, reproducible ~1 out of every 4 suite runs. Diagnostics: the tests run in parallel and reinitialize the static state of `Syzygy` While another probes. In a live game. `Syzygy.Init` It is called only once from `UciLoop`So, it's not a starting risk, but it's wise to keep a backup in case a GUI takes over. `setoption SyzygyPath` with search in progress.

### ✅ Tables - 3-4-5 piece set INSTALLED

In `syzygy` folder: **290 files, 940 MB** (145 `.rtbw` + 145 `.rtbz`), maximum 5 pieces. Verified on 2026-07-23.

| Pieces | Size | Condition |
|--------|--------|--------|
| 3-4-5 | ~1 GB | **✅ INSTALLED** - enough for most endings |
| 6 | ~150 GB | 🔲 the reader already supports it (`MemoryMappedFile` + offsets `long`) |
| 7 | ~18 TB | 🔲 only with dedicated hardware |

**Pay attention when configuring:** `max_pieces` from the bot and `SyzygyProbeLimit` They must not exceed the installed game - with 5 pieces on disk, every probe above that simply fails.

---

## ✅ BLOCK 6 - NNUE - Production (v3.0.0)

**Status: DONE (v3.0.0, 2026-07-25) · Neural network outperforms classic evaluator: gen3 +4.5 ±11.4 Elo, positive exhausted at 2650 games, LOS 77.8% · LTC calibration gauntlet pending**

Final schema **HalfKAv2_hm (feature_schema_id 2)**, not the HalfKP assumed by the original plan: kings as features, InputSize 22528 per perspective (32 buckets × 704), topology FT 22528→128 ×2 → L1 32 → 1, quantization QA=255/QB=64/OutputScale=400. AVX2 SIMD inference (VPMADDWD, precomputed clipped activation, merged MoveFeature) at ~66% of the speed of the classic. Incremental accumulator with full-refresh on king move + rival perspective patch, verified by incremental parity==recalculation. Datagen (`NoaChess.DataGen`) with mixture `lambda·sigmoid(score/SCALE) + (1−lambda)·wdl` and Syzygy/resign/tables adjudication.

**Critical datagen bug fixed:** `FindBestMove` returned Score 0 at the hard-stop by nodes during the first root play, setting 57% of the labels to zero (57.6%→2.1%; invisible to the game, only the `.Score` (which the datagen consumes). Without that correction, the network learned "half a table is dead equality".

**Generational Self-Play** (closes the distribution shift of the 1st generation imitation network): each promoted network trains the datagen of the next. gen2 +1.9 Elo, gen3 +4.5 Elo vs. classic. End-to-end automated pipeline. Network `noa-gen3` embedded in the executable. **Next: iterate through generations (gen4+) and anchor the absolute CCRL with a gauntlet.**

The inference infrastructure was already complete: `NnueNetwork.cs`, `NnueInference.cs`, `NnueAccumulator.cs`, `NnueAccumulatorStack.cs`, `NnueFeatureIndex.cs`, `NnueEvaluator.cs`, `NnueModelLoader.cs`, `NnueModelHeader.cs`, `IIncrementalEvaluator.cs`.

---

### ✅ 6A - Feature encoding + incremental update successful - DONE (v3.0.0)

**Implemented as HalfKAv2_hm (schema 2), not HalfKP as the original plan stated.** Kings as features, InputSize 22528. Incremental update with full refresh on king movement + rival perspective patch, and `PushMove`/`Pop`/`PushNull` verified by the incremental parity test==recalculation.

Before generating data, verify that the inference code is complete:

- **`NnueFeatureIndex.cs`** - implement **HalfKP** first (simpler than the reference HalfKAv2-hm):
  - Index = `king_square × 640 + piece_type_color × 64 + piece_square`
  - 41,024 possible inputs (64 king positions × 640 piece features)
- **Incremental update in `NnueAccumulatorStack`** - the critical case is the king's movement: when the king moves, the "king bucket" changes and **ALL** features change → **full refresh required**. `PushMove` It must detect if the mover is a king and call `RefreshAccumulator()`.
- Verify `PushMove` (add/remove features), `Pop` (restore accumulator), `PushNull` (no change of features).
- **Blending in transition** (optional): `NNUE * 0.5 + Classical * 0.5` It can compensate for a weak network while improving. The reference engine uses pure NNUE with classic fallback only when `count(pieces) > 7 AND abs(psq) > 1760`.

---

### ✅ 6B - Data generation from self-play - DONE (v3.0.0, with deviations)

**Created using a standalone tool `NoaChess.DataGen`** (not ICU mode) `go datagen` that the plan proposed). Self-play with node-limited search (`--nodes`), labels `lambda·sigmoid(score/SCALE) + (1-lambda)·wdl(result)`binary format `.noadata` with magic header. Award by **resign** (`--resign`) and **tables** (`--drawscore`/`--drawcount` after `--drawply`) + `--maxplies`**NOT implemented from the plan:** the re-labeling of Syzygy WDL for positions =6 pieces (the tablebases have existed since v2.8.0 but the datagen does not yet query them) and the UHO seed book (starts from random openings). These are pending as future improvements to the datagen.

- **Target: 50-100M self-play positions** at a depth of 7-9.
- **Output format:** binary - (position on bitboards, side to move, static eval in centipawns).
- Labels are the classic NoaChess evaluation **after all 4+5 blocks implemented** → highest quality labels.
- **SEED Book of Openings (reference order, verified 2026-07-18):** Datagen games start from a book of varied POSITIONS, not from startpos - diversity of distribution, not strength. The reference uses `noob_3moves.epd` from their official books repository in `generate_training_data` (source: nnue-pytorch wiki, Training datasets). Candidates for NoaChess: noob_3moves.epd as is, or UHO. This is NOT block 10.
- **Syzygy Re-labeling (reference order, requires block 9 = v2.8.0 done):** Allocated items upon entering =6 pieces; every position in the dataset with =6 pieces carries the exact WDL label from the tablebase instead of the eval - the noisiest part of the dataset gets perfect labels. (Verified 2026-07-18: the official nnue-pytorch pipeline passes SyzygyPath to the datagen and its rescorer re-labels with TB; re-labeled datasets "generally produce better networks".)
- **Data Plan B (note 2026-07-18):** The best modern reference networks are trained with data DERIVED FROM LEELA (lc0) re-labeled with Syzygy, not with their own self-play data - if our self-play dataset stagnates at 6C/7, converting public Lc0 data (lc0-data-converter tool) is a proven alternative.
- Filter: check positions, positions with eval > 2000 cp (too unbalanced).
- Implement ICU mode `go datagen` integrated into the engine - the cleanest way to pitch from bat.
- Expected distribution: ~70% positions of tied/balanced games (0-200 cp), ~30% positions with a clear advantage.

---

### ✅ 6C - Training - DONE (v3.0.0)

**Final architecture HalfKAv2_hm 22528→128→32→1** (not HalfKP-256). Own PyTorch pipeline: `train_nnue.py` (cosine LR + weight decay, CUDA), `validate_nnue.py` (corr/slope/RMS/sign), `export_model.py` (float → `.noannue` quantized). Parameterized network width (`--ft-out`/`--l1-out`The C# loader reads the header dimensions, so scanning architectures is Python-only. Early generations lacked the classic (1st gen imitation .NET); generational self-play closed the gap.

- **Target architecture:** HalfKP → 256 neurons (hidden) → 32 → 1.
- **Tool:** `nnue-pytorch` (community trainer, format compatible with the reference format) or your own trainer in PyTorch.
- **Quantization:** Export weights in int16/int32 in the expected format. `NnueModelHeader.cs`.
- **Iterations:**
  - run1 / run2: debug, format verification, successful loading into the engine.
  - run3/run4: first models with real data. Expect the classic to surpass them yet-the labels are good, but the network is small.
  - run5+: the network surpasses the classic → turning point. From here, iterate.
- NPS benchmarks: NNUE inference costs nodes. Measure with `bench` before and after activation.

---

### ✅ 6D - NNUE activation in production - DONE (v3.0.0, kernel)

**SPRT `NnueEvaluator` vs `ClassicalEvaluator` Passed: gen3 +4.5 ±11.4 Elo, positive exhausted at 2650p, LOS 77.8%.** Selectable with the ICU option `UseNNUE`; grid `noa-gen3` embedded in the exe. **NOT implemented from the plan (refinements pending):** dampening by rule-50 of the NNUE evaluation (`v = v·(195-rule50)/211`) and the `nnueComplexity` for time management. `rule50` What already exists in the search is the Syzygy DTZ ranking, not this.

- SPRT `NnueEvaluator` vs `ClassicalEvaluator` a TC 10+0.1.
- If H1 passes → v3.0.0. If not → run next.
- NNUE Complexity: `nnueComplexity = (416*nnueComplexity + 424*|psq-nnue|) / 1024` (measures divergence between PSQ and NNUE - useful for time management).
- Dampening by rule-50: `v = v * (195 - rule50) / 211` - reduces the evaluation in positions with a high 50-move counter.

---

### ✅ 6E - Lazy SMP multithreading - DONE (v3.1.0, 2026-07-28)

**Implemented.** Lazy Parallel Search SMP: N workers search for the same root, sharing a single TT; the rest of the state (search stack, stories, board, evaluator) is per thread. The main worker manages the time and reports; at the end, the workers vote on the move.`Threads=1` It is byte-identical to v3.0.0** (verified: 1,307,077 exact nodes in a 6-position fixed-depth battery). Node scaling ~7.6× to 8 threads. **Expected real-world gaming Elo +30-60 at long TC with many cores; LTC calibration gauntlet pending.**

**Estimated Elo: +30-60 Elo in real-world gaming with 16 cores (Threadripper 2950X)**

- **Lazy SMP:** Multiple threads search for the same root, sharing the TT; they diverge due to races within the TT and intersect the best lines. Done.
- `Board.Clone()` Give each thread its own board (deep copy with history); make/unmake never competes. Done.
- Thread-clonable evaluator: NNUE shares the read-only network with its own accumulators; classic is a new instance (scratch per call). Done.
- Shared TT lock-free by benign races (32-bit key verification + pseudo-legality veto rule out broken reads). Done.
- UCI option `Threads` default 1, max 16. Done.
- **Pending:** calibrate with gauntlet at long TC (60+0.6) where the SMP contributes more than in bullet.

---

## ✅ BLOCK 7 - NNUE iterative self-play (v3.1.0)

**Status: PENDING · After block 6**

- **Iterating the network by self-play (RL):** The engine plays against itself with the previous network as the evaluator → generates positions → retrains → new network. With each iteration, the network moves further away from the classic teacher and takes off.
- In run5+ the network surpasses the classic and acts as a teacher of itself.
- Historically in open-source engines this multiplies the Elo x2-3 compared to the first network trained against the classical.
- The cycle: `engine_vN → datagen(50M pos) → train → engine_v(N+1) → SPRT vs vN`.

---

## BLOCK 8 - NNUE with high-level human game positions (v3.2.0)

> ## CORRECTION 2026-07-31 - THIS BLOCK WAS NEVER ACTUALLY EXECUTED
>
> **Verified fact:** every dataset on disk - `selfplay-gen2` through `selfplay-gen7` - records
> `"openingPlies": "8-9 random legal"` in its manifest. **No generation was ever seeded from a
> human book.** The datagen writes `book:<path>` whenever `--book` is supplied
> (`tools/NoaChess.DataGen/Program.cs`), and not one manifest carries it.
>
> **Root cause:** the pipeline was correct and the book existed. `Noa-NnueGen.ps1` declares
> `[string] $Book = ""` and only appends `--book` when non-empty; `books/human.fens` (206 MB) was
> built 2026-07-30 00:37, and the gen7 datagen ran ten hours later **without the `-Book` argument**.
> The manifest recorded the truth; the documentation recorded the intention.
>
> **What this invalidates:** the headline conclusion of v3.2.0 - *"human-opening seeding did NOT
> raise strength over gen5, therefore pure self-play is exhausted"* - was measured on a net trained
> on random-opening data. **The claim is unsupported in both directions:** human seeding was never
> tested, and self-play exhaustion was never demonstrated by this experiment.
>
> **What remains true:** the nets themselves are real and were trained on real data. gen7 is
> embedded in the shipped executable and its ~3080 CCRL is a genuine measured gauntlet result. Only
> the *provenance claim* and the *conclusion drawn from it* were wrong.
>
> **Superseded by:** the real reason five consecutive generations landed flat is capacity and data
> volume, not label provenance - see **BLOCK 12**, which replaces this block's premise entirely.
> Book seeding survives as one input to BLOCK 12, now with a mandatory manifest assertion so an
> unseeded run can never again be reported as a seeded one.

**Status: SUPERSEDED BY BLOCK 12 (2026-07-31) · Infrastructure real and reusable, conclusion void**

This block adds **diversity of positions** to the training without changing the label source.

### Why human games

The weakness of pure self-play is that the engine tends to always explore the same types of positions: stalemates, known variations, positions that its own search favors. High-level human positions cover the range of 0-200 pieces (where real chess resides) with much greater structural variety: positional sacrifices, complex imbalances, technical endgames that self-play rarely reproduces.

### Data sources

- **Lichess database** - public games in PGN format, filtered by ELO = 2400 (both sides). Available at `database.lichess.org`~50M games available.
- **FIDE / chess.com databases** - high-level GM/IM games.
- **Games of the bot itself on Lichess (NoaBot, published 2026-07-16)** - the engine plays 24/7 against heterogeneous bots via lichess-bot (`F:\Works\Programacion\_BOT_EJECUTANDO_NO_BORRAR_lichess-bot-master`Each entry is archived locally (`pgn_directory` in config.yml) and can also be downloaded in bulk from the API (`lichess.org/api/games/user/NoaBot`It provides a diversity of positions against styles that self-play doesn't cover; the quality of the games doesn't matter (the labels are always assigned by the search itself, re-evaluating each position). Modest volume (~100-300 games/day ˜ 5-15K useful positions/day): a COMPLEMENTARY source of diversity, it doesn't replace the massive self-play datagen of Block 6B.
- Extract positions up to move 40 (before trivial endgames) - filter check, eval > 2000 cp.

### Implementation

- The **labels are still NoaChess' own search** evaluating each position at a depth of 7-9 - human games only contribute *diversity of positions*, not external knowledge.
- ⚠️ Playing against a strong engine DOES NOT work: the games are unbalanced (positions with huge advantage) and the tags are still bad because the tag search is on a board that has already been decided.
- **Data mix:** ~70% self-play + ~30% human positions (ratio to be calibrated with SPRT).
- Training with mixing should converge faster and produce a more robust network in infrequent positions in self-play.

### Infrastructure (list 2026-07-29)

- **`San.cs`** (`NoaChess.Core/Notation/`) - SAN parser that solves standard algebraic notation against `MoveGenerator.GenerateLegalMoves`Includes castling (OO/0-0), promotion (=Q or bare e8Q), and disambiguation. Verified with a full real chess.com game plus edge cases. 5/5 tests.
- **`PgnReader.cs`** (`NoaChess.DataGen/`) - PGN reader that extracts the main SAN line by discarding comments `{}`variants `()`, NAGs `$` and movement numbers.
- **`PgnBook.cs`** (`NoaChess.DataGen/`) - subcommand `pgnbook`: reads folders/files/globs of PGNs, replays each game and extracts a random FEN position in the range `[--min-ply, --max-ply]` (default ply 12-20). Options: `--dedup`, `--append`, `--per-game`, `--max`, `--seed`. Resumable (skips files already processed with `--append`).
- **`Program.cs`** (`NoaChess.DataGen/`) - start `pgnbook` + flag `--book <fens>` In the datagen: if specified, each game starts from a FEN in the book (instead of initial position + 8-9 random moves).

### Execution flow (when the PGNs arrive)

```
# For each folder of PGNs (Lichess elite, chess.com GMs):
dotnet run --project tools/NoaChess.DataGen -- pgnbook --in <carpeta> --out books/human.fens --append --dedup

# Datagen with the book:
dotnet run --project tools/NoaChess.DataGen -- --book books/human.fens --games 500000 --out data/gen6_human.noadata

# Train and compare with field gauntlet (not self-play SPRT):
python tools/training/nnue/train_nnue.py --data data/gen6_human.noadata ...
```

---

## BLOCK 12 - NNUE architecture overhaul (v4.x) - THE ACTIVE CAMPAIGN

**Status: PLANNED (opened 2026-07-31) · Branch `4.0.0` · Supersedes blocks 7, 8 and the "more
generations" strategy entirely**

### Why a major version

This campaign deliberately breaks the **frozen** C#↔Python model contract (schema 2 / architecture
id 1): new quantization (int8 L1), new topology (wider FT, output buckets), new format version.
Every prior rule about that contract being immutable is lifted **for this campaign only**, and
re-frozen at v4.2.0. It also replaces the in-RAM training pipeline and retires a UCI-visible
subsystem. That is a major version by every criterion this project has used.

### The diagnosis this campaign is built on

Five consecutive generations (gen3 → gen7) landed flat: +4.5, then +1.9, then +34 (gen5, over an
inflated 1495-game link), then gen6 failed to promote, then gen7 at +3.7 parity. Deepening labels
24k → 28k nodes bought +3.7 Elo. **That is the signature of a saturated network, not of exhausted
data.** The measured facts:

| Dimension | NoaChess today | Reference-class engines | Ratio |
|---|---|---|---|
| Feature transformer width | **128** | 1024 | **8× narrower** |
| Output buckets | **1** | 8 (by piece count) | - |
| Positions per generation | **13.1 M** | billions across training | **~100× fewer** |
| Samples seen per run | **~550 M** (7 gens × 6 epochs) | tens of billions | **~50× fewer** |
| Net file size | **5.7 MB** | 45-70 MB | - |
| L1 arithmetic | int16 (`VPMADDWD`) | int8 (`VPDPBUSD`/emulated) | **2-4× slower** |

**The cost model that justified staying narrow does not survive arithmetic.**
`NnueInference.cs` asserts the L1 dot product is "THE cost of NNUE eval". At FT=128 / L1=32 that
product is 32 × 256 = **8,192 int16 MACs ˜ 512 AVX2 instructions per evaluation** - far too small
to dominate anything at 446k NPS. If evaluation really is ~50% of runtime, the cost is somewhere
else: **feature-transformer row traffic** (5.8 MB of weights, random access by feature index, no
chance of staying in L2) and **king-bucket accumulator refreshes**. Those are fixable; width is
only unaffordable if you never fix them.

### Ordering rule for this campaign

Capacity is worthless without data, and data is unusable without a streaming loader. Therefore
**infrastructure first, data second, capacity third, search last** - and no width increase is
attempted before the profile exists.

---

### ✅ v4.0.0 - Foundation - DONE (2026-07-31, no Elo claim, shipped net unchanged)

**Gate MET.** Streaming reproduces in-RAM training (val 0.052585 vs 0.052003, ~1% apart = shuffle
trajectory noise). Strength provably unchanged: the embedded net is still arch-1 gen7, and a
fixed-depth suite searches 193,746 nodes exactly, before and after. 222/222 tests.

**THE MEASUREMENT - the assertion was wrong.** `nnueprofile` (new, non-UCI) reports isolated
per-primitive costs times real call counts from an instrumented search. gen7 at ft=128/l1=32:

| | share |
|---|---|
| L1 dot product | **26.2%** |
| Feature-transformer row traffic | **73.8%** |

`NnueInference.cs` had claimed for two versions that the L1 dot product was *"THE cost of NNUE
eval"*. It is roughly a quarter - and that claim is exactly what justified keeping the net narrow in
v3.2.0. The width decision had been resting on a cost model that does not survive arithmetic.

Those are the FIRST-RUN numbers, before the accumulator work below. The shipped build reads
**43.4% / 56.6%**, because making the feature-transformer primitives cheaper raised the dot
product's share. Both are correct at their own point in time; expect the second pair when running
`nnueprofile` today.

**Accumulator updates - profile-driven fix, with an honest negative result.** The profile caught
`MoveFeature` at 387 ns for 8 vector additions: `new Vector<short>(array, index)` bounds-checks every
iteration and the JIT does not hoist it. Rewritten with `Vector256.LoadUnsafe` over a ref taken once:
MoveFeature 387.2 → **154.9 ns** (2.5×), Add/Subtract 270.9 → **145.0 ns**, CopyFrom 138.2 → **62.2
ns**, attributed NNUE total -40%. **End-to-end wall time did not move** (730-767 ms across six
alternating runs of both builds, fully overlapping, node counts identical). The bottleneck is
**memory latency on a 5.5 MB feature table accessed near-randomly**, not instruction count. Recorded
as measured, and the profiler now prints this caveat itself so its attribution table is never read as
a promise of what an optimisation will return.

**int8 L1 (architecture 2).** Evaluation **1039.9 → 774.7 ns (−25%)**, NPS 243.7k → 268.7k. Moving
the weights is free - export already clipped them to ±127 while storing int16. The real change is
QA 255 → 127, a CORRECTNESS constraint: `VPMADDUBSW` sums two products into an int16 lane that
saturates, so QA=255 gives 64,770 > 32,767 (wrong) and QA=127 gives 32,258 < 32,767 (exact, always).
Loader refuses arch-2 with QA > 127; exporter re-checks the bound against actual weights (gen7 uses
65.9% of headroom). Arch 1 stays fully supported and re-exporting gen7 as arch 1 reproduces the
shipped payload **byte-identically**. **The embedded net stays arch 1**: QA=127 changes eval enough
to change search (193,746 → 140,008 nodes), which is strength-relevant and belongs behind an SPRT in
v4.2.0, where the net is retrained at QA=127 rather than re-quantised after the fact.

**Accumulator cache (finny table).** Refresh **4407 → 110 ns (40-52× cheaper)**, 99.6% served from
cache, 5.5 rows touched instead of ~32. Keyed by king SQUARE, not bucket: two squares sharing a
bucket are horizontal mirrors whose `Orient()` differs, so a bucket-keyed cache would blend two
feature spaces. At ft=128 refreshes are only 6-7% of NNUE work, so this is insurance for v4.2.0
rather than a speedup today - at ft=1024 the same rebuild is 8× more expensive.

**Streaming dataset - the ceiling is gone.** Features decode once into memory-mapped `.npy` shards
and stream; chunk order shuffled globally, buffers shuffled internally, reads stay sequential.
Leftover rows are CARRIED into the next buffer, not dropped (dropping a partial batch per buffer
loses data every epoch - measured 0.29% with a small buffer). Verified against the in-RAM decode:
**0 duplicates, exact subset of the training range, only the final tail dropped (< one batch), 0
validation leakage.**

**Provenance gate.** Datagen prints an unmissable `PROVENANCE:` line every run, and `--require-book`
turns operator intent into a checked precondition - exits with code 2 immediately instead of 13
hours later. This is the BLOCK 8 failure turned into a machine check.

**Also fixed (pre-existing, would have destroyed a run):** a validation split smaller than one batch
yields no batches → `nan` loss → no epoch "improves" → the checkpoint was written with
`"model": None`, losing the entire training run and only failing later at export.

**Original plan, for the record:**

- **Profile the evaluation properly.** Separate dot-product / FT row traffic / accumulator refresh
  cost. Every subsequent decision depends on this number. Nothing else in this version starts until
  it exists.
- **int8 L1 quantization.** Move activations and L1 weights to int8, using `VPMADDUBSW`+`VPMADDWD`
  (the AVX2 path - the target CPU is Zen+, so no VNNI and no AVX-512). Halves weight bandwidth and
  doubles per-element throughput. Requires a matching change in `export_model.py` and a new
  architecture id.
- **Accumulator cache** ("finny table") for king-bucket refreshes, so a king move no longer forces a
  full recomputation of a perspective.
- **Streaming dataset.** `dataset.py` currently loads everything into RAM behind a 120 M-record cap
  (`train_nnue.py --max-records`). That cap is an architectural ceiling that makes a billion-position
  dataset impossible. Replace with memory-mapped chunked reads and a shuffle buffer.
- **Manifest assertion (regression guard for the BLOCK 8 failure).** The pipeline must FAIL LOUDLY
  when a run claims book seeding and the manifest does not record it. A provenance claim that is not
  machine-checked is a claim that will eventually be false.
- Format version bump; parity test between scalar and SIMD retained as the correctness gate.

### v4.1.0 - Data scale - PIPELINE DONE (2026-07-31), corpus generation pending

**Gate: = 300 M positions on disk, manifest-verified provenance, and a 128-wide control net trained
on it to isolate the data axis from the capacity axis.** The tooling is shipped and verified end to
end; the gate itself needs 2-3 days of datagen, which is machine time, not code.

**The wall that had to come down first.** Feature decoding ran at **13,816 records/s** in a
per-record Python loop - **6 hours for 300M positions, 10 for 500M**, paid again on every change to
the data mix. `decode_block` does the same arithmetic with numpy over whole blocks: **169,366
records/s, 12× faster, 300M in 29 minutes.** The scalar `record_to_features` stays as the definition
of correctness and the vectorised path is asserted equal to it over 50,000 real records (0
mismatches) - a decoder that is fast and subtly wrong would poison every net trained after it.

**Sharded, crash-safe, resumable datagen.** A NOADATA file is only valid once its header is patched
at the end of the run, so a crash at hour 40 of a 3-day run destroyed everything. `--shard-size`
closes each shard as it fills (header patched, manifest written, SHA recorded); `--resume` continues
after the shards already on disk. Shards roll BETWEEN games, never inside one, because records are
game-ordered and the train/val tail cut depends on it. `--positions N` targets corpus size directly,
which is what is actually being specified - `--games` only approximates it.

**`corpus` audit subcommand.** Reports composition by source and verifies each shard off disk
(header vs derived count, schema, manifest presence), samples the label distribution, and warns on
the >20%-zero-scores signature of the old label bug. Opens shared, so a corpus can be audited while
a datagen is still writing it. Run against the existing data it states in one line what took five
generations to find: **84,697,234 positions across 7 datasets, every one `openings=8-9 random
legal`.**

**`Noa-DataScale.ps1`** runs the campaign in phases 0→4. Phase 1 mixes 45% bulk self-play / 20%
opening seeds / 35% middlegame seeds and passes `--require-book` on every seeded arm. Phase 3 trains
at **width unchanged**, which is what isolates the data axis - changing width and data together is
exactly what made block 8 uninterpretable.

#### ✅ PHASE 0 MEASURED (2026-08-01) - the premise held, and by a landslide

Two arms matched on **total search work**, not position count (at 28k nodes a position costs ~4.7×
what it costs at 6k, so equal counts would have meant 32 hours against 7 - and would have answered a
question nobody asks, since the campaign gets a compute budget rather than a position quota):

| arm | positions | nodes/move | node-work |
|---|---|---|---|
| `fast6k` | 20,001,946 | 6,000 | 1.2e11 |
| `deep28k` | 4,288,192 | 28,000 | 1.2e11 |

Identical architecture, hyperparameters and teacher. **Result: `fast6k` +182.2 ±16.6 Elo, LOS 100%**
(1167-307-312 over 1786 games, 10+0.1). **The campaign runs at 6,000 nodes - confirmed, not assumed.**

**The magnitude matters more than the direction.** +182 Elo is not "deep labels add little": it is
that **4.3M positions cannot train this net at all**. The feature transformer alone holds
22,528 × 128 ˜ 2.9M parameters, so the deep arm had ~1.5 positions per parameter and was broken from
the start. This is the strongest confirmation yet that the network is starved of DATA rather than of
label quality - and it explains retroactively why gen3 through gen7 landed flat while node counts
were being raised 14k → 20k → 24k → 28k. Every one of those generations tuned the axis that does not
matter at this size.

**It does NOT prove 6,000 is optimal**, only that it beats 28,000 decisively. Both arms sit deep in
the starved regime, so the slope cannot be extrapolated to the 300-500M range where returns must
diminish. A third arm at 3,000 nodes / 40M positions (same total work, ~7h) would say whether 6,000
is the plateau or still climbing - that choice doubles or halves the campaign corpus for the same
hours.

**Original plan, for the record:**

- **Drop labelling depth to 5,000-8,000 nodes.** At this network size quantity beats label quality by
  a wide margin, and the current 28,000 nodes buys almost nothing (+3.7 Elo measured). This is
  roughly 5× more positions per hour of machine time.
- Target **300-500 M positions**, mixing: bulk self-play, book-seeded openings (`human.fens`,
  now actually passed), middlegame seeds (`human_mid.fens`, ply 20-40), and the elite WDL-anchored
  set (`elite_wdl.fens`, ~9.8 M positions carrying real human game outcomes - the one signal in the
  entire pipeline the engine cannot manufacture for itself).
- The 128-wide control net answers the question BLOCK 8 failed to answer: **does data alone move
  strength?** Whatever it measures is honest, because the manifest now proves what went in.

### v4.2.0 - Capacity - ARCHITECTURE DONE (2026-08-01), nets pending

**Gate: SPRT vs v4.1.0 at the real time control, with NPS reported alongside Elo.** The architecture,
the cross-language verification and the width measurement are shipped; the nets themselves need
training. Strength unchanged so far: **exactly 193,746 nodes**, the same as v4.0.0 and v4.1.0.

**Output buckets (architecture 3).** The head is replicated per bucket, selected by piece count, so
the net gets a per-phase readout instead of one linear map serving a 32-piece opening and a 4-piece
ending alike. **Almost free at runtime**: only ONE bucket is evaluated per call, so per-evaluation
arithmetic is exactly the arch-2 cost; only the head's weight table grows, from 16 KB to 128 KB
against a 5.5 MB feature transformer. That ratio is why buckets ship before any width increase.
Selection is `clamp((pieceCount - 1) * buckets / 32, 0, buckets - 1)`, defined in exactly one
function per language.

**Verified across the language boundary, not assumed.** Bucket formula: C# golden values asserted in
tests, Python run against the same table, identical everywhere and in range over piece counts 0-40 ×
1-16 buckets. End-to-end: a Python-trained bucketed net exported as arch 3, evaluated on three
positions spanning buckets 7/0/5, gives **18 / 80 / 62** in the engine and **18 / 80 / 62** from the
new `verify_export.py`, which reproduces the integer forward pass from the exported FILE. Backward
compatibility: gen7 re-exports as arch 1 **byte-identically** (sha `3c7e94a9…`).

**Pricing width without training anything.** The cost of a width is a property of the SHAPES, not of
the weights, so `nnuewidth` synthesises shape-accurate nets and times them - the cost curve in
seconds instead of days. Preliminary, on a loaded machine:

| ft | eval | vs 128 | accumulator move | vs 128 |
|---|---|---|---|---|
| 128 | 898.6 ns | 1.00× | 28.6 ns | 1.00× |
| 256 | 1361.1 ns | **1.51×** | 44.6 ns | 1.56× |
| 512 | 2370.0 ns | **2.64×** | 83.8 ns | 2.93× |

Cost rises **sub-linearly**: doubling the transformer costs ~1.5×, not 2×, because packing and the
output layer do not scale. A materially better trade than the "wider is counterproductive"
assumption v3.2.0 rested on. The first version of the sweep reported 256 as FASTER than 128, which
is impossible; the estimator now takes the minimum of five repetitions and the report prints its own
sanity check, because a table that does not rise with width is noise and must not be believed.

**Buckets are measurable NOW, on existing data.** Width needs the new corpus; buckets do not, since
they add head capacity rather than input capacity and 84.7M positions already fits eight small
heads. `Noa-Buckets.ps1` trains two arms differing ONLY in `--out-buckets` (1 vs 8) and
`sprt_buckets.bat` plays them off. Both arms are trained fresh rather than reusing gen7 as control,
which would confound buckets with gen7's different hyperparameters.

**Original plan, for the record:**

- **Widen FT 128 → 512 → 1024**, one step at a time, measuring NPS at each. Stop where the
  strength-per-NPS curve turns over - but measure it, do not assume it as v3.2.0 did.
- **8 output buckets** selected by piece count. Only the final head is duplicated (32→1 per bucket),
  so the runtime cost is negligible and the specialisation is real. Best effort/reward ratio in the
  whole campaign: roughly 90 lines across `model.py`, `export_model.py` and `NnueInference.cs`.
- Deeper head with squared clipped activation if the profile permits.
- Re-freeze the C#↔Python contract at this point, parity-verified as before.

### v4.3.0 - Search

**Gate: the BLOCK measured as a block, then ablated. Not one SPRT per term.**

- **Complete the correction histories.** Only `PawnCorrectionHistory` exists today. Add minor-piece,
  major-piece, non-pawn (per colour) and continuation correction histories. Cheap tables, the
  pattern is already implemented once.
- **THE "COUPLED BUNDLE" PLAN WAS WITHDRAWN 2026-08-01, BEFORE ANY MACHINE TIME WAS SPENT ON IT.**

  The plan said: re-enter statScore in LMR, cutNode, double extensions and multi-level continuation
  history *as a bundle*, on the argument that each is worth only +2-5 Elo alone - below the
  resolution of an 8,000-game SPRT - and that they are worth more together.

  **The argument does not survive the evidence already in this repository.** These were not
  sub-resolution results:

  | Feature | What was actually measured |
  |---|---|
  | statScore in LMR | **Three** variants against v2.8.3-class baselines: -18 Elo (H0); -4.8 ±11.4 (LLR -2.89, H0); +4.2 ±9.1 flat over 3000 games. `AlphaBetaSearch.cs` records the conclusion: *do not re-add without a new mechanism; the direct form is settled* |
  | Double extensions (5E) | **Four** SPRTs, all at or below equality, -19.7 / -12.5 the worst |
  | Multi-level continuation history (5G) | **Four** builds: −33.9 → −10.9 → [0.496] → −4.2. Per-distance tables, gravity and the depth≥6 gate were all built and tested |
  | cutNode | Isolated term cut at both magnitudes, -4.0 / -7.1 (H0) |

  A supporting premise was also wrong: the plan assumed statScore had failed because the butterfly
  table was miscalibrated and that v2.8.3 later fixed the calibration. It did not - those three
  measurements were taken **after** the gravity fix.

  **The coupling rule itself remains sound as a general principle** (a genuinely sub-resolution term
  cannot be measured alone), but it was applied here to features that were cut for being clearly
  negative, with root causes identified at the time. Building them would have burned days of machine
  time against evidence that already existed.

- **What IS still defensible, as a targeted change rather than a bundle:** 5G's final result was
  exact equity, and its root cause was identified - *the hard killer/counter ordering bands sit above
  all history*, so no history refinement below them can express itself. Addressing those bands is the
  "new mechanism" the code demands before history-in-ordering is retried. One coherent change with a
  stated mechanism, not four resurrections. Still speculative; still needs its own SPRT.

### Deferred within this campaign (deliberately not in the critical path)

- **Delete the classical evaluator** (~2,300 lines across `ClassicalEvaluator`, `EvaluationParams`,
  `PawnStructureEvaluator`, `PieceSquareTables`, `Winnable`, `MaterialImbalance`). Worth **0 Elo
  directly**; the value is focus and test surface, and it removes the standing temptation to keep
  tuning a superseded path (see §FINAL REVIEW, which still holds King Safety Phase B and
  KingProtector "for after NNUE" - this closes that door). Costs nothing at runtime today and is
  the only independent sanity check on NNUE output, so it is scheduled **last and opportunistically**,
  never as a blocker.
- **Speed work beyond the NNUE path.** The v2.8.4 investigation found the engine ~50% eval-bound;
  the other ~50% is search and movegen and has never been profiled. Worth revisiting only once
  v4.2.0 lands, and **not** via native interop - see "What NOT to try again".

### Expected outcome

| Version | Lever | Expected Elo |
|---|---|---|
| v4.0.0 | Foundation | **0** (unlocks the rest) |
| v4.1.0 | Data scale | +80 to +150 |
| v4.2.0 | Capacity (width + buckets) | **+150 to +300** |
| v4.3.0 | Search bundle | +60 to +110 |

Realistic destination: **~3080 → 3300-3450 CCRL.** The overwhelming majority of the remaining gap
to reference-class strength lives in the network and the data, not in the search and not in the
implementation language.

---

## BLOCK 10 - Competition Opening Book (v4.9.0 - DEFERRED, was v4.0.0)

**Status: DEFERRED behind BLOCK 12 (2026-07-31)** - the v4.0.0 slot now belongs to the NNUE
architecture overhaul, which is where the measurable Elo is. A competition book remains worth
roughly nothing against the project's primary metric (see the order note below) and should not
consume machine time or attention until the v4.x campaign has run its course.

**Order Note (2026-07-18): The reference does not have its own book** - in CCRL/TCEC, tournament-neutral books are used, so a custom book does not count towards our main metric. Furthermore, a competition book is tuned against the FINAL engine's strength profile (with NNUE); building one beforehand would require re-tuning. What is needed before NNUE is the SEED position book for the datagen (UHO style) - that lives in 6B, not here.

### Philosophy

NoaChess's book **doesn't seek variety-it seeks to win**. If one variation scores better, the better one is always played. Fun or variety are secondary to the result.

### Option A - Existing Polyglot Book (implement first)

- Format `.bin` Standard polyglot, ICU compliant.
- Load into memory at startup; probe by Zobrist position hash.
- Play selection: **always the one with the highest weight** (not random). Randomness only occurs between plays with identical weight.
- Sources: `Performance.bin`, `komodo.bin`, `gm2600.bin` (GM games).

### Option B - Custom book from databases (greater competitive advantage)

- Download Lichess database of ELO 2400+ games (PGN format, ~50M games).
- Extract positions up to movement 20 with result.
- Calculate for each position and move: `peso = frecuencia × (win_rate - 0.5 × draw_rate)`.
- Export in Polyglot format with these weights.
- Result: an extremely deep book on popular high-level openings.

**Recommendation:** Option A first (1 day of implementation). Option B if the engine reaches serious tournaments.

### Option C - Learning overlay on top of the base book (later refinement)

- The base `.bin` (Option A or B) stays immutable - it is never rewritten after a game.
- A separate mutable overlay tracks `games / wins / draws / losses / lastUpdated` per `(position hash, move)`, populated from the engine's own played games (bot included).
- Move selection uses `effectiveWeight = baseWeight + boundedLearningAdjustment`, with an exploration floor so a bad early sample can't permanently kill a move.
- Requires atomic writes, a versioned overlay format, and corruption recovery (this is exactly the kind of file the Lichess bot process could otherwise corrupt on a crash).
- Only worth building once Option A/B is live and actually generating enough self-played games to make the overlay statistically meaningful.

---

## BLOCK 11 - Strength Extras (v4.5.0+)

**Status: RESERVED · To be determined upon arrival**

- Additional search improvements that the reference incorporates between now and then.
- Possible migration to HalfKAv2-hm (NNUE architecture richer than HalfKP-256).
- Additional refinement of time management (full adaptive factors: fallingEval, timeReduction, complexPosition) if version v2.6.4 does not include them all.
- Additional speed optimizations if NPS falls behind.
- **Competition profiles (Bullet/Blitz/Rapid/Classical)** - a single `Search`, but with an immutable `EngineProfile` selected per game, each with its own tuned time management (base allocation, increment, move overhead, panic extension) and search parameters (LMR/LMP/aspiration/null-move thresholds). Requires a separate SPRT per profile - Elo from different pools is not comparable. Directly relevant to the Lichess bot, which already plays a lot of bullet.
- **Reproducible release manifest** - a JSON emitted per build with `engineVersion`, `commit`, `runtime`, `model {file, sha256}`, `book {file, sha256}`, `searchProfile`, `build {rid, aot}`. Cheap to implement, and would have prevented the naming-confusion incidents around exe folders (e.g. `NoaChess-3.1.2-NNUE-0.7-gen7net`) by making "what exactly is in this build" self-describing.
- **Deterministic bench with checksum** - an internal `bench` command reporting `nodes / time / NPS / final checksum` over a fixed position set, so an accidental move-ordering regression between versions shows up as a checksum mismatch instead of only a silent Elo loss. Complements the existing move-ordering measurement rule (needs real match positions, not a handful of hand-picked ones - see memory `bench-width-for-ordering.md`).

---

## Permanent technical decisions

### What NOT to try again

- **Rewriting hot blocks in C++ via interop** (raised 2026-07-31, rejected without measurement being needed). Four reasons, in order of weight: (1) the single-executable requirement is a standing project decision - Syzygy was written as a ~1250-line managed port specifically to avoid a native DLL; (2) the NNUE hot path already runs AVX2 intrinsics through `System.Runtime.Intrinsics`, and RyuJIT emits the same machine instructions MSVC would, so the theoretical gain there is ~0; (3) a managed→native transition per leaf evaluation costs more than it saves unless the whole search moves, which is a rewrite and not "certain blocks"; (4) the arithmetic - roughly +50-70 Elo per DOUBLING of NPS means a generous 15% gain is worth ~+10 Elo, against a campaign (BLOCK 12) worth 10-30× that. If native codegen is ever genuinely wanted, the answer that preserves every constraint is **NativeAOT**, measurable with a fixed-node bench in an afternoon and with no architectural risk. What would reopen this: a profile identifying a specific hot loop where RyuJIT codegen is measurably and substantially worse.
- **More self-play generations at ~13 M positions per generation** (retired 2026-07-31). gen3 → gen7 landed flat; deepening labels 24k → 28k nodes bought +3.7 Elo. The limit is network capacity and dataset volume, not label depth or label provenance. Superseded by BLOCK 12.
- **King safety with safe checks without strict masking** - attempted on v2.4.6, -77 Elo. The coverage mask must include ALL defenders. See memory `king-safety-fase-b-cut.md`.
- **Tuning mobility with Texel Tuner** - spurious EG signal, converges to negative values. Use reference values ​​directly.
- **Multiple terms in a single SPRT** - if it fails, you don't know which one is the culprit. One term = one SPRT, always.
- **Playing against a strong engine to generate NNUE data** - unbalanced matches + bad tags.

### SPRT Decision Rules

- SPRT H0 → discard term, document in memory, do not retry before the next larger block.
- SPRT H1 → optional precision bump, commit, gauntlet version to fine-tune the measured Elo.
- Gauntlet current field: 7 rivals 2580-2788 CCRL, tc=60+0.6, rounds=28.
- **Field Upgrade:** when NoaChess exceeds 70% score → raise opponents to 2750-2950 CCRL.

### CPU (Threadripper 2950X, Zen+, 0x17 family)

- Microcoded PEXT → slow. CPUID guard active on `ComputeUsePext()` since v2.5.0.
- AVX2 supported and fast → use for NNUE inference (vectorized SIMD).
- 16 cores / 32 threads → Lazy SMP with `Threads=16` It will take the biggest leap in real-world gaming.
- No AVX-512.

---

## 🔁 FINAL REVIEW - terms cut to be rescued **AFTER NNUE (block 6)**

**Order decided on 2026-07-23.** Previously, this section stated "at the end of the classic eval block (before NNUE) or after the first global retune texel." **They now come after block 6.** The reason:

- **Search survives the NNUE; evaluation does not.** These two are terms WITHIN the classic evaluator, and the integration plan (§6, blending) leaves the classic as a marginal fallback - the reference only consults it with more than 7 pieces AND `|psq| > 1760`Anything refined here will no longer be consulted.
- **Their documented diagnosis is "conflict with existing tuning," not "misbehaved."** The prescribed remedy is a **global Texel retune**, which is the most expensive job on the list and whose output is parameters from the classic evaluation.
- **Its track record is the worst in the project**: Phase B failed three times (-77, 0, -13) and KingProtector poisoned the game at long TC. Lowest expected value per machine hour.

**Acknowledged counterargument:** The data generator uses the current engine for labeling, so a better evaluation would yield better labels. This is weakened by the fact that tablebases already re-label the noisiest part of the dataset and that the labels come from the SEARCH score, not from static evaluation.

**If you want to invest in eval before NNUE, what does make up is the global retune texel by itself** - it improves all terms at once and is also a prerequisite for these two.

| Term | Cut in | Evidence | Rescue route |
|---------|-----------|-----------|-----------------|
| **King Safety Phase B** (shelter/storm/safe checks complete) | v2.4.6 | −77 → 0 → −13 in three attempts | POST-NNUE. Let the network learn King Safety on its own; only if the classic is still alive, re-evaluate after global retune |
| **KingProtector** (4E) | v2.6.5 | Poison to LTC on PSTs PeSTO | POST-NNUE. Only with PSTs re-tuned together (full texel roll) |

---

## Version history

| Version | Description | Elo SPRT | Elo CCRL est. |
|---------|-------------|----------|---------------|
| 2.3.0 | Search (cont-hist, singular, LMR, IIR, ProbCut) | +91 ±34 | ~2640 |
| 2.4.0 | Eval base + texel tuning | +13 Elo | ~2680 |
| 2.4.5 | Tempo + phalanx + backward | +12 Elo | ~2710 |
| 2.5.0 | Staged movegen + lazy legality + PEXT | +101 Elo | ~2833 |
| 2.6.0 | attackedBy infra (prereq) | - | - |
| 2.6.1 | Threats | +103 ±35 | ~2775 |
| 2.6.2 | Non-linear mobility | +6.6 ±11.5 (LOS 87%) | **2780 ±20 measured** |
| 2.6.3 | Shelter/Storm + King Safety | +76.9 ±31.2 (LOS 100%) | **2800 ±25 measured** |
| 2.6.4 | Adaptive Time Management | without SPRT (see note) | **2875 ±20 measured** |
| 2.6.5 | Piece terms (TrappedRook, bishop, WeakQueen, etc.) + timeman ref | +19.5 ±13.6 | **2835 ±25 measured** (re-anchored field) |
| 2.6.6 | Passed reference pawns (definition + filter + proximity + path) | +45.8 ±23.1 | **2880 ±25 measured** |
| 2.6.7 | Pawn structure chain (Complete Connected, WeakUnopposed, WeakLever, DoubledEarly, BlockedPawn) | +28.4 ±17.5 | **2895 ±25 estimated** |
| 2.6.7.1 | Timeman patch (opening brake) + UCI ponder/infinite fix (Arena freeze) | +14.3 ±13.5 | **~2920 ±20 measured (exact CCRL pace)** |
| 2.6.8 | Material imbalance polynomial (Romstad) + retune set of part values ​​+ bullet guardrail | +78.4 ±31.5 (LOS 100%) | **~2944 ±15 measured** |
| 2.6.9 | Winnable / scale factors (complexity, almostUnwinnable, OCB, rook endgames, no queen, material factor without pawns) | +34.3 ±19.5 (LOS 100%) | **~2941 ±25 measured** |
| 2.7.0 | Improving flag (LMR/RFP/LMP; NMP → 5B) | +4.0 ±27.1 STC (standing 380g) · **+43 ±23 rel LTC** | **~2965 ±25 measured** |
| 2.7.1 | NMP ≥14 verification + fail-soft + statScore on RFP (full bundle of ref. knocked down by SPRT and dissected: R→post-qsearch-checks, eval input→NNUE, futility→5C, captFut→5G) + mate fixes (ID not cut on mate, UCI score mate) | +2.9 ±7.4 grouped · +44 ±23 rel LTC | **~2970 ±25 measured** |
| (5C) | ❌ CUT - suite LMR + statScore 4-comp: all negative to 10+0.1 (−9.7/−25.7/−11.5/−10.8); fix contHist ply2/4 archived → 5G | - | - |
| 2.7.2 | 5D TT redesign (5F era): 4×16B clustering, aging, cached eval, ttPv | +37.9 ±15.0 grouped · +48 ±23 rel LTC | **~2975 ±25 measured** |
| (2.7.3) | ❌ CUT WITHOUT RELEASE - 5E single campaign (4 negative SPRTs) + 5G multi-level story (4 builds, the last 2 exact equity; below tables-by-distance/gravity/gate built, blocking = hard gang killers/counter); both closed | - | - |
| 2.7.4 | **Quiescence Rework (FIX)**: In check without stand-pat, all moves, zero pruning, mate detected; stalemate guard, fail-soft, 4 promotions, reference pruning block (futility 147, SEE -36, capture history); **fixes root hang** with mate/stalemate always present | -2.1 ±9.9 SPRT (H0) · +52 ±23 rel LTC vs +48 | **~2975 ±25 NO CHANGE** |
| 2.8.2/5F | ✅ ProbCut reimplemented with normal depth>=1 verification; A/B isolated 59-51-90 | +13.9 ±35.8, LOS 77.7% | Included in full match |
| 2.8.0 | **Block 9: Syzygy - DONE** (native C# port, NOT Fathom/P-Invoke: no C compiler, and a DLL would break the single executable). WDL in search + DTZ filtering at root + 4 UCI options | SPRT pending | 3000 finals verified with no discrepancies |
| 2.8.1 | Critical fixes Syzygy + capture history/partial quiet sort/threats | +14.1 ±10.8 SPRT · +75 ±23 rel LTC | **~3000 ±25 CCRL** |
| 2.8.2 | Final classic audit: pawn correction, ProbCut verified, fixed initial aspiration + recentered fail-low, severity with killer/counter bands, no check extension, hardened log UCI | **+28.0 ±17.2, H1 at 834p** | **~3013 ±30 CCRL (624p LTC, +94 ±23 relative)** |
| 3.0.0 | **Block 6: NNUE - DONE.** HalfKAv2_hm (schema 2, kings as features), AVX2 SIMD inference, incremental accumulator, datagen with Syzygy adjudication, 57%→2% tag bug fixed, generational self-play (gen2 +1.9, gen3 +4.5 Elo vs classic) | **gen3 +4.5 ±11.4 vs classic, positive exhausted 2650p, LOS 77.8%** | LTC gauntlet pending |
| 2.8.0 | **Syzygy tablebases (ADVANCED - reference order: TB before NNUE)** - perfect evaluation in-game + datagen adjudication and relabeling | TBD | +final game |
| 3.0.0 | NNUE production (HalfKP-256; datagen with seed book of positions + Syzygy re-labeling) | TBD | ~3150+ |
| **3.1.0** | **Lazy SMP (16 threads) - ✅ DONE** - `Threads=1` byte-identical, node scaling ~7.6× to 8 threads | LTC gauntlet pending | ~3150+ expected |
| **3.1.1** | **Cold-start patch - ✅ DONE** - `PublishReadyToRun` AOT: start ~25 s → ~7 s; warmup NNUE depth 6 → 1. No change in strength | - | = **~3050 CCRL** |
| **3.1.2** | **Time Fix - ✅ DONE** - cap at mid-iteration to 1 thread + easy-move to \|score\|≥700cp; 8.5s→1.68s in decisive final to 5+5 | SPRT −5.0 ±27.7 (neutral); published by direct measurement | = **~3050 CCRL** |
| **3.2.0** | **NNUE gen7 + human openings (block 8) - ✅ DONE** - `pgnbook` Extracts 3.04M elite FENs and seeds the datagen | vs gen5 +3.7 (parity); vs classic +28.5 LOS 100% | **~3080 ±40 CCRL** |
| **3.2.1** | **Bot Stability - ✅ DONE** - depth stop in ponder + STALL visible; `Threads` 30→24 in the bot config | no strength change | = ~3080 |
| **3.3.0** | **Cut by matte tested - ✅ DONE** - matte at 1: 1074 ms → 22 ms. NNUE scale **CUTTED** (−61.7, H0) | +3.3 ±23.1 (523p, neutral); published by behavior | = ~3080 |
| **3.4.0** | **Block 11: Elite Data - Infrastructure DONE, Data in Generation.** Two Independent Paths: **(C)** Middlegame Seeds (`pgnbook --min-ply 20 --max-ply 40`) so that self-play starts where games are decided, not just in openings - it didn't need code, the flags already existed; **(C+)** **elite WDL anchoring**: `pgnbook --with-result` writes `FEN;R` and the new mode `datagen --label-book` It labels each position with **(score from our search, ACTUAL result of the human game)**, without self-play. This is the only pipeline signal the engine cannot generate on its own: the self-play WDL is its own opinion played to the end, which is why the lambda sweep found it useless (0.750 → 0.338). **This is NOT learning by imitation**: the human contributes neither evaluation nor play, only the position and who won. The manifest records `mode` and `wdlSource` so that a labeled dataset is never confused with self-play | TBD | - |
| **4.0.0** | **BLOCK 12 - Foundation - ✅ DONE.** Measured the cost model and overturned it (L1 dot 26.2%, FT row traffic 73.8%). int8 L1 arch 2 (eval −25%, QA→127 forced by the VPMADDUBSW saturation bound, arch 1 still byte-identical). Accumulator cache 40-52× cheaper refreshes. Accumulator updates 1.9-2.5× faster in isolation with NO end-to-end change - the bottleneck is memory latency, not instructions. Streaming dataset removes the 120 M-record RAM ceiling. Provenance gate (`--require-book`) turns the BLOCK 8 failure into a machine check. Pre-existing checkpoint-loss bug fixed | **no Elo claim** - gate MET (streaming val 0.052585 vs 0.052003; 193,746 nodes unchanged) | = ~3080 |
| **4.1.0** | **BLOCK 12 - Data scale - PIPELINE DONE, corpus pending.** Vectorised feature decoding (12×: 6 h → 29 min for 300M) removed the wall; sharded/resumable datagen removed the multi-day crash cliff; `corpus` audits provenance off disk; `Noa-DataScale.ps1` phases 0→4 with phase 0 testing the volume-beats-depth premise before days are spent | infrastructure only, no Elo claim yet | +80 to +150 expected once the corpus is built |
| **4.2.0** | **BLOCK 12 - Capacity - ARCHITECTURE DONE, nets pending.** Output buckets (arch 3, head replicated per bucket, only one evaluated per call), cross-language verification (`verify_export.py`, exact agreement 18/80/62 across buckets), and `nnuewidth` which prices width from shapes alone: **256 = 1.51×, 512 = 2.64×, sub-linear**. Buckets measurable now on existing data via `Noa-Buckets.ps1` | architecture only, no Elo claim yet | **+150 to +300 expected once trained** |
| **4.3.0** | **BLOCK 12 - Search (PLANNED).** Complete correction histories (minor/major/non-pawn/continuation - only pawn exists today), then re-enter statScore, cutNode, double extensions and multi-level continuation history **as a bundle**, per the golden coupling rule | TBD | +60 to +110 expected |
| 4.9.0 | Competition Opening Book - DEFERRED behind BLOCK 12 (the reference does not have its own book; tournaments use neutral books, so it does not move the primary metric) | - | tournament |
| 5.0.0+ | Strength Extras (competition profiles, reproducible release manifest, deterministic bench checksum) | - | - |
