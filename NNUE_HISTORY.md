# NNUE Training History

Generational self-play pipeline. Each generation's datagen uses the previously
promoted net as teacher; the training data accumulates across generations.

---

## 2026-08-31 - the coarse-threat pipeline closes end to end; fqhuman in flight

Two training-side builds between releases, neither an Elo claim yet.

**The coarse-threat lane (gate 2b) is complete and parity-proven at every
joint.** The probe had already said the 144-bucket aggregate encoding
carries as much signal as the fine set (+4.14% of validation loss vs
3.96%) at 1/400th the dimensionality; what was missing was everything
between the probe and a playing engine. Now built: a C# shard encoder
(7 s per 5M-position shard, 3000/3000 record parity against the probe's
own enumeration - the fine set's target filters, the blocked-pawn
relation, multiplicity kept), 191 CSR companions (16 GB - the companion
format IS the CSR cache, the same trick that saved the fine cache from
1.24 TB), a trainer lane mirroring the threat transformer (same
accumulator, same QAT grid, smoke-trained green), the export block
(payload flag 1<<2, 144 x ft_out int16 appended last), the engine loader
and an evaluation-time lane that sums the relation rows into LOCAL copies
of the accumulators - per evaluation, never per node, which is exactly
the cost mechanism that killed the fine set at the clock. The chain ends
with exact integer parity: verify_export and the engine agree to the
centipawn on both contract positions. The real net trains as soon as the
GPU frees; its fixed-node SPRT against the champion decides whether the
banked +60.4 of threat content finally converts.

**fqhuman is training**: the champion recipe verbatim on the full ~924M
corpus - the 594M fq594 saw plus the human opening and middlegame
segments the corpus program just finished. It answers the oldest open
question of the campaign (does human-position seeding pay?), never
actually measured because of the gen7-era provenance bug. SPRT against
fq594 on export.

## 2026-08-30 - fq594 ships as v5.3.0: volume, measured alone, wins

**+29.7 Elo [+12.2, +47.3], LLR +3.13, H1 over 763 fixed-node games against
fqmix**, with the trend rising at the close (the last 160 games near 59%).
The recipe is fqmix verbatim - factorized + QAT, no transformer weight decay,
the refit reference-style loss (240/145), lambda 1.0 to 0.7, 60 epochs, seed 1 -
on the corpus extended from 324M to ~594M positions: the 70 inherited shards
plus 54 new random-opening bulk shards of identical provenance (gen7 teacher,
6,000-node labels, engine 5.0.2.1). One variable moved. Volume had never been
measured alone at this scale - the -108.6 that once seemed to close the data
axis had moved three variables at once - and it turns out to be worth the
second-largest net gain of the project after factorization. Training took ~54
wall-clock hours sharing the GPU with datagen; validate/export/verify all
green, engine-side parity exact on both contract FENs (54 / -137 cp).

Held out on purpose: the corpus program's human segments (openings done,
middlegames still generating at the time). They are the NEXT arm - mix at
comparable volume, one variable again - and close a historical debt: the
human-seeding hypothesis from the gen7 era was never actually tested (the
provenance bug: every manifest said random).

The gauntlet moves to a stronger field (mean 3337) and anchors **3317 +-44
CCRL (47.3% over 240)** - a series break by design, with four bridge anchors
shared with the old field running 3334-3451 per-opponent. Both bots run it,
hash-verified, previous binaries kept as rollbacks.

## 2026-08-29 - fqmix ships as v5.2.0: the axes add

**+19.6 Elo [+6.4, +32.9], LLR +3.20, H1 over 1,295 fixed-node games against
fqwd0**, the shipping champion, with a rising trend (the last 300 games at
58%). The three arms measured one at a time on the fq60 recipe - fqwd0 +11.1,
fqloss2 +11.9, fqlam +5.3 unconcluded - stack rather than overlap; over fq60
that is roughly +30 in a week, all of it training-side. The gauntlet anchors
**3342 +-86 CCRL (81.7% over 240 games)**, with the honest caveat that the
12-engine field is saturating: 55% against its strongest member at 3281, six
opponents at 90% or better. The absolute series needs a stronger field from
here on; the SPRT remains the instrument for deltas. Both bots run it,
hash-verified, previous binaries kept as rollbacks.

Same day, the psqt verdict (entry below) and the launch of **fq594**: the fqmix
recipe verbatim on the 70 inherited shards plus the 54 new bulk shards - ~594M
positions, teacher net and 6,000-node label depth held fixed so volume is the
only moving axis, epochs held at 60 by series convention (which knowingly mixes
volume with training compute, as the told-vs-fq60 row did). The corpus program's human
segments (openings and middlegames, still generating) stay out of this run,
and the interrupted open.0010 shard is excluded outright. About 27 hours; SPRT against fqmix.

## 2026-08-28 - the loss axis runs at last, and wins

**fqloss2: +11.9 Elo [+2.6, +21.2], LLR +3.08, H1 over 2,832 fixed-node games
against fq60.** The exact fq60 recipe with only the loss changed: symmetric
win-probability loss, separate scales for net and label, exponent 2.5 - and the
constants REFIT to this corpus by logistic regression on its own (score, result)
pairs: offset 240, scale 145. The raw constants had already measured **-41.8 H0**
(their offsets assume another centipawn scale), so the axis needed two runs to
speak: **the scale was the poison, not the loss.** Every net published before
this week had trained on raw MSE over a single-segment sigmoid; the loss
implemented on 2026-08-11 finally trained, and won.

That makes two H1 arms of the same size against fq60 - fqwd0 +11.1 (shipping in
v5.1.0) and fqloss2 +11.9 - plus the lambda schedule at +5.3 unconcluded.
Whether they ADD is the only question left. `fqmix` (all three axes on the fq60
recipe) takes the GPU the moment the psqt net trains out, and its SPRT runs
against **fqwd0, the shipping champion**, because that is the question v5.2.0
has to answer. If the stack disappoints, the diagnosis is overlap and fqloss2
runs against fqwd0 alone.

