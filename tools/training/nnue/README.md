# NoaChess NNUE training pipeline

End-to-end flow (all offline; the engine never depends on Python):

```
1. Generate self-play data (C#, fast):
   dotnet run --project tools/NoaChess.DataGen -c Release -- \
       --games 30000 --nodes 4000 --threads 10 --seed 1 --out data/run1.noadata

2. Train (PyTorch, CPU is fine for this size):
   cd tools/training/nnue
   python train_nnue.py --data ../../../data/run1.noadata \
       --out checkpoints/run1.pt --epochs 8 --lambda 0.7

3. Validate (loss + quantization error):
   python validate_nnue.py --checkpoint checkpoints/run1.pt --data ../../../data/run1.noadata

4. Export to the engine's binary format:
   python export_model.py --checkpoint checkpoints/run1.pt \
       --out ../../../models/nnue/noa-v2.noannue

5. Use in the engine:
   setoption name EvalFile value models/nnue/noa-v2.noannue
   setoption name UseNNUE value true

6. Promotion gate: SPRT vs the classical baseline (cutechess), same binary,
   only the evaluator differs. No SPRT pass, no promotion.
```

The steps above are the starter run of 2026-07. The shipped nets train with
many more switches (factorized features, the coarse threat lane, QAT, the
reference-style loss, a lambda ramp, `--reweight` per source); the full recipe
of each one is in NNUE_HISTORY.md. `dump_args.py` prints the arguments a
checkpoint was trained with, but for a resumed run those are only its last
segment's: read NNUE_HISTORY.md before copying them. Run control added in
v5.9.25:

- `--val-split tail-dedup`: the last `--val-fraction` of each file, minus
  every tail row whose position is a training row of any file (the plain
  `tail` default let 19% of the validation tail repeat training positions on
  the 282-file corpus). `hash` exists for comparison only.
- Exact resume: every epoch writes `<out>.state`; `--resume-state <out>.state`
  continues the run exactly (weights, optimizer, scheduler, lambda ramp, RNGs).
  `--init-from` only warm-starts a new run from a checkpoint's weights.
- Clean stop at an epoch boundary: `--stop-after-epoch N`, or create
  `<out>.stop` in a running job (exit code 3; resume with `--resume-state`).
- `--loader-process` and `--loader-workers N`: the streaming loader in its own
  process with N reader threads; byte-identical batches, about twice the
  training speed on the GPU box.

`NoaChess.DataGen --tb-path <dirs>` relabels positions inside the tablebase
range with their exact WDL and, from v5.9.25, probes the tables inside the
search too; it runs a startup self-check (K+N+N against K must score 0) and
refuses to start if the probe is not live.

## Frozen contracts

| Contract | C# side | Python side |
|---|---|---|
| Feature schema 2 (HalfKAv2_hm 22528) | `NnueFeatureIndex.cs` | `dataset.py` |
| Dataset format NOADATA1 | `tools/NoaChess.DataGen/DatasetFormat.cs` | `dataset.py` |
| Model format NOANNUE1 + quantization | `NnueModelLoader.cs` / `NnueNetwork.cs` | `export_model.py` |
| Architecture 1 (FT 128, L1 32) | `NnueInference.cs` | `model.py` |

Changing any of these requires bumping the corresponding id and regenerating
whatever depends on it. The C# test suite pins the exact behavior
(`NnueTests.cs`), including golden feature indices and incremental-vs-refresh
equality.

## Targets

`target = lambda * sigmoid(score / 400) + (1 - lambda) * wdl(result)` in
win-probability space; both signals are side-to-move relative. `--lambda 1.0`
trains purely on search scores, `0.0` purely on game results.

## Reproducibility

Every dataset ships with a `.manifest.json` (parameters, filters, engine
version, SHA-256). Checkpoints embed their training args and dataset path.
Exported models carry the payload SHA-256 in the header, which the engine
prints on load (`info string NNUE model loaded (<sha>)`).
