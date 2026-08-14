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
import queue
import threading
import time
from pathlib import Path

import numpy as np
import torch

import dataset
from model import NoaNnue, OUTPUT_SCALE, FT_OUT, L1_OUT, OUT_BUCKETS, FACTORIZED, QA


def prefetch(iterable, depth):
    """Produces batches on a background thread so the loader overlaps the GPU.

    Measured at full scale on the 69-shard corpus: 20.3 ms/batch in the loader
    against 11.0 ms of forward+backward, run one after the other. Overlapping
    them takes a step from their sum to their maximum.

    THE BATCHES AND THEIR ORDER ARE UNCHANGED. This moves only WHERE a batch is
    built, never which one comes next: the generator is still advanced exactly
    once per batch, in order, from the same RNG. Anything else would silently
    alter what the net trains on, so it is checked rather than assumed.

    The heavy work inside the loader is numpy (mmap reads, concatenate, fancy
    indexing), all of which release the GIL, which is what makes the overlap
    real rather than nominal.
    """
    if depth <= 0:
        yield from iterable
        return

    pending = queue.Queue(maxsize=depth)
    done = object()

    def produce():
        # An exception on the producer must reach the consumer, otherwise the
        # training loop would just see a short epoch and carry on as if the data
        # had run out normally.
        try:
            for item in iterable:
                pending.put(item)
        except BaseException as error:  # noqa: BLE001 - re-raised on the consumer
            pending.put(error)
        else:
            pending.put(done)

    threading.Thread(target=produce, daemon=True).start()
    while True:
        item = pending.get()
        if item is done:
            return
        if isinstance(item, BaseException):
            raise item
        yield item


def wdl_target(scores, results, lam):
    """Blends search score and game result into a win-probability target."""
    score_p = torch.sigmoid(scores / OUTPUT_SCALE)
    result_p = (results + 1.0) / 2.0  # -1/0/+1 -> 0/0.5/1
    return lam * score_p + (1.0 - lam) * result_p


def symmetric_win_rate(value_cp, offset, scaling):
    """Maps a centipawn value to a win rate, symmetric in the sign of the value.

    A plain sigmoid(cp / scale) has its steepest region at 0, so it spends most
    of its resolution separating +10 cp from -10 cp - positions that are the
    same game. This form is flat near zero and steep around +/- offset, which is
    where a centipawn actually changes the result. Both halves are evaluated and
    subtracted so the mapping is exactly antisymmetric.
    """
    q = (value_cp - offset) / scaling
    qm = (-value_cp - offset) / scaling
    return 0.5 * (1.0 + torch.sigmoid(q) - torch.sigmoid(qm))


def make_loss(args):
    """Returns loss(raw_output, scores, results, lam) for the chosen style."""
    if args.loss_style == "mse":
        # What every net so far was trained with: squared error between the net's
        # sigmoid and a target built with one 400 cp scale.
        def mse_loss(raw, scores, results, lam):
            return torch.mean((torch.sigmoid(raw) - wdl_target(scores, results, lam)) ** 2)
        return mse_loss

    # The reference formulation. Three differences from the above, all of them
    # deliberate on their side: the win-rate mapping above instead of a plain
    # sigmoid, separate scalings for the net and for the label (the net's output
    # distribution is not the teacher's), and an exponent above 2 so large
    # errors are punished harder than squared error punishes them.
    def reference_loss(raw, scores, results, lam):
        qf = symmetric_win_rate(raw * OUTPUT_SCALE, args.in_offset, args.in_scaling)
        pf = symmetric_win_rate(scores, args.out_offset, args.out_scaling)
        t = (results + 1.0) / 2.0
        pt = pf * lam + t * (1.0 - lam)
        return torch.mean(torch.pow(torch.abs(pt - qf), args.pow_exp))
    return reference_loss


