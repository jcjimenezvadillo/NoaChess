"""Prints the dataset file list a checkpoint was trained on, one path per line.

The pipeline's rule is that a candidate differs from its baseline in ONE axis.
"Same data" is the easiest half of that to get wrong, because the file list is
typed into a script rather than derived: ds2 dropped selfplay-gen8 relative to
its baseline without anyone deciding to, on top of the epoch change it was
actually testing. Reading the list off the baseline checkpoint makes the shared
axis mechanical instead of remembered.

Exits non-zero if any listed file is missing, so a training script can refuse to
start rather than quietly train on a different corpus.

Usage: python list_checkpoint_data.py checkpoints/ds1e60.pt
"""
import os
import sys

import torch


def main(path):
    checkpoint = torch.load(path, map_location="cpu", weights_only=False)
    files = checkpoint.get("args", {}).get("data") or checkpoint.get("dataset")
    if not files:
        raise SystemExit(f"{path}: no dataset list recorded in the checkpoint")

    missing = [f for f in files if not os.path.exists(f)]
    for f in files:
        print(f)
    if missing:
        print(f"\n{len(missing)} of {len(files)} files are MISSING:", file=sys.stderr)
        for f in missing:
            print(f"  {f}", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    main(sys.argv[1])