**And the psqt two-headed net measured the same day: H0.** fqpsqt (the fqwd0
recipe plus a 1-bucket psqt head trained with MSE at lambda 0.85) against
fqwd0 at fixed nodes: **-6.4 [-18.3, +5.5], LLR -3.10 over 1,691 games**,
steady between -3 and -10 from game 400 on. The lane itself is proven correct -
trainer, export, verify_export and the engine agree to the centipawn on the
real net (sha a85696e8) - so this is content that does not buy Elo, not a bug.
What the verdict does not cover: the head trained on the MSE loss that fqloss2
beat that same morning, and with 1 bucket against the reference's 8. The one
cheap retry left is a psqt head ON the fqmix recipe, one arm against fqmix,
and only if fqmix wins; the complexity machinery the head was meant to unlock
waits behind that same door.

One number corrected from the v5.1.0 entries: the corpus in flight is a
**270M-position extension** of the existing 324M (to ~594M total), generated at
the same 6,000-node label depth and with the same teacher net as the original
corpus, by the current engine's search. Holding the teacher fixed is the point:
volume is then the only axis that moves, and volume is the axis every
measurement this month has named as the binding one.

## 2026-08-27 - fqwd0 ships as v5.1.0

The no-ft-weight-decay arm measured +11.1 [+2.3, +19.9] H1 over fq60 in 3,224 fixed-node games
and anchors at 3242 +-25 CCRL on the single-thread gauntlet. Raw reference loss lost -41.8 H0
(scale mismatch; refit 240/145 trains as fqloss2); the lambda schedule ended +5.3 in 6,000
unconcluded and waits to stack. The corpus extension and the psqt net are next.

## 2026-08-26 - the capacity axis closes at the clock, and two of this file's own claims fall

**Width 512 at the clock: -27.0, H0.** At fixed nodes it had won +38.4; at 180+2
it pays all of that back in speed, and more. With the 256 at -31.9 the capacity
axis is CLOSED, this time with converged, valid arms at both widths.

