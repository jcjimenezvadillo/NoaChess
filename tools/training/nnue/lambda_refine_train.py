# Lambda REFINE: trains the plausible-peak lambdas with MULTIPLE SEEDS each,
# so the A/B match can average out training luck (the original sweep used one
# net per lambda, so its ranking was dominated by which 5-epoch net happened to
# come out better, not by lambda). Fewer, repeated lambdas > more lambda values.
#
# Range picked from the first sweep: the plausible peak was 0.85-0.95
# (0.925/0.875/0.85 were the top three, all within noise). We resolve THAT
# region with seed-averaging instead of adding finer steps (finer steps only
# add noise; the effect is smaller than the grid already is).
#
# Usage:  python lambda_refine_train.py
# Output: checkpoints/lref-{lam}-s{seed}.pt  +  models/nnue/lref-{lam}-s{seed}.noannue

import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).parent

LAMBDAS = [0.85, 0.875, 0.90, 0.925, 0.95]
SEEDS   = [42, 777]                       # 2 seeds per lambda -> training luck averaged

# gen3-only on purpose: lambda is a property of the loss objective (how much to
# weight WDL vs search score), largely data-independent, so gen3 is a fast, fair
# proxy. Keeps each net at ~4 min instead of ~12 on the full combined set.
DATA = [str(HERE / "../../../data/selfplay-gen3.noadata")]

EPOCHS = 5
FT_OUT = 128
L1_OUT = 32


def main():
    total = len(LAMBDAS) * len(SEEDS)
    n = 0
    for lam in LAMBDAS:
        for seed in SEEDS:
            n += 1
            tag  = f"lref-{lam:.3f}-s{seed}"
            ckpt = str(HERE / f"checkpoints/{tag}.pt")
            net  = str(HERE / f"../../../models/nnue/{tag}.noannue")
            print(f"\n{'='*60}\n  [{n}/{total}] train lambda={lam:.3f} seed={seed} -> {tag}\n{'='*60}\n", flush=True)
            r = subprocess.run([
                sys.executable, str(HERE / "train_nnue.py"),
                "--data", *DATA, "--out", ckpt,
                "--epochs", str(EPOCHS), "--lambda", str(lam), "--seed", str(seed),
                "--ft-out", str(FT_OUT), "--l1-out", str(L1_OUT), "--weight-decay", "1e-5",
            ])
            if r.returncode != 0:
                print(f"ERROR: training failed for {tag}")
                sys.exit(1)
            r = subprocess.run([
                sys.executable, str(HERE / "export_model.py"),
                "--checkpoint", ckpt, "--out", net,
            ], capture_output=True, text=True)
            if r.returncode != 0:
                print(f"ERROR exporting {tag}\n{r.stderr}")
                sys.exit(1)
            print(f"  exported {tag}.noannue", flush=True)

    print("\nAll nets trained + exported. Run lambda_refine_match.py next.\n")


if __name__ == "__main__":
    main()
