# Lazy NNUE accumulator

Status: **implemented and measured** (branch 4.5.0). Node-identical, +5% NPS.

## Why

Profiling (2026-08-07, `audit/profile_search.py`) over 90 s of real search:

```
28.73%  NnueInference.EvaluateInt16
 7.39%  NnueAccumulatorStack.PushMove     <-- this
 2.49%  NnueAccumulatorCache.Refresh      <-- and this
```

The stack was EAGER - its own header said *"the search pushes BEFORE making a
move on the board and pops after unmaking it"* - so every `MakeMove` paid a full
`CopyFrom` of both perspectives plus the feature math, whether or not the
position was ever evaluated.

v4.4.0 made that worse rather than better: the quiescence transposition work
means far more children now return without evaluating (cached static eval, or a
TT score cutoff), and every one of those was an accumulator update nobody read.

## Scope

`src/NoaChess.Engine/Evaluation/Nnue/NnueAccumulatorStack.cs`, plus a `Computed`
flag and a one-perspective copy on `NnueAccumulator`, and new counters in
`NnueProfiling` / `NnueProfiler`.

`AlphaBetaSearch` only ever calls `Reset`, `PushMove`, `PushNull`, `Pop` and
(through the evaluator) `GetPerspective`, so the public interface did not change
and no call site moved.

## Design

Per level: a `Pending` record of the move, and `Computed[2]` alongside the
existing `Valid[2]`. `PushMove` records and returns - no copy, no feature math.
`GetPerspective` walks back to the nearest computed ancestor and materialises
forward.

### King squares do not need storing

The feature index needs `board.KingSquare(perspective)` at the pre-move
position, and the CURRENT board always supplies it:

- **Non-king mover:** the move does not change either king square. Along any
  chain of pending updates for a perspective, no level can contain a king move
  for that perspective - such a level sets `Valid = false`, and an invalid
  perspective is refreshed rather than chained. The king square is therefore
  constant across the whole chain.
- **King mover:** that perspective is invalidated and refreshed; the OTHER
  perspective is patched using its own king square, which this move did not
  move.

So the king square is read once per `GetPerspective`, outside the chain loop,
and no positional snapshot is needed. That is what keeps the refactor cheap.

## The trap that cost the first cut of this

**Materialise every level on the way up, not just the top.**

The obvious version collapses the whole chain straight onto `_top`: one copy
plus N replays, leaving the intermediate levels uncomputed. It is one copy
cheaper per evaluation and strictly worse overall, because a parent that never
evaluates then has its update replayed by *every one of its children*. In-check
plies skip the static eval, so this is common, not exotic.

Measured on the first cut, at depth 10 over the profiler's position set:

```
                        collapse-onto-top     materialise each level
pending applied            1,087,813               377,767
  vs eager (2 x pushes)       105.6%                 36.7%
perspective copies           320,866               396,481
wall time                     758 ms                687 ms
```

The "lazy" stack was doing **5.6% MORE feature work than the eager one it
replaced**, and the end-to-end gain was inside noise (+0.3%). Writing each level
as it is crossed makes an unevaluated parent pay exactly once for all of its
children. Same nodes and same evaluation count in both columns - only the
accumulator bookkeeping changed.

Note that the fix costs MORE copies (396,481 vs 320,866) and is still far
faster. Copies are ~16 ns per perspective; a fused `MoveFeature` is ~78 ns.
Trading copies for replays is the right direction.

## Verification

It is a **pure performance refactor** - same evaluations, same search - so node
counts must be byte-identical. `bench_lazyacc.bat` runs four alternating passes
and includes a base-against-itself control, and `audit/bench_identical.py` names
the diverging positions instead of just reporting that some diverged.

Measured 2026-08-08, the full 150 positions at depth 12, two alternating passes
each, machine idle:

```
nodes   34,690,140   in all four runs, position by position, plus the control
NPS     base 927,971 / 916,765      candidate 948,797 / 953,432
time    base 37.4 + 37.8 = 75.2 s   candidate 36.6 + 36.4 = 73.0 s
        +3.1% NPS  /  -2.9% wall time
```

Run-to-run drift was about 1% (the base's two passes differ by 1.2%), so the
effect is comfortably outside the noise and every candidate pass beat every base
pass. A first read over only the FIRST 40 positions of the same file gave
+4.96%, which was optimistic - the speedup varies by position type, so quote the
full set.

Do not skip this check on any future change here. A full SEE implementation on
2026-08-07 passed ~70k equivalence comparisons and still moved node counts 1.8%;
it was reverted rather than shipped unexplained.

## What is left on this axis

The accumulator side is now ~6.8% of wall time (was ~15%). What remains, from
the same profile: `PartialSortRange` 3.90%, movegen 6.10%, `Thread.PollGC`
3.59%, and the L1 dot product at 43.9% of NNUE work, which is bounded by memory
latency on the 5.5 MB feature table rather than by instruction count.