**And the disk audit that preceded the verdict corrected this file twice.** The
2026-08-24 doubt about the 256 ("its -31.9 comes from a log that ends at epoch
5") was itself false: the tail of a queue log belongs to the LAST arm the queue
ran, and that epoch-5 tail was the 512's - the 256's own run converged and its
number stands. And the clean output-bucket pair this file kept calling pending
had in fact run on 2026-08-16: **-49.7 H0**, both arms arch 3 at 60 epochs, one
variable. The contradiction with v4.2.0's +20.1 resolves the unhappy way. Both
"top candidates" named two entries below are dead, and the rule that came out of
it: **every "this was never measured" claim gets verified ON DISK before it
reorders a queue.**

## 2026-08-25/26 - in flight (superseded by the entries above)

The three training arms the crashed 11-08 queue never reached are running in
series on the fq60 recipe, one variable each: fqloss (the reference loss
instead of the raw MSE every published net trained with), fqlam (lambda
scheduled 1.0 to 0.7) and fqwd0 (no ft weight decay). Fixed-nodes SPRTs
follow each export; same architecture means the verdict transfers to the
clock unchanged. The psqt two-headed net is built end to end (engine lane,
trainer head, exact virtual-row folding, parity tests) and trains on top of
whichever recipe wins; verify_export must learn the psqt block first.

---

## Status 2026-08-24: threats CLOSED, width OPEN, and speed finally calibrated

> **Superseded 2026-08-26** (entry above): the width question closed at the
> clock, and the doubt this entry casts on the 256 measurement was itself wrong.

Two verdicts and one constant. The constant is worth more than both verdicts.

### Threats lose at the clock and do not ship

    723 games at 180+2 (131W 162L 430D, 59.5% draws)
    Elo   -14.9   95% [-30.1, +0.1]
    LLR   -3.371  against [-2.94, +2.94]   H0 ACCEPTED

    trend in blocks of 100:
      -17.4  -8.7  -18.5  -14.8  -9.7  -10.4  -12.9

Stable across all seven blocks, no drift. Both arms in the same binary, with the
engine already 15.9% faster that same day. **They evaluate better - that much
was proved with +47.8 at fixed nodes - but they cost so much speed that the
complete package loses.**

### Width 512 wins, and its burial was not valid

    637 games at fixed nodes (226W 156L 255D)
    Elo   +38.4   95% [+18.7, +58.3]
    LLR   +3.338   H1 ACCEPTED

The previous verdict was -30.3, and width had been declared dead on it for
weeks. **That arm was cut at EPOCH 5 OF 60** to save 13.5 hours. Comparing a
truncated arm against a converged one measures the truncation.

**And the 256 has not been measured properly either:** its -31.9 comes from a
log that ends at epoch 5, exactly the same way. The apparent contradiction - 256
losing 32 while 512 wins 38 - disappears the moment the log is read.

> **Correction 2026-08-26: the paragraph above about the 256 was FALSE.** The
> epoch-5 tail in that log belonged to the 512 arm; the 256's own run converged
> and its -31.9 stands.

### THE CONSTANT: ~65 Elo per doubling of speed

Crossing the two threat verdicts gives something this project never had:

    at fixed nodes   +47.8      (evaluation only)
    at the clock     -14.9      (evaluation + speed)
    NPS ratio         0.510x
    ------------------------------------------------
    speed costs  47.8 + 14.9 = 62.7 Elo
    log2(0.510) = -0.971
    =>  ~64.6 Elo PER DOUBLING, at 180+2

Until today this was estimated with a borrowed constant, and the break-even came
out anywhere between 0.58x and 0.70x depending on who wrote it. **Use 65.**

It also reorders the capacity question: no longer "wider?" but **"CHEAP
capacity?"**. The 512 runs at 0.655x against a threshold of 0.664x - right on
the edge, which is why its clock SPRT could not be replaced by arithmetic. The
two candidates that move to the front are the **converged 256** (less gain,
considerably faster) and **output buckets**, which add capacity at no
per-evaluation cost because only one bucket is evaluated per call.

> **Superseded 2026-08-26:** the 512 clock SPRT ran and lost (-27.0), and the
> disk audit found the clean bucket pair had already run on 2026-08-16 and lost
> -49.7 H0. Both candidates are dead; the axis that stayed open was training,
> and it paid (+11.9, top entry).

---

## Status 2026-08-23 (night): the three ENGINE levers for threats, measured and closed

Threats are worth +47.8 Elo at fixed nodes and the only thing blocking them is
that they do not fit in the clock. All three engine-side levers were attacked
and none opens the door on its own:

    perspective-free delta   +15.9%   DONE, 0.446x -> 0.510x of fq60's NPS
    lazy rows                NULL     they are hot in cache
    finny table              ~1%      only saves rows
    pruning relations        NO       signal proportional to cost

### 1. The delta never depended on perspective, and ran twice

Which piece attacks which is a fact of the BOARD. Perspective does not change
the fact, only the NUMBER it is indexed with. The delta took perspective as a
parameter, so per node it repeated identical geometry - magic lookups and target
loops - to produce two copies of the same fact, and the O(n*n) diff also ran
twice.

                       before  after
    AffectedAttackers     3      2
    CollectPairs/From     4      2
    diff                  2      1

**+15.87% [+12.0%, +15.3%]**, paired, 4 alternated pairs, 208/238 positions,
sign test p = 0.00000, with BYTE-IDENTICAL nodes (1,109,671 exactly).

What had to be checked first: the pair-to-index map is INJECTIVE, and `Map` and
`symmetric` depend only on TYPES, so "this relation gets recorded" does not
depend on perspective. The only thing that does is which direction survives in
the symmetric relations, and the final indexing resolves that.

The target masks are what save the design: the pair lists carry BOTH directions
of the symmetric relations and the diff is quadratic, so a 1.4x length would
have cancelled half the gain. With the masks they come out 1.043x longer and the
diff drops 45.1%.

### 2. Deferring rows until materialisation: NULL

With a threat net the accumulator was eager, while a HalfKA one defers and only
applies 54%. The per-level difference was recorded and applied at
materialisation:

    delta rows      8.00 -> 5.28 per node   (-34%)
    copies          eager -> 36.9%
    clock           +2.37%, sign test p = 0.139  ->  NULL

**Removing a third of the row work moved the clock zero.** The random-row
microbenchmark prices them at 109 ns and 12-20% of the clock; in real search
they are almost always hot in cache because sibling nodes touch similar
relations. Third time in this project that an isolated cost exaggerates the
bottleneck.

And it found a real bug the 376 tests did NOT see: `PushNull` does not call
`CompleteThreatDelta`, so a null level inherited a sibling's STALE diff. With
the eager code it was harmless; with the lazy code it was silent corruption.
The identical-nodes checker caught it, not the suite.

### 3. Pruning relations: there is nothing to prune

Command **`threatbands`**, which crosses mean per-row weight with how often each
relation type appears:

    target      % of signal   relations/pos
    Pawn           63.4%         36.32
    Knight         16.1%          9.76
    Rook           11.1%          7.62
    Bishop          8.1%          4.78
    Queen           1.0%          0.86

Proportional in every type. The weaker half of the 84 bands is 5.8% of the
signal and 9.7% of the relations: a swap, not a jump.

### Where that leaves it

The remaining cost is COMPUTE - generating the lists and diffing them - and **it
cannot be deferred**, because the diff needs the board from BEFORE, which
disappears when the move is made. It is paid at every node whether or not anyone
evaluates that node.

There is no model left to build: the question is empirical and a clock SPRT of
the threat net against fq60 answers it, both arms in the same accelerated
binary.

Instruments left in the engine: **`threatfinny`** (how much a threat accumulator
cache would save) and **`threatbands`**.

---

## Status 2026-08-23: threats BEAT the playing net, and cannot ship

**+47.8 Elo [+21.6, +74.5] at fixed nodes, 345 games, LLR +4.29, H1 accepted.**
It is the first thing to beat `fq60` since August 10. The first block of 100
games already read +45.4, so it is not end-of-run drift.

    training      60/60 epochs, 125.5 h, epoch 60 was the BEST (val 0.005856)
    exported      --arch 4 explicit, verified: arch 4, 21.3 MB
    baseline      noa-fq60 (hash 7f18eade), which is the net EMBEDDED in the
                  executable the bots play

**Mind the baseline**: the bat that existed compared against `noa-fqc60`, which
is a different net. Checked by loading all three candidates and reading the hash
the engine reports. Against the wrong net the result would have meant nothing.

### Why it does not ship: it costs two thirds of the speed

    NPS with fq60        ~746,000
    NPS with amthreat    ~245,000     -> 0.33x
    depth at 3+2         19.1 against 17.5   -> 1.6 plies of handicap

At the clock that is +48 of evaluation against around -55 of speed. That is not
a draw a release can be cut from: it is a better net that does not fit in the
time it has.

**Where the time goes, measured with the search profiler:**

    ThreatDelta.CollectFrom        16.4%
    CompleteThreatDelta            19.9%
    NnueAccumulator.Refresh        10.5%
    ThreatDelta.AddPawn             3.8%
                                   -----
    threat machinery               ~50% of search time

That is the inherent cost of tracking ~37 features per position with a delta,
not something a micro-optimisation removes. **The route to shipping it is
redesigning the refresh, not tuning what exists.**

### Two attempts to speed it up, both measured, one reverted

**Preallocated buffers instead of `stackalloc`.** The profile put
`Buffer.ZeroMemoryInternal` at **96.6%** of the time: `MaxActiveFeatures` went
from 128 to 512 while fixing an overflow that killed games, and that number
sizes three `stackalloc`s that C# ZEROES, in a method that runs per node.
Removed, the zeroing fell to 1.62%.

**And the timed A/B says it was worth ~3%, not 96%.** Same binary before and
after, same net, alternated passes: 241,788 against 249,692 nps, with 10%
dispersion inside each arm. The arithmetic agrees: 2 KB of memset is ~50 ns,
times 805,073 nodes is 40 ms out of 3,200, or 1.25%. **A sampling profile says
WHERE to look; only a stopwatch says HOW MUCH it is worth.** The change stays
because it is node-identical and cleaner, not for its Elo.

**Ordered merge instead of the O(n*n) sweep: REVERTED for measuring worse.**

    original linear sweep        19.87% self
    merge + IntroSort            21.61%  (5.47% just sorting)
    merge + insertion            22.51%

The lists have ~37 entries and **83% of features survive the move**, so
`Contains` exits early almost always while ordering pays for every element. The
original comment said a linear sweep beats any structure, and it was right.

## Status 2026-08-23: architecture 5 was measured and LOSES

Head rebuilt in the reference style - pairwise transformer read, squared
activation beside the clipped one, second hidden layer that the output reads
past, and a linear bridge. Two 3-epoch arms, identical except `--dual`, at fixed
nodes:

    575 games   -32.8 Elo [-56.5, -9.3]   LLR -6.01   H0 accepted

Both nets verified: `noa-a5dual` is arch 5 and `noa-a5ctrl` arch 2.

**The most likely cause is design, not implementation: the pairwise read HALVES
the L1 input**, from 256 to 128, leaving 64 values per perspective. The
reference can afford it because its transformer is 1024-wide and pairing leaves
512. We copied the technique without the width that carries it.

**What the parity tests could not see.** They came out exact - engine against
numpy, and QAT float against integer below 1 cp - and the architecture is still
bad. **They verify that both sides compute THE SAME THING, not that the thing
they compute is GOOD.**

The experiment that would separate the causes, if this is ever retaken: arch 5
WITHOUT the pairing. If that wins, the culprit is pairing at this width and the
right move is widening first.

---

## Status 2026-08-11: the playing net is `fq60`, and it measures 3271 +-40 CCRL

**+128 over v4.5.0's 3143**, and the first jump in the project that lands
cleanly outside its neighbouring version's error bar. It did not come from
architecture or from more generations: it came from **fixing the trainer**.

| change | measured |
|---|---|
| feature factorization | **+195.4 +-57.5** SPRT, H1 in 102 games; **+128** in the field |
| quantization-aware training | **+23.5 +-15.5** on top of the previous |

**The defect was measured before it was fixed:** **85.6%** of the feature
transformer quantised to exactly zero, 2,221 of 22,528 features were dead, and
attributing the error stage by stage gave **38.77 cp from the transformer
against 4.9 from the head** on a mean absolute evaluation of 231 cp. The engine
was playing 16.6% away from the net that had been trained. After factorizing:
zeros 85.6% -> 21.3%, error 38.79 -> 17.63 cp, and dead features fall to
**exactly 1,024** - the structurally impossible ones (pawns on ranks 1 and 8) -
so no legal feature is ignored at all.

**Own-SPRT to field conversion: +195.4 became +128.** Record that before
promising anything from an SPRT against yourself.

### The axes, re-measured 2026-08-14: it is the DATA, not the teacher

| axis | measured | how |
|---|---|---|
| more DATA (20M to 324M) | **+104.6** [+68.8, +142.8] | `told` vs `fq60`, H1 in 171 games |
| more DATA at equal compute | **+182** +-16.6 | scale calibration, LOS 100% |
| better LABELS at 20M | **+21.2** [+6.8, +35.7] | teacher test, 1,295 games |
| better LABELS at 324M | **+10.7** [-3.4, +24.9] | `fqc60` vs `fq60`, 1,100 games, unconcluded |
| more CAPACITY (width 256) | **-30.3** [-52.4, -8.5] | `fqw256`, H0 in 494 games |

**The bottleneck is POSITIONS, and the teacher matters little.** The two label
rows are the same question at two corpus scales: **+21.2 with 20M positions per
arm became +10.7 at the real 324M**. That is this campaign's expensive lesson,
and it is written out separately because it repeats: **an effect measured on a
small corpus does not predict the same effect on the full corpus**. The 59 h of
datagen that regenerated the 324,299,195 positions with `fq60` as teacher bought
a slightly better baseline, not a jump.

Two caveats on the first row, which is the one that governs:

- `told` (20M) and `fq60` (324M) both trained **60 epochs**, so the 324M net
  also received 16x more gradient steps. The +104.6 **mixes more unique
  positions with more training compute** and this measurement does not separate
  them.
- The **+182** from the calibration is not a pure volume measure: it compared
  20M at 6,000 nodes against 4.3M at 28,000 - volume against depth at equal
  compute. The only clean volume measure is the row above.

Per corpus doubling that gives **+82 Elo at the bottom** (4.3M to 20M) and
**+26 up here** (20M to 324M). It decays fast, and it is two points: they do not
support extrapolating the next doubling.

### Audit of the negatives: which of these burials hold

Written 2026-08-14 after nearly burying the threat features with two design
defects inside the test. The rule that was missing, now applied to everything
declared dead:

**Before accepting a negative, four questions must be answered.** 1) Did every
arm converge? 2) Is the configuration in the regime where the thing is known to
work? 3) Is there a **positive control** - an arm that measures something
already measured as a gain? 4) What difference remains against the reference?
If any is missing, the verdict is "no verdict", not "does not work".