def main():
    parser = argparse.ArgumentParser()
    # One or more datasets. Multiple files are concatenated - used to MIX
    # generations (e.g. the net's own self-play + the classical baseline) so
    # the net covers both distributions instead of overfitting to one.
    parser.add_argument("--data", required=True, nargs="+")
    parser.add_argument("--out", required=True)
    parser.add_argument("--epochs", type=int, default=6)
    parser.add_argument("--batch", type=int, default=8192)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--lambda", dest="lam", type=float, default=0.7)
    # Lambda schedule. Both default to --lambda, so a run that does not ask for
    # a schedule holds it constant exactly as before. Interpolated linearly over
    # the whole run and clamped, which is what the reference does:
    #   actual_lambda = start + (end - start) * ratio,  ratio in [0, 1]
    # The point is to weight the teacher's evaluation early, when the net cannot
    # yet tell a won position from a drawn one, and shift toward the game result
    # later, when it can.
    parser.add_argument("--start-lambda", type=float, default=None)
    parser.add_argument("--end-lambda", type=float, default=None)
    # Loss formulation. "mse" is what every net so far used. "reference" is the
    # published form: an antisymmetric win-rate mapping with its own offset and
    # scaling on each side, and an exponent above 2.
    parser.add_argument("--loss-style", choices=["mse", "reference"], default="mse")
    parser.add_argument("--in-offset", type=float, default=270.0)
    parser.add_argument("--in-scaling", type=float, default=340.0)
    parser.add_argument("--out-offset", type=float, default=270.0)
    parser.add_argument("--out-scaling", type=float, default=380.0)
    parser.add_argument("--pow-exp", type=float, default=2.5)
    parser.add_argument("--val-fraction", type=float, default=0.05)
    parser.add_argument("--seed", type=int, default=1)
    # Weight decay pulls weights toward zero. Higher values keep them away from
    # the int8/int16 quantization clip bounds -> less quantization noise in the
    # deployed eval (a real signal: it rose to ~34cp on the deep-label data).
    parser.add_argument("--weight-decay", type=float, default=1e-5)
    # Weight decay for the FEATURE TRANSFORMER only. Defaults to --weight-decay,
    # which is what every net so far was trained with, so leaving it alone
    # changes nothing.
    #
    # WHY IT DESERVES ITS OWN KNOB. The reference trainer sets weight decay to
    # 0.0 on the feature transformer and applies it only to the dense layers.
    # Ours goes through Adam(model.parameters()), so it reaches the transformer
    # too - and because EmbeddingBag produces a DENSE gradient, every one of the
    # 22,528 rows is decayed on every one of ~1.1M steps, including the rows
    # whose feature never appeared in the batch. Measured on a matched pair
    # (gen2net vs gen2net_wd, same data, same epochs, decay 1e-5 vs 1e-4): mean
    # |w| 0.00149 -> 0.00045 and the fraction of the table that survives
    # quantization 86.2% -> 95.5% dead.
    #
    # HONEST CAVEAT: on that same pair the RELATIVE quantization error barely
    # moved (15.8% -> 17.3%), so the mechanism by which this would buy Elo is
    # plausible but not established. That is what the SPRT is for.
    parser.add_argument("--ft-weight-decay", type=float, default=None)
    # Network width. Wider = more capacity (and a slower engine eval). The C#
    # loader reads both dimensions from the header, so no engine change is
    # needed. Saved into the checkpoint so export/validate rebuild the right net.
    parser.add_argument("--ft-out", type=int, default=FT_OUT)
    parser.add_argument("--l1-out", type=int, default=L1_OUT)
    # Output buckets (v4.2.0). The head is replicated per bucket and selected by
    # piece count, so the net gets a per-phase readout. Only one bucket is
    # evaluated at play time, so this is capacity at ~zero runtime cost. Saved
    # into the checkpoint so export rebuilds the right shape; 1 = unbucketed.
    parser.add_argument("--out-buckets", type=int, default=OUT_BUCKETS)
    # Feature factorization (v4.6.0). Adds 704 virtual (piece, square) features
    # that every real feature fires alongside its own row, so the shared row
    # collects 32x the gradient. They are folded into the real rows at export,
    # exactly, so the engine and the model file are completely unaffected. See
    # the header of model.py for the measurement that motivated it.
    parser.add_argument("--factorized", action="store_true", default=FACTORIZED)
    # Quantization-aware training. Rounds weights and floors activations inside
    # the forward pass with a straight-through estimator, so the optimiser sees
    # the arithmetic the ENGINE runs instead of a float approximation of it.
    # Measured motivation in model.py: quantization currently moves the shipping
    # net's evaluation by 16.6%, essentially all of it in the feature
    # transformer. --qa must match the export arch: 255 for arch 1 (every net
    # that has ever shipped), 127 for arch 2/3.
    parser.add_argument("--qat", action="store_true")
    parser.add_argument("--qa", type=int, default=QA, choices=[QA, 127])
    # Legacy salvage flag: drops exactly-0 labels. Was needed only for the old
    # contaminated datasets (an engine hard-stop bug zeroed ~57% of labels,
    # fixed 2026-07-24). Clean datasets have ~2% genuine-draw zeros - leave off.
    parser.add_argument("--drop-zero-scores", action="store_true")
    # LEGACY IN-RAM PATH ONLY. Features are ~136 bytes per record once decoded,
    # so this cap is really ~16 GB of RAM - an ARCHITECTURAL CEILING that makes
    # the 300-500M position datasets BLOCK 12 targets impossible. It applies
    # only when --no-streaming is passed; the streaming path is bounded by disk.
    parser.add_argument("--max-records", type=int, default=120_000_000,
                        help="in-RAM path only: cap on total records (proportional per-file subsample)")
    # Streaming is the default from v4.0.0: features are decoded once into
    # memory-mapped shards and batches are read straight off the mapping, so
    # dataset size stops being bounded by RAM. --no-streaming keeps the old
    # in-RAM path, which exists so the two can be compared on the same data.
    parser.add_argument("--no-streaming", dest="streaming", action="store_false",
                        help="use the legacy in-RAM path instead of memory-mapped streaming")
    parser.set_defaults(streaming=True)
    parser.add_argument("--chunk", type=int, default=8192,
                        help="streaming: contiguous records per shuffle chunk")
    parser.add_argument("--buffer-chunks", type=int, default=64,
                        help="streaming: chunks held in the shuffle buffer (RAM = chunk*buffer*136B)")
    parser.add_argument("--prefetch", type=int, default=4,
                        help="batches built ahead on a background thread (0 disables); "
                             "batches are identical either way, only faster")
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)

    if args.streaming:
        return train_streaming(args, rng)

    # Count records first so we can size a proportional subsample if (and only
    # if) the combined set exceeds the safety cap.
    sizes = []
    for path in args.data:
        sizes.append(len(dataset.load_records(path)))
    total = sum(sizes)
    ratio = min(1.0, args.max_records / total) if total > 0 else 1.0
    if ratio < 1.0:
        print(f"subsampling {total:,} -> {int(total*ratio):,} records ({ratio*100:.1f}%) to fit under --max-records")

    # Decode each file once (cached next to it), optionally subsample, then split
    # THAT FILE into train/val by a tail cut. Splitting per file - not on the
    # concatenation - is what makes the validation set a representative mix of
    # ALL generations instead of only the last file; a tail cut also keeps whole
    # games on one side (the format orders records by game).
    train_parts = ([], [], [], [])
    val_parts = ([], [], [], [])
    train_total = 0
    val_total = 0
    for path, size in zip(args.data, sizes):
        recs = dataset.load_records(path)
        feats = dataset.precompute_features(recs, cache_path=path + ".features.npz")
        if ratio < 1.0:
            n = max(1, int(size * ratio))
            idx = rng.choice(size, size=n, replace=False)
            idx.sort()
            feats = tuple(a[idx] for a in feats)
        if args.drop_zero_scores:
            # (stm, opp, scores, results); keep only real-signal labels.
            keep = feats[2] != 0
            feats = tuple(a[keep] for a in feats)
        m = len(feats[0])
        vc = int(m * args.val_fraction)
        cut = m - vc
        for k in range(4):
            train_parts[k].append(feats[k][:cut])
            val_parts[k].append(feats[k][cut:])
        train_total += cut
        val_total += vc
        print(f"dataset: {m:,} records from {path}  (train {cut:,} / val {vc:,})")

    train_set = tuple(np.concatenate(train_parts[k]) for k in range(4))
    val_set = tuple(np.concatenate(val_parts[k]) for k in range(4))
    print(f"train: {train_total:,}  val: {val_total:,}  from {len(args.data)} files")

    return run_training(
        args,
        lambda: dataset.batches(None, args.batch, rng, precomputed=train_set),
        lambda: dataset.batches(None, args.batch, np.random.default_rng(0), precomputed=val_set),
        train_total, val_total)


