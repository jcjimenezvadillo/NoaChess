# NNUE Training History

Generational self-play pipeline. Each generation's datagen uses the previously
promoted net as teacher; the training data accumulates across generations.

**Key finding:** the dominant lever is datagen label depth (`--nodes`), not the
generational loop itself. gen2–gen4 all used 14000-node labels and made small
steps (+2 to +6 Elo); gen5 raised labels to 20000 nodes and jumped +34. Deeper
labels = stronger teacher = bigger step.

Internal SPRTs run at TC 10+0.1. Note that vs-classical comparisons at that fast
TC are speed-sensitive (the NNUE eval is ~66% the speed of classical), so the
absolute CCRL placement of a net comes from `gauntlet_nnue.bat` (vs the 12-engine
CCRL field), not from the internal SPRT. Classical baseline (2.8.4-equivalent,
NNUE off) ≈ 3020–3035 CCRL.

| Version | Gen | Datagen nodes | Direct step measured | vs Classical (direct SPRT) | CCRL (gauntlet) |
|---------|-----|--------------|----------------------|----------------------------|-----------------|
| NNUE-0.2 | gen2 | 14000 | +1.9 vs classical (H1) | +1.9 | — |
| NNUE-0.3 | gen3 | 14000 | +6.2 ±11.3 vs classical (2707g, LOS 85.8%) | +6.2 | — |
| NNUE-0.4 | gen4 | 14000 | +3.5 ±9.9 vs gen3 (3000g, LOS 75.3%) | +9.1 ±13.4 (1950g) | — |
| NNUE-0.5 | gen5 | 20000 | +34.0 ±14.4 vs gen4 (1495g, LOS 100%) | ~+15 (see note) | **~3050 ±40** |
| NNUE-0.6 | gen6 | 24000 | not promoted (score drifted to 0.494 at 800g, stopped; teacher stays gen5) | — | — |
| NNUE-0.7 | gen7 | 28000 | +3.7 ±10.2 vs gen5 (3000g, LOS 76.2%, parity - not a formal H1) | +28.5 ±13.0 (2176g, LOS 100%) | **~3080 ±40** |
| — | gen8 | 6000 | **not promoted** (SPRT vs gen7 stopped at H0, 198g; blunder rate TRIPLED in real games) | — | gauntlet started and abandoned |

**gen5 CCRL calibration (2026-07-28):** field gauntlet vs the 12 CCRL engines
(2862–3281, 20 games each, 240 total) at TC 60+0.6, single-threaded. **51.0%
overall; ML performance rating ≈ 3050 CCRL** against a field averaging 3043.
gen5 beats every opponent ≤3010 (Colossus 2862: 92.5%, Bit-Genie 3010: 57.5%)
and loses to ≥3120 (Winter 3120: 37.5%, Patricia 3281: 17.5%), crossover ~3050.
This is the first CCRL number for the NNUE line. Note it lands only ~+15 over the
classical estimate (~3035), NOT the +42 the internal SPRT chain suggested — the
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
byte for byte and changes only the training length (120 epochs against 6), which
is exactly what makes the comparison between them clean.

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
