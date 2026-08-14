"""Print the training arguments embedded in a checkpoint.

The pipeline's own rule is that a new run must differ from its baseline in ONE
axis. That is only checkable if the baseline's hyperparameters are read off the
checkpoint rather than remembered, which is how block 8 became uninterpretable.

Usage: python dump_args.py checkpoints/ds1e60.pt [checkpoints/gen7.pt ...]
"""
import sys
import torch


def main(paths):
    for p in paths:
        ck = torch.load(p, map_location="cpu", weights_only=False)
        print(f"=== {p} ===")
        args = ck.get("args") or ck.get("train_args") or {}
        if hasattr(args, "__dict__"):
            args = vars(args)
        if not args:
            print("  no embedded args; top-level keys:", list(ck.keys()))
        else:
            for k in sorted(args):
                print(f"  {k:<18} {args[k]}")
        for k in ("epoch", "epochs_done", "val_loss", "best_val", "dataset"):
            if k in ck:
                print(f"  [{k}] {ck[k]}")
        print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    main(sys.argv[1:])