Running the list through those four questions:

| buried conclusion | status after the audit |
|---|---|
| "the data axis is closed" | **WAS FALSE**. Never measured. Measured 2026-08-14: **+104.6** |
| `fqw512`, width 512, loser | **INVALID**: cut at **epoch 5 of 60** to save 13.5 h. Exactly the truncated arm question 1 forbids |
| `fqw256`, width 256, -30.3 | **IN DOUBT**: 494 games, converged, but measured on the POOR input. If input and capacity are coupled, this measures the coupling, not the width |
| `ds1b8`, buckets, -15.2 | already known invalid: mixes buckets with arch 1 vs arch 3 quantisation |
| "self-play is exhausted" | already known false: measured with the broken trainer |
| King safety phase B | **VALID**: three independent measurements, classical eval, no scale dependency |

Five of six burials do not survive the audit. The pattern is not that the ideas
were bad: it is that **the bar for saying "no" sat far below the bar for saying
"yes"**, and that biases an entire campaign toward abandoning things that
worked.

### The epochs axis, MEASURED: +6.4 and unconcluded

`fqc120` (same corpus, same recipe, 120 epochs instead of 60) against `fq60`:

    2,920 games at 10+0.1   score 0.5092
    +6.4 Elo  95% [-2.1, +14.9]   LLR +0.756 of +-2.94, 26% of the way to H1

