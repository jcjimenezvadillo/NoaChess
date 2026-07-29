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
| — | gen6 | 24000 | not promoted (score drifted to 0.494 at 800g, stopped; teacher stays gen5) | — | — |

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

Notes:
- gen2's SPRT log was later removed in a cleanup; its +1.9 (H1) is on record from
  the run, not a file.
- gen5's +34 is the deeper-labels payoff (14000→20000 nodes). Its absolute CCRL
  placement (~3050) comes from the field gauntlet; the internal-vs-classical step
  is skipped for gen5 because the gauntlet is the more direct placement.
