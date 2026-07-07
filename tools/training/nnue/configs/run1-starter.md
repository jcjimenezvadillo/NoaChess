# Experiment: run1-starter (first NoaChess NNUE)

Reproducibility record per the technical roadmap.

## Data
- Generator: NoaChess.DataGen (engine NoaChess 2.0.0-dev, evaluator: classical ~2070 Elo)
- Games: 29,991 self-play, 4,000 nodes/move, seed 20260707, threads 10
- Openings: 8-9 random legal plies
- Filters: no in-check positions, no tactical best-move positions, |score| < 20000
- Records: 2,820,289 (data/selfplay-run1.noadata + .manifest.json)

## Training
- Architecture 1: HalfKP 40960 -> FT 128 x2 -> L1 32 -> 1 (see model.py)
- Targets: lambda 0.7 * sigmoid(score/400) + 0.3 * wdl(result)
- Epochs 6, batch 8192, Adam lr 1e-3, seed 1, val fraction 5% (tail split)
- Hardware: CPU (Ryzen), PyTorch 2.x

## Expected outcome
First functional net. The teacher is the engine's own classical eval at 4K
nodes, so the ceiling of this net is roughly "classical eval quality, cheaper
pattern interpolation". Beating classical by a wide margin requires more data
(tens of millions of positions), stronger teacher searches and iterated
generations — planned as the next runs.

## Gate
SPRT vs classical (tools/sprt/sprt_nnue_vs_classical.bat). Promote only on
"H1 accepted".