**It does not conclude.** The honest reading is "small positive, below what
3,000 games resolve". Not enough to ship - the interval touches zero - but it
does not close the axis either: doubling the epochs is worth **something**, on
the order of +6.

**Cost: 19 h of training plus 14 h of SPRT for a number that does not
conclude.** That is the figure that matters for planning: 33 machine-hours for
an effect the available budget cannot resolve.

#### My prediction failed, and for trusting validation

I predicted **flat**, in writing and before the result, based on `fqc120`
finishing with validation **0.10% WORSE** than `fqc60` (0.005860 against
0.005854), and on it running +0.42% worse at the two comparable annealing
fractions.

Both nets trained the same corpus with the same validation split, so it was the
cleanest possible comparison between validations. **And it still pointed at the
wrong sign.**

Third time in the same week that a cheap proxy gave the wrong sign:

| proxy | said | measured |
|---|---|---|
| teacher at 20M | +21.2 | +10.7 at 324M |
| threat probe v1 | -5.43% | +3.96% with its defects fixed |
| fqc120 validation | -0.10% (worse) | **+6.4 Elo (better)** |

**Validation loss orients; it does not decide.** It was written as a warning in
this very file before I ignored it in a prediction of my own.

### The net is under-trained, not saturated

`fqc60`'s validation curve was still falling **5.59% over its last ten epochs**
and only flattened at 60 because the learning-rate cosine bottomed out at
1.07e-05. Its training loss (0.005545) remains **below** validation (0.005866)
with the gap closing, which is the opposite of overfitting. In flight:
`fqc120`, the same recipe with the cosine stretched to 120 epochs
(`T_max=args.epochs`, checked before launching), about 19 h.

### Two conclusions of this file are OVERTURNED

**1. "Do not re-propose network capacity" stands, but for a different reason.**
It was written when width 512 measured -76/-93 with the broken trainer. I
reopened the axis on 2026-08-11 arguing that measurement was tainted, because
widening worsens exactly the defect factorization fixed: the same signal spread
over more neurons gives smaller weights, and small weights are what quantisation
erases. **The argument was reasonable and it was wrong**: with factorization and
QAT in place, width 256 still loses 30 Elo. The axis is now closed with valid
evidence.

**2. "Self-play is exhausted" was FALSE, but only half-resolved.** Five flat
generations produced that conclusion, measured with the trainer that quantised
85.6% of the transformer to zero: a generation could come out flat because the
net could not exploit better labels, not because there were none. Repeated with
the trainer fixed, the new teacher wins, but **+10.7 at real scale, not the +22
the 20M test promised**. The correct conclusion is not "the teacher matters":
it is that **changing teacher over the same positions buys little, and adding
positions buys much**.

### What comes next, reordered 2026-08-14

The order is set by cost per Elo, not by how interesting the idea is:

1. **`fqc120`**, 120 epochs on the new corpus. Attacks the half of the +104.6
   that is compute, requires generating nothing and touches no engine code, and
   costs 19 h. IN FLIGHT.
2. **More corpus.** About 60 h per doubling, on the order of +26 expected from
   the current slope, and it pays to measure first how much of the +104.6 was
   compute.
3. **Threat features**: the reference adds to HalfKA an entire 60,720-dimension
   set with 128 active that encodes which piece attacks which, and we have none
   of it. Still the structural attack, but it is weeks of C# and two cheaper
   axes sit ahead. The probe that decides whether it is worth it is already
   written and verified (`probe_threats.py`).

**Of the old queue only `fqb1`/`fqb8` survives** (output buckets with their int8
control - a bucketed net only exports as arch 3, which is int8 with QA=127, so
measuring it against arch 1 `fq60` would move two variables). It survives
because it resolves a real contradiction (+20.1 at LOS 99.8% in v4.2.0 against
-15.2 in `ds1b8`), not to tune a number. **`fqwd0`, `fqloss` and `fqlam` are
dropped**: they are hyperparameter search, they live in the +-10-20 band, and at
10+0.1 resolving **+10 Elo takes ~8,700 games (45 h)** and **+5 Elo takes
~35,000 (181 h)**. Nothing gets tested whose expected effect is smaller than
the measuring instrument.

> **Overturned 2026-08-25 to 28: all three "dropped" arms ran after all, and the
> only H1s of the month came from them.** fqwd0 measured +11.1 H1 and shipped as
> v5.1.0; the reference loss, refit to this corpus, measured +11.9 H1 (raw
> constants -41.8); the lambda schedule +5.3 unconcluded, kept to stack. The
> instrument argument was right about the cost - each verdict took 2,800-6,000
> fixed-node games - and wrong about the value. And `fqb1`/`fqb8` resolved the
> other way before ever running as a pair: the disk audit found the clean
> bucket comparison had already run on 2026-08-16, **-49.7 H0**.

---

## Earlier history (generations gen2-gen9, up to v4.5.0)

> Everything below describes the generational era and **ends at gen9 / v4.5.0**.
> It is kept because it documents how the project got here and what was
> discarded along the way, but the current figures are the ones above. Where a
> conclusion of this section has been overturned, a note cites the measurement
> that overturned it.

**Key finding of that era:** the dominant lever looked like datagen label depth
(`--nodes`), not the generational loop itself. gen2-gen4 all used 14000-node
labels and made small steps (+2 to +6 Elo); gen5 raised labels to 20000 nodes and
jumped +34. **Superseded on 2026-08-01:** at equal total search work, 20M
positions at 6,000 nodes beat 4.3M at 28,000 by **+182.2 ±16.6, LOS 100%**. The
network was starved of DATA and label depth was never the binding constraint.

