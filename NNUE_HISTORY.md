# NNUE Training History

Generational self-play pipeline results. Elo vs classical = cumulative gain over the classical evaluator (measured by SPRT at TC 10+0.1).

| Version | Gen | Elo vs classical | SPRT result | Notes |
|---------|-----|-----------------|-------------|-------|
| NNUE-0.1 | gen2 | +1.9 | H1 | First positive generation |
| NNUE-0.2 | gen3 | +4.5 ±11.4 | Marginal (LOS 77.8%, 2650g) | Exhausted positive |
| NNUE-0.3 | gen4 | ~+8-9 acumulado | Marginal (LOS 75.3%, 3000g) | +3.5 vs gen3; cumulative confirmed by sprt_best_vs_classical (+9.1 ±13.4, 1950g) |
