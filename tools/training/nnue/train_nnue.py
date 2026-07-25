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
from model import NoaNnue, OUTPUT_SCALE, FT_OUT, L1_OUT


def wdl_target(scores, results, lam):
    """Blends search score and game result into a win-probability target."""
    score_p = torch.sigmoid(scores / OUTPUT_SCALE)
    result_p = (results + 1.0) / 2.0  # -1/0/+1 -> 0/0.5/1
    return lam * score_p + (1.0 - lam) * result_p


def main():
    parser = argparse.ArgumentParser()
    # One or more datasets. Multiple files are concatenated — used to MIX
    # generations (e.g. the net's own self-play + the classical baseline) so
    # the net covers both distributions instead of overfitting to one.
    parser.add_argument("--data", required=True, nargs="+")
    parser.add_argument("--out", required=True)
    parser.add_argument("--epochs", type=int, default=6)
    parser.add_argument("--batch", type=int, default=8192)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--lambda", dest="lam", type=float, default=0.7)
    parser.add_argument("--val-fraction", type=float, default=0.05)
    parser.add_argument("--seed", type=int, default=1)
    # Weight decay pulls weights toward zero. Higher values keep them away from
    # the int8/int16 quantization clip bounds -> less quantization noise in the
    # deployed eval (a real signal: it rose to ~34cp on the deep-label data).
    parser.add_argument("--weight-decay", type=float, default=1e-5)
    # Network width. Wider = more capacity (and a slower engine eval). The C#
    # loader reads both dimensions from the header, so no engine change is
    # needed. Saved into the checkpoint so export/validate rebuild the right net.
    parser.add_argument("--ft-out", type=int, default=FT_OUT)
    parser.add_argument("--l1-out", type=int, default=L1_OUT)
    # Legacy salvage flag: drops exactly-0 labels. Was needed only for the old
    # contaminated datasets (an engine hard-stop bug zeroed ~57% of labels,
    # fixed 2026-07-24). Clean datasets have ~2% genuine-draw zeros — leave off.
    parser.add_argument("--drop-zero-scores", action="store_true")
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)

    # One-time decode of all records into arrays (cached next to each file);
    # epochs afterwards are pure array slicing. Multiple files are concatenated.
    feats_list = []
    for path in args.data:
        recs = dataset.load_records(path)
        print(f"dataset: {len(recs):,} records from {path}")
        feats_list.append(dataset.precompute_features(recs, cache_path=path + ".features.npz"))
    if len(feats_list) == 1:
        features = feats_list[0]
    else:
        features = tuple(np.concatenate([f[k] for f in feats_list]) for k in range(4))
        print(f"combined: {len(features[0]):,} records from {len(args.data)} files")

    if args.drop_zero_scores:
        # features = (stm, opp, scores, results); keep only real-signal labels.
        keep = features[2] != 0
        features = tuple(a[keep] for a in features)
        print(f"drop-zero-scores: kept {keep.sum():,} / {len(keep):,} "
              f"({keep.mean()*100:.1f}%) nonzero-label records")

    # Train/validation split BY GAME would need game ids; the format orders
    # records by game, so a contiguous tail split approximates it (whole
    # games end up on one side of the cut except at most one).
    n_used = len(features[0])
    val_count = int(n_used * args.val_fraction)
    cut = n_used - val_count
    train_set = tuple(a[:cut] for a in features)
    val_set = tuple(a[cut:] for a in features)
    print(f"train: {cut:,}  val: {val_count:,}")
    steps_per_epoch = -(-cut // args.batch)  # ceiling division

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device: {device}"
          + (f" ({torch.cuda.get_device_name(0)})" if device.type == "cuda" else " (no CUDA GPU)"))

    model = NoaNnue(args.ft_out, args.l1_out).to(device)
    print(f"net: ft_out={args.ft_out} l1_out={args.l1_out}")
    optimizer = torch.optim.Adam(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs, eta_min=1e-5)

    # Feature arrays stay in host memory (too large for VRAM); each batch is
    # copied to the GPU just before the forward pass.
    def to_dev(a):
        return torch.from_numpy(a).to(device, non_blocking=True)

    def evaluate_validation():
        if len(val_set[0]) == 0:
            return float("nan")
        model.eval()
        losses = []
        with torch.no_grad():
            for stm, opp, scores, results in dataset.batches(
                    None, args.batch, np.random.default_rng(0), precomputed=val_set):
                pred = torch.sigmoid(model(to_dev(stm), to_dev(opp)))
                target = wdl_target(to_dev(scores), to_dev(results), args.lam)
                losses.append(torch.mean((pred - target) ** 2).item())
        model.train()
        return float(np.mean(losses)) if losses else float("nan")

    print(f"training: epochs={args.epochs} batch={args.batch} lr={args.lr} lambda={args.lam}")
    start = time.time()

    best_val_loss = float("inf")
    best_state = None
    best_epoch = 0

    for epoch in range(1, args.epochs + 1):
        epoch_losses = []
        for step, (stm, opp, scores, results) in enumerate(
                dataset.batches(None, args.batch, rng, precomputed=train_set)):
            pred = torch.sigmoid(model(to_dev(stm), to_dev(opp)))
            target = wdl_target(to_dev(scores), to_dev(results), args.lam)
            loss = torch.mean((pred - target) ** 2)

            optimizer.zero_grad()
            loss.backward()
            optimizer.step()
            model.clip_weights()
            epoch_losses.append(loss.item())

            if step % 50 == 0:
                elapsed = time.time() - start
                steps_done = (epoch - 1) * steps_per_epoch + step + 1
                steps_left = args.epochs * steps_per_epoch - steps_done
                eta_min = steps_left * (elapsed / steps_done) / 60
                print(f"  epoch {epoch} step {step}: loss {loss.item():.6f} "
                      f"({elapsed:.0f}s, ETA {eta_min:.0f} min)", flush=True)

        val_loss = evaluate_validation()
        current_lr = optimizer.param_groups[0]['lr']
        marker = ""
        if val_loss < best_val_loss:
            best_val_loss = val_loss
            best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
            best_epoch = epoch
            marker = " *"
        print(f"epoch {epoch}: train {np.mean(epoch_losses):.6f}  val {val_loss:.6f}  lr {current_lr:.2e}{marker}", flush=True)
        scheduler.step()

    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    torch.save({"model": best_state,
                "args": vars(args),
                "dataset": args.data}, args.out)
    print(f"saved checkpoint: {args.out} (best epoch {best_epoch}, val {best_val_loss:.6f})")


if __name__ == "__main__":
    main()