Internal SPRTs run at TC 10+0.1. Note that vs-classical comparisons at that fast
TC are speed-sensitive (the NNUE eval is ~66% the speed of classical), so the
absolute CCRL placement of a net comes from `gauntlet_nnue.bat` (vs the 12-engine
CCRL field), not from the internal SPRT. Classical baseline (2.8.4-equivalent,
NNUE off) ≈ 3020-3035 CCRL.

**NEWEST AT THE TOP.** The table used to run in ascending order, and what gets
consulted is always the latest net, never the first.

**And from `fact60` on, the axis stops being generational.** The gen2-gen9 rows
differ by WHO labelled the data and at how many nodes; the rows below differ by
HOW THE NET IS TRAINED on data that does not change. That is why the node column
freezes at 6000 and a "what changes" column appears.

| Net | Engine | What changes | Measured step | CCRL (gauntlet) |
|---|---|---|---|---|
| **NNUE-1.1 `fq60`** | v4.6.2, v4.7.0 | factorization **+ quantization-aware training** | **+23.5 +-15.5 vs fact60**, H1 | **3271 +-40** (600 games, 75.7%) |
| **NNUE-1.0 `fact60`** | v4.6.2 (never shipped alone) | **feature factorization**: 704 virtual (piece, square) features folded EXACTLY into their 32 copies at export | **+195.4 +-57.5 vs ds1e60**, LOS 100%, H1 in 102 games | - (fq60 measured it) |
| `ds1e60` | v4.4.0-v4.5.0 base | 60 epochs on the full 324M corpus | the campaign's comparison base | - |
| **NNUE-0.9 `gen9`** | v4.3.1, v4.4.0, v4.5.0 | same corpus as gen8, only epochs 6 &rarr; 60 | **+18 vs gen7** (1178 games, H1, LLR 2.97) | **~3114** (v4.4.0, 600 games) |
| - `gen8` | - | 6000 nodes, first at-scale corpus | **NOT promoted** (H0 at 198 games; real-game blunders TRIPLED) | gauntlet started and abandoned |
| NNUE-0.7 `gen7` | v3.2.0, v4.3.1 | 28000 nodes | +3.7 +-10.2 vs gen5 (parity, no formal H1) | **~3080 +-40** |
| NNUE-0.6 `gen6` | - | 24000 nodes | **NOT promoted** (fell to 0.494 by 800 games) | - |
| NNUE-0.5 `gen5` | v3.1.x | 20000 nodes | +34.0 +-14.4 vs gen4, LOS 100% | **~3050 +-40** |
| NNUE-0.4 `gen4` | - | 14000 nodes | +3.5 +-9.9 vs gen3 | - |
| NNUE-0.3 `gen3` | - | 14000 nodes | +6.2 +-11.3 vs classical | - |
| NNUE-0.2 `gen2` | v3.0.0 | 14000 nodes | +1.9 vs classical, H1 | - |

**What the table teaches at a glance:** seven generations of self-play took the
net from ~3050 to ~3114, that is **+64 in seven steps**. The next two changes
touched no data at all and were worth **+157** (3114 to 3271). The problem was
never where the games came from.

**Nets discarded along the way** (same corpus, same hyperparameters, one axis
different): `ds1w512` width 512 **-76 / -93**, `ds1b8` 8 buckets **-15.2** (NOT
a clean comparison, see the note at the end), `ds2` **-108.6** (moved three
variables at once), `fqw256` width 256 **-30.3, H0** with factorization and QAT
already in place.

**v4.5.0 changes no net, but it changes what serving one costs.** gen9 is still
the shipped network; the runtime around it got about 10% faster, and roughly a
third of that is NNUE-side. The accumulator stack was **eager** - it copied both
perspectives and did the feature math on every `MakeMove` whether or not the
position was ever evaluated - and now records the update and materialises it on
demand from the nearest computed ancestor (+3.6%, node-identical). Anyone
re-reading the speed notes above should treat "the NNUE eval is ~66% the speed of
classical" as the pre-4.5.0 figure; the gap is narrower now and was never
re-measured, because with NNUE shipped in every build the comparison stopped
mattering.

**gen5 CCRL calibration (2026-07-28):** field gauntlet vs the 12 CCRL engines
(2862-3281, 20 games each, 240 total) at TC 60+0.6, single-threaded. **51.0%
overall; ML performance rating ≈ 3050 CCRL** against a field averaging 3043.
gen5 beats every opponent ≤3010 (Colossus 2862: 92.5%, Bit-Genie 3010: 57.5%)
and loses to ≥3120 (Winter 3120: 37.5%, Patricia 3281: 17.5%), crossover ~3050.
This is the first CCRL number for the NNUE line. Note it lands only ~+15 over the
classical estimate (~3035), NOT the +42 the internal SPRT chain suggested - the
expected shrink of self-play gains against a diverse external field. It is the
FLOOR: Lazy SMP (v3.1.0, measured +253 Elo Threads=30 vs Threads=1 at 20+0.2, LOS 100%; CCRL field gauntlet pending); cold-start fix (v3.1.1, no Elo change)
and deeper-node generations (gen7+ at 28000+ nodes) add on top. This 3050 is
single-threaded.

**gen6 (2026-07-28):** 24000 nodes. SPRT vs gen5 (TC 10+0.1) drifted below 0.5
at 800 games and was stopped. Not promoted; gen5 remains the active teacher.
The gen6 dataset is included in the gen7 combined training set.

**gen7 (2026-07-29, v3.2.0):** 28000 nodes, embedded and promoted as a
**marginal** generation - the vs-gen5 SPRT is parity (76.2% LOS), not a formal
H1. Its own gauntlet (240 games, 60+0.6, single-thread, field 2862-3281)
placed it at **57.9%, ~3080 ±40 CCRL**, up from gen5's 51.0%/~3050 but inside
combined gauntlet noise. The honest read at the time: the human-opening
seeding this generation shipped with did not itself buy strength over gen5 -
the value was the data pipeline and pinning the NNUE-over-classical delta at
+28.5 (the old cascade-sum ~+46 had over-counted, since self-play Elo is not
additive). **Correction (2026-07-31):** the human-opening seeding never
actually ran - every manifest on disk says `"openingPlies": "8-9 random
legal"`, the `-Book` argument was never passed. The +28.5 and ~3080 figures
stand; what is void is the provenance claim and the "pure self-play is
exhausted" conclusion drawn from it, since gen7 was trained on random
openings after all. See [README](README.md) for the full correction.