def train_streaming(args, rng):
    """
    v4.0.0 default. Features live in memory-mapped shards and batches are read
    off the mapping, so the dataset is bounded by disk instead of by RAM. The
    120M-record cap of the in-RAM path is not merely raised here, it stops
    existing - which is what makes BLOCK 12's 300-500M position target possible
    at all.
    """
    store = dataset.FeatureStore(args.data, val_fraction=args.val_fraction)
    print(f"train: {store.train_total:,}  val: {store.val_total:,} "
          f"from {len(args.data)} files (streaming, "
          f"chunk={args.chunk} buffer={args.buffer_chunks})")

    if args.drop_zero_scores:
        # The legacy salvage flag filters a resident array, which the streaming
        # path deliberately does not have. It was only ever needed for the
        # pre-2026-07-24 datasets whose labels an engine bug had zeroed.
        raise SystemExit("--drop-zero-scores is not supported with streaming; "
                         "pass --no-streaming, or regenerate the dataset")

    return run_training(
        args,
        lambda: store.stream_batches(args.batch, rng, split="train",
                                     chunk=args.chunk, buffer_chunks=args.buffer_chunks),
        lambda: store.stream_batches(args.batch, np.random.default_rng(0), split="val",
                                     chunk=args.chunk, buffer_chunks=args.buffer_chunks),
        store.train_total, store.val_total)


