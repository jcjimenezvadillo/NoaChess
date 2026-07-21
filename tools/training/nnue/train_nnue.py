# Trains the NoaChess NNUE (architecture 1) from a NOADATA1 dataset.
#
# Target (per the technical roadmap):
#   target = lambda * sigmoid(search_score / SCALE)
#          + (1 - lambda) * wdl(result)
# in win-probability space, trained with MSE against the net's sigmoid.
# Both signals are from the side to move, matching the record layout.
#
# Usage:
#   python train_nnue.py --data ../../data/selfplay.noadata --epochs 6 \
#       --out checkpoints/run1.pt [--lambda 0.7] [--batch 8192] [--lr 1e-3]

import argparse
import time
from pathlib import Path

import numpy as np
import torch

import dataset
from model import NoaNnue, OUTPUT_SCALE


def wdl_target(scores, results, lam):
    """Blends search score and game result into a win-probability target."""
    score_p = torch.sigmoid(scores / OUTPUT_SCALE)
    result_p = (results + 1.0) / 2.0  # -1/0/+1 -> 0/0.5/1
    return lam * score_p + (1.0 - lam) * result_p


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--epochs", type=int, default=6)
    parser.add_argument("--batch", type=int, default=8192)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--lambda", dest="lam", type=float, default=0.7)
    parser.add_argument("--val-fraction", type=float, default=0.05)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)

    records = dataset.load_records(args.data)
    print(f"dataset: {len(records):,} records from {args.data}")

    # One-time decode of all records into arrays (cached next to the data):
    # epochs afterwards are pure array slicing instead of Python loops.
    features = dataset.precompute_features(records, cache_path=args.data + ".features.npz")

    # Train/validation split BY GAME would need game ids; the format orders
    # records by game, so a contiguous tail split approximates it (whole
    # games end up on one side of the cut except at most one).
    val_count = int(len(records) * args.val_fraction)
    cut = len(records) - val_count
    train_set = tuple(a[:cut] for a in features)
    val_set = tuple(a[cut:] for a in features)
    print(f"train: {cut:,}  val: {val_count:,}")

    model = NoaNnue()
    optimizer = torch.optim.Adam(model.parameters(), lr=args.lr)

    def evaluate_validation():
        if len(val_set[0]) == 0:
            return float("nan")
        model.eval()
        losses = []
        with torch.no_grad():
            for stm, opp, scores, results in dataset.batches(
                    None, args.batch, np.random.default_rng(0), precomputed=val_set):
                pred = torch.sigmoid(model(torch.from_numpy(stm), torch.from_numpy(opp)))
                target = wdl_target(torch.from_numpy(scores), torch.from_numpy(results), args.lam)
                losses.append(torch.mean((pred - target) ** 2).item())
        model.train()
        return float(np.mean(losses)) if losses else float("nan")

    print(f"training: epochs={args.epochs} batch={args.batch} lr={args.lr} lambda={args.lam}")
    start = time.time()

    for epoch in range(1, args.epochs + 1):
        epoch_losses = []
        for step, (stm, opp, scores, results) in enumerate(
                dataset.batches(None, args.batch, rng, precomputed=train_set)):
            pred = torch.sigmoid(model(torch.from_numpy(stm), torch.from_numpy(opp)))
            target = wdl_target(torch.from_numpy(scores), torch.from_numpy(results), args.lam)
            loss = torch.mean((pred - target) ** 2)

            optimizer.zero_grad()
            loss.backward()
            optimizer.step()
            model.clip_weights()
            epoch_losses.append(loss.item())

            if step % 50 == 0:
                print(f"  epoch {epoch} step {step}: loss {loss.item():.6f} "
                      f"({time.time() - start:.0f}s)", flush=True)

        val_loss = evaluate_validation()
        print(f"epoch {epoch}: train {np.mean(epoch_losses):.6f}  val {val_loss:.6f}", flush=True)

    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    torch.save({"model": model.state_dict(),
                "args": vars(args),
                "dataset": args.data}, args.out)
    print(f"saved checkpoint: {args.out}")


if __name__ == "__main__":
    main()
