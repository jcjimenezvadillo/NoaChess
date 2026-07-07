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

## Frozen contracts

| Contract | C# side | Python side |
|---|---|---|
| Feature schema 1 (HalfKP 40960) | `NnueFeatureIndex.cs` | `dataset.py` |
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