def run_training(args, make_train_batches, make_val_batches, train_total, val_total):
    """Shared training loop. Both data paths feed it the same batch tuples."""
    steps_per_epoch = max(1, -(-train_total // args.batch))  # ceiling division
    total_steps = args.epochs * steps_per_epoch

    loss_fn = make_loss(args)
    start_lambda = args.lam if args.start_lambda is None else args.start_lambda
    end_lambda = args.lam if args.end_lambda is None else args.end_lambda
    val_lambda = end_lambda
    if start_lambda != end_lambda:
        print(f"lambda schedule: {start_lambda} -> {end_lambda} over {total_steps:,} steps")
    if args.loss_style != "mse":
        print(f"loss: {args.loss_style} (pow {args.pow_exp}, "
              f"in {args.in_offset}/{args.in_scaling}, out {args.out_offset}/{args.out_scaling})")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"device: {device}"
          + (f" ({torch.cuda.get_device_name(0)})" if device.type == "cuda" else " (no CUDA GPU)"))

    model = NoaNnue(args.ft_out, args.l1_out, args.out_buckets, args.factorized,
                    args.qat, args.qa).to(device)
    print(f"net: ft_out={args.ft_out} l1_out={args.l1_out} out_buckets={args.out_buckets} "
          f"factorized={args.factorized} qat={args.qat}"
          + (f" (QA={args.qa}, export as arch {'1' if args.qa == QA else '2/3'})"
             if args.qat else ""))
    # Two parameter groups so the transformer can be decayed differently from
    # the head. With ft_weight_decay equal to weight_decay this is arithmetically
    # identical to one group, which is what keeps the default run unchanged.
    ft_weight_decay = args.weight_decay if args.ft_weight_decay is None else args.ft_weight_decay
    ft_params = [model.ft.weight, model.ft_bias]
    ft_ids = {id(p) for p in ft_params}
    head_params = [p for p in model.parameters() if id(p) not in ft_ids]
    # A parameter silently left out of every group would never be optimised at
    # all, and the loss would still go down because the rest of the net absorbs
    # it. Count them instead of trusting the list comprehension.
    assert len(ft_params) + len(head_params) == len(list(model.parameters())), \
        "parameter groups do not cover the model"
    optimizer = torch.optim.Adam(
        [{"params": ft_params, "weight_decay": ft_weight_decay},
         {"params": head_params, "weight_decay": args.weight_decay}],
        lr=args.lr)
    if ft_weight_decay != args.weight_decay:
        print(f"weight decay: feature transformer {ft_weight_decay}, head {args.weight_decay}")
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs, eta_min=1e-5)

    # Feature arrays stay in host memory (too large for VRAM); each batch is
    # transferred to the GPU just before the forward pass. pin_memory=True on
    # CUDA lets the DMA engine transfer without involving the CPU, so the GPU
    # gets the data faster and the next batch can be prepared in parallel.
    _use_pin = device.type == "cuda"
    def to_dev(a):
        t = torch.from_numpy(np.ascontiguousarray(a))
        if _use_pin:
            t = t.pin_memory()
        return t.to(device, non_blocking=True)

    def evaluate_validation():
        if val_total == 0:
            return float("nan")
        model.eval()
        losses = []
        with torch.no_grad():
            for stm, opp, scores, results in prefetch(make_val_batches(), args.prefetch):
                # Validation uses a FIXED lambda even when training schedules it.
                # A moving objective would make each epoch's number measure a
                # different thing, and "best epoch" would be picking the epoch
                # whose objective happened to be easiest.
                losses.append(loss_fn(model(to_dev(stm), to_dev(opp)),
                                      to_dev(scores), to_dev(results), val_lambda).item())
        model.train()
        return float(np.mean(losses)) if losses else float("nan")

    # A validation split smaller than one batch yields NO batches, so the loss
    # is nan, no epoch ever counts as an improvement, and the checkpoint used to
    # be written with "model": None - silently losing the entire run, which only
    # surfaced later as a crash at export time. Warn here and fall back below.
    if val_total < args.batch:
        print(f"WARNING: validation split ({val_total:,}) is smaller than one batch "
              f"({args.batch:,}); validation loss will be nan and the LAST epoch "
              f"will be saved. Raise --val-fraction or lower --batch.")

    print(f"training: epochs={args.epochs} batch={args.batch} lr={args.lr} lambda={args.lam}")
    start = time.time()

    best_val_loss = float("inf")
    best_state = None
    best_epoch = 0

    for epoch in range(1, args.epochs + 1):
        epoch_losses = []
        for step, (stm, opp, scores, results) in enumerate(
                prefetch(make_train_batches(), args.prefetch)):
            ratio = min(1.0, ((epoch - 1) * steps_per_epoch + step) / max(1, total_steps))
            lam = start_lambda + (end_lambda - start_lambda) * ratio
            loss = loss_fn(model(to_dev(stm), to_dev(opp)),
                           to_dev(scores), to_dev(results), lam)

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

    # Never save a checkpoint without weights. best_state stays None when no
    # epoch improved on the initial infinity - which happens whenever the
    # validation loss is nan (see the warning above), and used to produce a
    # checkpoint carrying "model": None that destroyed the run.
    if best_state is None:
        best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
        best_epoch = args.epochs
        print("note: no epoch improved a measurable validation loss; "
              "saving the final epoch instead of nothing.")

    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    torch.save({"model": best_state,
                "args": vars(args),
                "dataset": args.data}, args.out)
    print(f"saved checkpoint: {args.out} (best epoch {best_epoch}, val {best_val_loss:.6f})")


if __name__ == "__main__":
    main()
