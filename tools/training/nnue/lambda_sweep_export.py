# Exports all lsweep-*.pt checkpoints to models/nnue/lsweep-*.noannue
#
# Usage:
#   python lambda_sweep_export.py

import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).parent

LAMBDAS = [0.75, 0.775, 0.80, 0.825, 0.85, 0.875, 0.90, 0.925, 0.95]

def main():
    for lam in LAMBDAS:
        tag  = f"lsweep-{lam:.3f}"
        ckpt = HERE / f"checkpoints/{tag}.pt"
        out  = HERE / f"../../../models/nnue/{tag}.noannue"
        if not ckpt.exists():
            print(f"SKIP {tag}.pt — not found")
            continue
        print(f"Exporting {tag} ...", end=" ", flush=True)
        cmd = [
            sys.executable, str(HERE / "export_model.py"),
            "--checkpoint", str(ckpt),
            "--out", str(out),
        ]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            print(f"ERROR\n{result.stderr}")
            sys.exit(1)
        print("OK")
    print("\nAll nets exported.")

if __name__ == "__main__":
    main()
