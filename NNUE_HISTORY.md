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
| NNUE-0.1 | gen2 | 14000 | +1.9 vs classical (H1) | +1.9 | — |
| NNUE-0.2 | gen3 | 14000 | +6.2 ±11.3 vs classical (2707g, LOS 85.8%) | +6.2 | — |
| NNUE-0.3 | gen4 | 14000 | +3.5 ±9.9 vs gen3 (3000g, LOS 75.3%) | +9.1 ±13.4 (1950g) | — |
| NNUE-0.4 | gen5 | 20000 | +34.0 ±14.4 vs gen4 (1495g, LOS 100%) | pending | pending (running) |

Notes:
- gen2's SPRT log was later removed in a cleanup; its +1.9 (H1) is on record from
  the run, not a file.
- gen5's +34 is the deeper-labels payoff (14000→20000 nodes). Its absolute CCRL
  number is being measured via the field gauntlet; the internal-vs-classical step
  is skipped for gen5 because the gauntlet is the more direct placement.
