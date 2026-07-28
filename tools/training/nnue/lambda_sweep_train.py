# Lambda sweep: trains one net per lambda value, all with the same combined
# dataset and hyperparams, so only lambda varies.
#
# Usage:
#   python lambda_sweep_train.py
#
# Outputs: checkpoints/lsweep-0.50.pt ... checkpoints/lsweep-1.00.pt

import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).parent

LAMBDAS = [0.75, 0.775, 0.80, 0.825, 0.85, 0.875, 0.90, 0.925, 0.95]

DATA = [
    str(HERE / "../../../data/selfplay-gen3.noadata"),
]

EPOCHS  = 5
SEED    = 42
FT_OUT  = 128
L1_OUT  = 32

def main():
    for lam in LAMBDAS:
        tag = f"lsweep-{lam:.3f}"
        out = str(HERE / f"checkpoints/{tag}.pt")
        print(f"\n{'='*60}")
        print(f"  Training lambda={lam:.3f}  -> {tag}.pt")
        print(f"{'='*60}\n")
        cmd = [
            sys.executable, str(HERE / "train_nnue.py"),
            "--data", *DATA,
            "--out", out,
            "--epochs", str(EPOCHS),
            "--lambda", str(lam),
            "--seed", str(SEED),
            "--ft-out", str(FT_OUT),
            "--l1-out", str(L1_OUT),
            "--weight-decay", "1e-5",
        ]
        result = subprocess.run(cmd)
        if result.returncode != 0:
            print(f"ERROR: training failed for lambda={lam}")
            sys.exit(1)
        print(f"\n  Done: {tag}.pt\n")

    print("\nAll lambdas trained. Run lambda_sweep_match.py next.\n")

if __name__ == "__main__":
    main()