Notes:
- gen2's SPRT log was later removed in a cleanup; its +1.9 (H1) is on record from
  the run, not a file.
- gen5's +34 is the deeper-labels payoff (14000→20000 nodes). Its absolute CCRL
  placement (~3050) comes from the field gauntlet; the internal-vs-classical step
  is skipped for gen5 because the gauntlet is the more direct placement.

**gen8 (2026-08-05): trained, measured, NOT PROMOTED.** The data-scale campaign's
first net: 331M positions (314,564,250 train / 16,555,978 val from 70 files) at
6000-node labels, ft=128, one output bucket, **6 epochs**. Three independent
measurements all said no:

1. **SPRT vs gen7** at 60+1, `Threads=1`, ponder off: stopped at **H0** after 198
   games (59W 95D 41L, 53.8%). No evidence of the +50 Elo the bounds asked for.
2. **Real games on the bot**, same binary, only the net swapped: the avoidable
   material-loss rate **tripled**, 0.23 to 0.72 per 100 moves (p≈0.017), and the
   score fell from 80.5% to 75.8% against opposition only 58 Elo stronger. See
   [[bot-version-timeline-aug2026]] in the session memory for the exact cutoffs.
3. **Gauntlet** vs the 12-engine field: started, then abandoned once the first
   two measurements agreed. No number recorded.

**The cause is the training schedule, not the data.** The loss curve never
flattened - validation loss fell 0.008005 → 0.005993 across the six epochs and
**the largest single drop was the last one** (-0.00065, against -0.00005 for the
first), with every epoch marked as a new best. `CosineAnnealingLR` is built with
`T_max=args.epochs`, so the learning rate hit its 7.63e-05 floor exactly when the
schedule ran out. Training stopped because the calendar ended, not because the
model converged. The `--epochs 6` default dates from the 4-20M-position
generations; at 331M it is roughly 2 billion samples seen, which is low by any
standard for this size of corpus.

**Numbering rule (settled here, precedent from gen6):** a generation number is
consumed by the ATTEMPT, not by the promotion. gen6 was trained, drifted below
0.5 and was never promoted, and the next net was still called gen7. Same now:
**gen8 keeps its number and this row**, and the retrain is **gen9**. Note that
gen9 is not a new generation in the datagen sense - it reuses gen8's corpus
byte for byte and changes only the training length (60 epochs against 6, on
disk-speed grounds - see below - not 120 as first planned), which is exactly
what makes the comparison between them clean.

**gen9 (2026-08-06): trained, measured, PROMOTED.** Same corpus as gen8, same
ft=128/one-bucket architecture, only `--epochs` changed from 6 to 60. The first
attempt used 120 and projected 3.7 days: the corpus lived on the repo's
mechanical HDD, so training was disk-starved rather than compute-bound (GPU
utilization 8-43%, step rate collapsed from ~40/s to ~7/s), and the shards had
only looked fast for gen8 because they were still warm in the OS page cache
from having just been written. Copied to an SSD instead (`train_ssd.bat`),
throughput recovered to ~28 steps/s and 60 epochs completed in 8.1 h. Validation
loss reached **0.005518** at epoch 60 (gen8: 0.005993 at epoch 6 - about 8%
lower), and the tail of the curve was flattening (-0.000063, -0.000058,
-0.000056 across the last three epochs), unlike gen8's accelerating tail -
consistent with a schedule that ran its course rather than one cut short.

**SPRT vs gen7 at 10+0.1, `Threads=1`, ponder off, no tablebases for either
side: H1 accepted, LLR 2.97 crossing the 2.94 bound at 1178 games** (352W 536D
290L, 52.63%, ~+18 Elo - close to the elo1=20 upper bound tested, not a
marginal scrape past elo0=0). The score drifted down substantially as the
sample grew (56.4% at 141 games, 54.8% at 221, 53.1% at 289, 52.5% at 702, 52.0%
at 950) before the final tally settled at 52.63% - a reminder that the
project's own naive score-tracking during a run is not the SPRT's actual
statistic; cutechess's pentanomial LLR is what decided this, not the eyeballed
trend. First real, confirmed Elo gain of the data-scale campaign, and a modest
one, matching what the flattening loss curve predicted rather than the dramatic
jump the campaign hoped for - the next lever (width, ft=128 to 512) is already
running, corpus and epoch count held fixed, to isolate that axis next.

**Status as of v4.3.0.4 (2026-08-04): still gen7, unchanged since v3.2.0.**
Everything shipped between v3.2.0 and v4.3.0.4 - Lazy SMP, complete correction
histories, output buckets, the ponderhit and root-move fixes - is search or
scheduling, not training, so this table has nothing new to record. The next
entry here is gated on the data-scale campaign (`Noa-DataScale.ps1`): phase 0
already measured the current net as **data-starved by +182 Elo** at equal
compute (20M positions @ 6000 nodes beat 4.3M @ 28000 nodes, LOS 100%), which
is why the campaign trains at 6000 nodes instead of pushing label depth
further - see [README](README.md) and [CHANGELOG](CHANGELOG.md).

**Status as of v4.3.1 (2026-08-05): still gen7, and now measured with the
current engine.** A field gauntlet of **v4.3.1 + gen7** scored **59.7% over 165
games** against the same 12 CCRL engines (average 3043), for a performance of
**~3110 ±45**. Applying one formula to all three runs for once: gen5 3050, gen7
3098, v4.3.1+gen7 3111. The **+13** over the gen7 figure sits well inside ±45,
so the correction histories and the 4.3.x fixes are **not measurably visible
here** - what the run establishes is a band, roughly **3070-3155**, with the
crossover against the field around 3150 (50.0% against Rubichess 3150, 60.7%
against Winter 3120, and losses to Princhess 3230 and above).

Two parameters differ from the gen5 and gen7 gauntlets, so treat this as the
start of a cleaner series rather than a strict continuation: the opening book
was cut at `plies=16`, and **no engine had tablebases** (uniform, therefore fair,
and stricter than a run where only NoaChess has them). Ponder was off, as it has
been throughout - cutechess only ponders when the bare `ponder` flag is present
on an `-engine` line, which these runs never pass. The run was stopped at 165 of
a planned 600 games.


---

## gen9's absolute number, measured 2026-08-07 (v4.4.0 gauntlet)

The first full-length gauntlet carrying gen9: **600 games, 60.1%, performance
~3114** against the 12-engine field averaging 3043, at 60+0.6 single-threaded,
ponder off, no tablebases for anyone. Per-opponent performances land between
3052 and 3215, so no single pairing is dragging the figure.

**This does NOT measure gen9's +18.** The previous full reading was ~3110 for
v4.3.1+gen7, and a 600-game gauntlet resolves roughly ±20. gen9 (+18 by SPRT)
plus the v4.4.0 search work (~+7 by node and nps measurement) should land near
3135; 3114 is inside the band either way. The honest statement is that the
engine sits around **3100-3150** and that nothing regressed - the gauntlet
confirms position, it does not resolve a delta of this size.

> **SUPERSEDED 2026-08-10.** That 3100-3150 band was true for four versions and
> stopped being so with `fq60`: **3271 +-40** over 600 games, +128 over
> v4.5.0's 3143, and the first jump landing cleanly outside the neighbouring
> version's bar. What follows is kept because it explains how the engine got
> here, not where it is.

### The capacity axis is now closed in both directions

Two capacity experiments were trained on gen9's exact corpus and
hyperparameters, differing in one flag each, and both **lost**:

| variant | difference | result |
|---|---|---|
| `ds1w512` | `--ft-out 512` instead of 128 | **-76** at 10+0.1, **-93** at 60+0.6 |
| `ds1b8` | `--out-buckets 8` instead of 1 | **-15.2 ±25.3, H0** at 435 games |

The b8 result is clean - checkpoint metadata confirms 60 epochs, batch 16384,
lambda 0.85, ft_out 128, l1_out 32 and the same 70 shards for both, with
`out_buckets` the only difference - so it is not the undertraining that sank
gen8. Wider loses and more heads loses. **Do not re-propose network capacity as
the next NNUE lever**; if this axis reopens it will be from the data side.

> **CONFIRMED 2026-08-11, after reopening it and being wrong.** The axis was
> reopened arguing these measurements came from the broken trainer. Repeated
> with factorization and QAT in place, width 256 measures **-30.3 [-52.4,
> -8.5], H0 in 494 games**, and `fqw512` was cut at epoch 5 rather than spend
> 13.5 hours confirming the same direction. The sentence above stands, now with
> valid evidence behind it. Separate note on buckets: **`ds1b8` was not a clean
> comparison** after all, because a bucketed net only exports as arch 3 (int8,
> QA=127) while the base was arch 1 (int16, QA=255), so that -15.2 mixes
> buckets with quantisation. The same 8 buckets measured **+20.1 at LOS 99.8%**
> in v4.2.0 on another corpus, and that contradiction was to be resolved by the
> `fqb1`/`fqb8` PAIR in the queue. **Resolved 2026-08-26 by the disk audit**:
> the clean pair had already run on 2026-08-16 and measured **-49.7 H0**.

Each published engine bakes its net in as an embedded resource, so a net swap
requires a republish, and `src/NoaChess.UCI/Resources/noa-embedded.noannue`
persists between builds - verify the reported hash before every measurement.

## The headroom guard that cried wolf (2026-09-02)

After 40 hours of training, `fqhuman` failed the exporter's accumulator
headroom check by three units: worst int16 lane 32,770 against the 32,767
limit. The first instinct - tighten the clipping, or worse, retrain - would
have been wrong both ways, because **the guard's bound was loose, not the
net's weights large**. It summed the MAX_ACTIVE largest row magnitudes over
the whole feature table, freely mixing combinations the schema cannot
produce. Two exact properties tighten it:

1. A feature index is `bucket * 704 + plane * 64 + square`, and one
   accumulator belongs to one perspective whose king square fixes ONE
   bucket - rows from different king buckets never share an accumulator.
2. Inside a bucket the layout is plane-major over 64 squares, and a square
   holds at most one piece, so at most one plane can be active per square.

| bound | fqhuman | verdict |
|---|---|---|
| global tail (the old guard) | 32,770 | false positive |
| per king bucket | 31,052 | passes |
| per bucket and per square (now) | 30,995 | passes |
| measured over 4,000,000 real positions | 12,948 | 39.5% of int16 |

The control that justifies trusting the change: re-exporting the shipping
champion under the new guard produces a **byte-identical** file, so the fix
touches only the check, never the payload. The lesson generalizes the
negative-control rule: **a guard that aborts is also a measurement, and its
bound must be interrogated for reachability before it is obeyed.** Asked
against the corpus, this one overstated reality by 2.4x. The limit itself
was never raised - a silent int16 accumulator overflow does not error, it
just plays worse, which is exactly why the guard exists.

## fqhuman: the human segments finally measured (v5.4.0, 2026-09-02)

**+18.7 [+5.3, +32.0], LLR +2.96, H1 over 1,360 fixed-node games against
fq594.** The champion recipe verbatim; the single variable is the corpus,
extended ~594M -> ~924M by adding the human game segments from datascale2
(opening plies 12-20 and middlegames 20-40, open.0010 excluded). This
closes the provenance-bug debt: the human datagen had silently never run,
so every earlier belief about human seeding was untested. The honest
confounder, recorded as ever: volume also rose, like every corpus decision
in the series. Fourth consecutive net promotion by the same protocol
(fqwd0 +11.1, fqmix +19.6, fq594 +29.7, fqhuman +18.7), all same-arch
fixed-node SPRTs whose verdicts carry to the clock by construction.

