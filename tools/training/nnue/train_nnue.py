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
#
# Continue an interrupted run (or the same run with more --data files) from
# the last finished epoch: the same arguments plus
#       --resume-state checkpoints/run1.pt.state
# A running job is stopped at its next epoch end, state saved, by creating
# checkpoints/run1.pt.stop (it then exits with code 3).

import argparse
import contextlib
import os
import random
import subprocess
import queue
import threading
import time
from pathlib import Path

import numpy as np
import torch

import dataset
from model import (NoaNnue, OUTPUT_SCALE, FT_OUT, L1_OUT, L2_OUT, OUT_BUCKETS,
                   FACTORIZED, QA)


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


def parse_reweight(spec):
    """"a=1.5,b=0.4" -> {"a": 1.5, "b": 0.4}. Empty/None -> {} (no reweighting)."""
    if not spec:
        return {}
    weights = {}
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        substr, _, value = part.partition("=")
        if not value:
            raise SystemExit(f"--reweight: '{part}' is not 'substring=weight'")
        weights[substr.strip()] = float(value)
    return weights


def refuse_to_share_the_machine(args):
    """Stops BEFORE any work when another python job owns the machine.

    Placed at the very top of main on purpose. The first version of this check
    sat next to the device selection, which runs after the feature shards are
    built - so a run that was going to be refused first spent however long the
    decode takes, which for the full corpus is hours. A guard that fires late is
    most of a guard that does not fire.
    """
    if args.force or args.cpu:
        return
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq python.exe"],
                             capture_output=True, text=True, timeout=30).stdout
        others = out.lower().count("python.exe") - 1
    except Exception:
        return                      # no tasklist, no opinion
    if others > 0:
        raise SystemExit(
            f"ABORTADO: hay {others} proceso(s) python mas en la maquina. Un "
            f"entrenamiento que comparte GPU puede morir con out-of-memory horas "
            f"despues de empezar - le paso a un ensayo lanzado mientras fqc120 iba "
            f"por la epoca 89 de 120. Espera, o pasa --force, o --cpu para un ensayo.")


# Splits whose validation rows the other family trains on: at one fraction, a
# tail-family validation set (tail, tail-dedup) holds no row a tail-family run
# trained on, and a hash one none a hash run did; across families about 95% of
# it was trained on.
_SPLIT_FAMILY = {"tail": "tail", "tail-dedup": "tail", "hash": "hash"}


def _norm_path(path):
    # One spelling per file, so a list compares equal across working
    # directories and drive-letter case.
    return os.path.normcase(os.path.abspath(path))


def _split_of(prev_args, fallback_fraction):
    """(val_split, val_fraction) a checkpoint's args were trained with.
    Checkpoints older than the option carry no val_split, and every one of
    them was trained on the tail split."""
    return prev_args.get("val_split", "tail"), prev_args.get("val_fraction", fallback_fraction)


def refuse_a_leaking_warm_start(args):
    """Stops BEFORE any work a warm start that would validate on rows the
    starting net trained on.

    The case it exists for: a multi-day run cut by a reboot, as on 2026-09-09,
    continued with --init-from <out>.partial and without the --val-split or
    --val-fraction it was started with. The continuation would score itself on
    rows its starting weights trained on and keep the checkpoint that
    memorised best, with nothing in the log to say so. Checked only when the
    checkpoint shares a data file with this run: a net from another corpus
    says nothing about these rows. Checkpoints older than the option carry no
    val_split, and every one of them was trained on the tail split.
    """
    if not args.init_from:
        return
    start = torch.load(args.init_from, map_location="cpu", weights_only=False)
    prev = start.get("args") if isinstance(start, dict) else None
    if not isinstance(prev, dict):
        return

    def norm(paths):
        return {_norm_path(p) for p in (paths or [])}
    if not norm(prev.get("data")) & norm(args.data):
        return
    prev_split, prev_fraction = _split_of(prev, args.val_fraction)
    if (_SPLIT_FAMILY.get(prev_split) != _SPLIT_FAMILY[args.val_split]
            or prev_fraction != args.val_fraction):
        raise SystemExit(
            f"--init-from {args.init_from} was trained with --val-split {prev_split} "
            f"--val-fraction {prev_fraction} on files this run uses too; this run asks for "
            f"--val-split {args.val_split} --val-fraction {args.val_fraction}, and would "
            f"validate on rows the starting net trained on. Pass the checkpoint's split "
            f"and fraction (tail and tail-dedup are interchangeable).")


# EXACT RESUME (2026-10-01). After every epoch <out>.state is rewritten with
# everything the next epoch depends on: the current weights (not the best),
# Adam's moments, the cosine scheduler, the lambda ramp's position, the best
# so far, and every RNG the loop draws from. --resume-state restores all of it
# and carries on at the next epoch, so a run cut by a crash or a reboot ends
# with the same weights it would have had uninterrupted - checked bit for bit
# on CPU by the resume test. --init-from could only approximate that: fresh
# Adam moments, a new cosine over the remaining epochs and a lambda ramp
# re-entered by hand.
#
# The --data list may change at the boundary (that is how a corpus generated
# while the run trains joins it): the schedules carry on, and the best-epoch
# tracking starts over because the validation set is no longer the one the
# saved best was measured on. A change that would put rows the net trained on
# into validation is refused (check_resume_data).
TRAIN_STATE_FORMAT = "noa-train-state-1"

# What a resume must repeat exactly. The architecture, because the saved
# tensors fit only the net they came from. The schedule - epochs, lr and the
# lambda ramp - because the restored scheduler and ramp carry on with the
# saved values while the log and the final checkpoint's args would claim the
# new ones; weight decay for the same reason, since loading the optimizer
# state restores the saved one. The loss and the batch, because the best-epoch
# comparison and what one Adam step means depend on them. The seed and the
# in-RAM data options, so the checkpoint's args stay a true record of the
# run. chunk, buffer_chunks, prefetch, loader_workers, loader_process, out,
# cpu and force are free.
_RESUME_ARCH = ("ft_out", "l1_out", "out_buckets", "factorized", "qat", "qa",
                "threats", "coarse", "dual", "l2_out", "psqt_buckets")
_RESUME_FIXED = ("epochs", "lr", "batch", "weight_decay", "loss_style", "in_offset",
                 "in_scaling", "out_offset", "out_scaling", "pow_exp", "seed",
                 "streaming", "max_records", "drop_zero_scores")


def _effective(a, key):
    # The lambda ends and the transformer's decay default to another option;
    # compared as the values the run actually used, so spelling the default
    # out (or not) is not a mismatch.
    fallback = {"start_lambda": "lam", "end_lambda": "lam", "ft_weight_decay": "weight_decay"}
    return a[fallback[key]] if a.get(key) is None else a[key]


def _flag(key):
    return {"lam": "--lambda", "streaming": "--no-streaming"}.get(key, "--" + key.replace("_", "-"))


# Exit code of a run stopped at an epoch boundary (--stop-after-epoch or
# <out>.stop). Not 0: the wrappers (Noa-DataScale-Train.ps1) read 0 as a
# finished run and go on to validate and export <out>, which a stopped run
# has not written - or which is a stale one from an earlier run of the name.
EXIT_STOPPED = 3


def optimizer_groups(model):
    """(feature transformer parameters, head parameters): Adam's two groups,
    in the order it numbers their moments. One definition for the run and
    for the resume check, so the check compares what the run builds."""
    ft_params = [model.ft.weight, model.ft_bias]
    ft_ids = {id(p) for p in ft_params}
    return ft_params, [p for p in model.parameters() if id(p) not in ft_ids]


def optimizer_param_names(model):
    # Adam's saved moments are matched to the parameters by POSITION, not by
    # name, so the names in that order are saved with the state and compared.
    names = {id(p): n for n, p in model.named_parameters()}
    return [names[id(p)] for group in optimizer_groups(model) for p in group]


def _tail_train_rows(count, val_fraction):
    # A file's training rows under the tail splits: dataset.FeatureStore's cut.
    return count - int(count * val_fraction)


def record_counts(paths):
    """(normalised path, records) for each file that holds any, from the
    headers alone - the counts FeatureStore will find, since it rebuilds the
    shards of a file newer than them - known before any shard is built. An
    empty file is left out, as FeatureStore leaves it out."""
    out = []
    for p in paths:
        try:
            n = len(dataset.load_records(p))
        except (OSError, ValueError) as exc:
            raise SystemExit(f"--data {p}: cannot be read ({exc})")
        if n:
            out.append((_norm_path(p), n))
    return out


def refuse_missing_data(args):
    """Stops BEFORE any work when a --data file, or with --coarse its
    companion, is not there. FeatureStore finds out only on reaching that
    file, after building the shards of every file listed before it - on a
    launch that adds files, the new files' decode."""
    problems = []
    for p in args.data:
        if not os.path.isfile(p):
            problems.append(f"--data {p}: no such file")
        elif args.coarse and not os.path.isfile(dataset.coarse_companion_for(p)):
            problems.append(f"--data {p}: no coarse companion {dataset.coarse_companion_for(p)} "
                            f"(run Noa-CoarseEncodeAll.ps1 first)")
    if problems:
        raise SystemExit("missing data:\n  " + "\n  ".join(problems))


def refuse_to_overwrite_a_run(args):
    """Stops BEFORE any work a fresh run whose --out holds an unfinished one.

    The case: a reboot on day 3 and the launch .bat run again as it was,
    without --resume-state. It would start over at epoch 1 and, an epoch
    later, replace the day-3 state with epoch 1's - and the .partial with it,
    since a first epoch always improves on nothing. A finished run (every
    epoch done and its final checkpoint on disk) may be overwritten, as any
    run always could, so a deliberate retrain under the same name still goes.
    """
    path = args.out + ".state"
    if args.resume_state or not os.path.exists(path):
        return
    try:
        saved = torch.load(path, map_location="cpu", weights_only=False)
        done, total = saved["epoch"], saved["args"]["epochs"]
    except Exception as exc:  # noqa: BLE001 - reported, the user decides
        raise SystemExit(f"{path} exists and cannot be read as a training state ({exc}); "
                         f"move it away to start a new run with --out {args.out}")
    if done < total or not os.path.exists(args.out):
        raise SystemExit(
            f"{path} holds an unfinished run ({done} of {total} epochs"
            + ("" if done < total else ", no final checkpoint") + "): continue it with the same "
            f"arguments plus --resume-state {path}, or move it away to start over")


def load_resume_state(args):
    """Loads --resume-state and refuses, BEFORE any work, a resume that would
    not continue the run the state was saved from.

    Same placement argument as refuse_to_share_the_machine: the feature
    shards, the coarse companions and the dedup masks of a full corpus take
    hours to build, and a refusal after them is most of a refusal wasted.
    Everything here reads only the state file and builds the net once on the
    CPU to compare tensor shapes and Adam's parameter order, which also
    catches a model.py that changed under a run whose switches did not.
    """
    if not args.resume_state:
        return None
    try:
        state = torch.load(args.resume_state, map_location="cpu", weights_only=False)
    except Exception as exc:  # noqa: BLE001 - reported, nothing to resume from
        raise SystemExit(f"--resume-state {args.resume_state}: cannot be read ({exc})")
    if not isinstance(state, dict) or state.get("format") != TRAIN_STATE_FORMAT:
        # The likely mistake: the .partial or the final checkpoint, which hold
        # weights only - resuming from them is what --init-from approximates.
        raise SystemExit(f"--resume-state {args.resume_state} is not a training state written "
                         f"by this trainer; pass <out>.state (a .partial or a final checkpoint "
                         f"holds only weights)")
    prev = state["args"]
    now = vars(args)
    problems = []
    for key in _RESUME_ARCH + _RESUME_FIXED:
        if prev.get(key) != now[key]:
            problems.append(f"{_flag(key)}: the state has {prev.get(key)!r}, this run {now[key]!r}")
    for key in ("start_lambda", "end_lambda", "ft_weight_decay"):
        if _effective(prev, key) != _effective(now, key):
            problems.append(f"{_flag(key)}: the state has {_effective(prev, key)!r}, "
                            f"this run {_effective(now, key)!r}")
    # The cosine's length lives in the scheduler, not only in the args: a
    # different T_max would silently reshape every learning rate still to come.
    t_max = state["scheduler"].get("T_max")
    if t_max != args.epochs:
        problems.append(f"--epochs: the saved cosine runs over {t_max} epochs, this run asks "
                        f"for {args.epochs}")
    # Stricter than the warm-start guard: the same split AND the same mode.
    # tail and tail-dedup train on the same rows but validate on different
    # ones, and the restored best was measured on the saved one.
    if (state["val_split"], state["val_fraction"]) != (args.val_split, args.val_fraction):
        problems.append(f"--val-split/--val-fraction: the state has {state['val_split']} "
                        f"{state['val_fraction']}, this run {args.val_split} {args.val_fraction}")
    if args.stop_after_epoch is not None and args.stop_after_epoch <= state["epoch"]:
        problems.append(f"--stop-after-epoch {args.stop_after_epoch}: the state has already "
                        f"finished epoch {state['epoch']}")
    # Switches that differ are already reported; with equal ones the shapes
    # still have to agree, or model.py changed under the run.
    if all(prev.get(key) == now[key] for key in _RESUME_ARCH):
        try:
            with torch.random.fork_rng(devices=[]):
                probe = NoaNnue(args.ft_out, args.l1_out, args.out_buckets, args.factorized,
                                args.qat, args.qa, threats=args.threats, dual=args.dual,
                                l2_out=args.l2_out, psqt_buckets=args.psqt_buckets,
                                coarse=args.coarse)
        except ValueError as exc:
            raise SystemExit(f"--resume-state: this net cannot be built ({exc})")
        want = {k: tuple(v.shape) for k, v in probe.state_dict().items()}
        have = {k: tuple(v.shape) for k, v in state["model"].items()}
        if want != have:
            differ = sorted(k for k in want.keys() | have.keys() if want.get(k) != have.get(k))
            problems.append(f"architecture: the saved tensors do not fit this net "
                            f"({len(differ)} differ, e.g. "
                            + ", ".join(f"{k} {have.get(k)} vs {want.get(k)}" for k in differ[:3])
                            + ")")
        else:
            # Same names and shapes, and still Adam's moments land on the
            # wrong tensors if model.py now registers them in another order:
            # a crash at the first step, after the data is built, or no crash
            # at all when the swapped tensors share a shape.
            names = optimizer_param_names(probe)
            flat = [p for group in optimizer_groups(probe) for p in group]
            ids = [i for g in state["optimizer"]["param_groups"] for i in g["params"]]
            moments = state["optimizer"]["state"]
            off = [names[k] for k, (i, p) in enumerate(zip(ids, flat))
                   if "exp_avg" in moments.get(i, {})
                   and tuple(moments[i]["exp_avg"].shape) != tuple(p.shape)]
            saved_names = state.get("optimizer_params")
            if len(ids) != len(flat) or off or (saved_names is not None and saved_names != names):
                problems.append(f"optimizer: the saved Adam moments follow the parameters in "
                                f"another order than this model.py registers them "
                                f"({len(ids)} saved, {len(flat)} now"
                                + (f"; misshapen for {', '.join(off[:3])}" if off else "") + ")")
    if problems:
        raise SystemExit(f"--resume-state {args.resume_state} would not continue the run it "
                         f"was saved from:\n  " + "\n  ".join(problems))
    if (prev.get("chunk"), prev.get("buffer_chunks")) != (args.chunk, args.buffer_chunks):
        print(f"resume: --chunk/--buffer-chunks {prev.get('chunk')}/{prev.get('buffer_chunks')} "
              f"-> {args.chunk}/{args.buffer_chunks}: allowed, but the batches stop being "
              f"the ones an uninterrupted run would have drawn")
    return state


def check_resume_data(args, state):
    """Says how --data differs from the state's and refuses, BEFORE any work,
    a change that would validate on rows the net has already trained on.

    Compared by the absolute paths and record counts the state saved - the
    paths as typed would resolve against whatever folder the resume runs in.
    Under the tail splits a file's validation rows are its tail, so two
    changes leak (the hash split is immune, a row's side follows its
    position): a file now SHORTER than any epoch trained it, whose new tail
    is rows the net trained on; and, under tail-dedup, a REMOVED file, whose
    training positions stop being dropped from the other files' tails.
    Adding a file, or growing one, moves rows from validation into training
    only. --accept-val-leak goes ahead anyway, e.g. to drop a corrupt file.
    """
    old = [tuple(x) for x in state["data_counts"]]
    new = record_counts(args.data)
    before, now = dict(old), dict(new)
    typed = {_norm_path(p): p for p in args.data}
    if old != new or state["reweight"] != args.reweight:
        print(f"resume: the data differs from the state's (epochs {state['epoch'] + 1}+ "
              f"train on the new list):")
        for p, _ in new:
            if p not in before:
                print(f"  + {typed[p]}")
        for p, _ in old:
            if p not in now:
                print(f"  - {p}")
        for p, count in new:
            if p in before and before[p] != count:
                print(f"  ~ {typed[p]}: {before[p]:,} -> {count:,} records")
        if sorted(old) == sorted(new) and old != new:
            print("  same files in another order")
        if state["reweight"] != args.reweight:
            print(f"  --reweight {state['reweight']!r} -> {args.reweight!r}")
    if _SPLIT_FAMILY[args.val_split] != "tail":
        return
    # Every file any earlier epoch trained on, with the most rows it trained
    # on: a file dropped at one resume and brought back shorter at the next
    # leaks the same way.
    trained = state.get("trained") or {p: _tail_train_rows(c, args.val_fraction) for p, c in old}
    leaks = []
    for p, rows in trained.items():
        cut = _tail_train_rows(now[p], args.val_fraction) if p in now else None
        if cut is not None and cut < rows:
            leaks.append(f"{typed.get(p, p)}: rows {cut:,}-{min(rows, now[p]):,} are its "
                         f"validation tail now and were trained on")
        elif cut is None and p in before and args.val_split == "tail-dedup":
            leaks.append(f"{p}: removed, so the other files' tail rows that repeat one of its "
                         f"{rows:,} trained positions return to validation")
    if leaks and not args.accept_val_leak:
        raise SystemExit("this resume would validate on rows the net trained on "
                         f"(--val-split {args.val_split}):\n  " + "\n  ".join(leaks)
                         + "\nKeep the files as they were, or pass --accept-val-leak to go ahead "
                           "with a validation set that flatters overfit epochs.")
    for line in leaks:
        print(f"WARNING (--accept-val-leak): {line}")


def ramp_ratio(ramp, epoch, step):
    """Position of the lambda ramp, 0..1, at 'step' of 'epoch'.

    Kept as an exact fraction of integers, (num + steps * per_step) / den.
    A fresh run has num 0, per_step 1 and den the run's total steps, which is
    the expression the loop always used, to the bit. continue_ramp re-bases
    it when a resume changes the epoch length.
    """
    steps = (epoch - 1 - ramp["base_epoch"]) * ramp["steps_per_epoch"] + step
    return min(1.0, (ramp["num"] + steps * ramp["per_step"]) / ramp["den"])


def continue_ramp(ramp, done_epochs, epochs, steps_per_epoch):
    """The ramp re-based at an epoch boundary for a new epoch length.

    Added data makes every later epoch longer, and the old step arithmetic
    would then jump the ramp at the boundary. This one starts from exactly
    where the saved one stopped and reaches the end exactly at the last step.
    Integers, so that a re-base to an unchanged length is the same fraction
    and Python's int division rounds it to the same float.
    """
    at = ramp["num"] + (done_epochs - ramp["base_epoch"]) * ramp["steps_per_epoch"] * ramp["per_step"]
    left = max(1, (epochs - done_epochs) * steps_per_epoch)
    return {"base_epoch": done_epochs, "steps_per_epoch": steps_per_epoch,
            "num": at * left, "per_step": ramp["den"] - at, "den": ramp["den"] * left}


def rng_states(rng, device):
    """Every RNG the loop can draw from. The numpy Generator is the one that
    matters: stream_batches is handed the SAME object every epoch and keeps
    advancing it, so restoring it is what makes the next epoch's shuffle the
    one an uninterrupted run would have drawn. torch's are restored too
    although nothing draws from them after the init today, and CUDA's only
    when the run is on the GPU, so a --cpu test never wakes the device."""
    return {"numpy": rng.bit_generator.state if rng is not None else None,
            "torch": torch.get_rng_state(),
            "cuda": torch.cuda.get_rng_state_all() if device.type == "cuda" else None,
            "python": random.getstate()}


def restore_rng_states(saved, rng, device):
    if rng is not None and saved["numpy"] is not None:
        rng.bit_generator.state = saved["numpy"]
    torch.set_rng_state(saved["torch"])
    if device.type == "cuda" and saved["cuda"] is not None:
        try:
            torch.cuda.set_rng_state_all(saved["cuda"])
        except Exception as exc:  # noqa: BLE001 - another GPU count; nothing draws from it
            print(f"note: CUDA RNG state not restored ({exc})")
    random.setstate(saved["python"])


def write_train_state(path, state):
    """Replaces 'path' with 'state' so that a crash at any moment leaves the
    previous file or the new one whole, never a torn one.

    Written to a temporary file beside it, flushed to the disk (a reboot is
    the case this exists for, and a rename can outlive data still in the
    cache), and only then moved over the old one by one os.replace. On
    Windows that move fails while another process holds the target open - a
    reader, an antivirus scan - so it is retried for a few seconds.
    """
    tmp = path + ".tmp"
    try:
        with open(tmp, "wb") as fh:
            torch.save(state, fh)
            fh.flush()
            os.fsync(fh.fileno())
    except BaseException:
        with contextlib.suppress(OSError):
            os.remove(tmp)
        raise
    for attempt in range(20):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            if attempt == 19:
                raise
            time.sleep(0.25)


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
    # How the validation rows are chosen (2026-10-01). "tail" is what every net
    # so far used: the last val-fraction of each file. It keeps whole games on
    # one side but not whole positions, and the same position recurs across
    # files: 5.48% of one shard's validation tail was found verbatim among the
    # training rows of other files (2026-09-23), which biases the best-val
    # checkpoint toward overfit epochs. "tail-dedup" keeps the tail cut and
    # drops from validation every tail row whose position (the HalfKA feature
    # sets of both perspectives) is a training row of any file: training is
    # exactly the tail split's. "hash" puts a row in validation iff a hash of
    # its position says so, wherever it occurs - which splits every game
    # across both sides (99.99% of its validation rows share a game, and the
    # game result, with training rows) and reads the whole corpus for each
    # validation pass; kept for comparison. Masks are cached per file next to
    # the feature shards. Streaming path only.
    parser.add_argument("--val-split", choices=list(dataset.VAL_SPLITS), default="tail",
                        help="tail: last val-fraction of each file (default); tail-dedup: the "
                             "same, minus tail rows whose position is a training row of any "
                             "file; hash: by position hash (splits games across both sides)")
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
    # Arch 4: adds the threat feature transformer. Needs its own shard cache,
    # built on first use and reused after (about 5.4 h for the full corpus), and
    # exports as --arch 4. A run without this flag never touches that cache.
    parser.add_argument("--dual", action="store_true",
                        help="architecture 5: pairwise transformer read, squared "
                             "activations, a second hidden layer the output reads "
                             "past, and a linear bypass. Changes the shape of l1 "
                             "and out, so a checkpoint trained with it can only be "
                             "exported as --arch 5.")
    parser.add_argument("--l2-out", type=int, default=L2_OUT,
                        help="second hidden layer width (--dual only)")
    parser.add_argument("--psqt-buckets", type=int, default=0,
                        help="psqt head buckets (two-headed net); 0 disables")
    parser.add_argument("--threats", action="store_true",
                        help="train the threat feature transformer as well (arch 4)")
    parser.add_argument("--coarse", action="store_true",
                        help="train the 144-bucket coarse threat lane (needs the "
                             ".coarsedata companions from DataGen coarse-encode)")
    # Two jobs on one GPU end an eighteen-hour run with an out-of-memory hours
    # in, and the cost of finding out is the whole run. This already happened
    # to a smoke test launched while fqc120 was 89 epochs into 120: cuBLAS
    # refused to initialise and the test died. The probe has carried this guard
    # since; the trainer, which has far more to lose, did not.
    parser.add_argument("--force", action="store_true",
                        help="train even if another python process is on the machine")
    parser.add_argument("--cpu", action="store_true",
                        help="force CPU; for smoke tests while a GPU job runs")
    parser.add_argument("--qat", action="store_true")
    parser.add_argument("--init-from", default=None,
                        help="Checkpoint whose weights start this run. The\n"
                             "architecture switches must match the checkpoint;\n"
                             "the optimizer and the schedules start fresh, so a\n"
                             "continued run passes --lr and --start-lambda at the\n"
                             "values the interrupted schedule had reached. To\n"
                             "continue an interrupted run exactly, use\n"
                             "--resume-state instead.")
    # Exact resume: see TRAIN_STATE_FORMAT. A resume passes the arguments the
    # run was started with; only --data (and --reweight) may change.
    parser.add_argument("--resume-state", default=None,
                        help="<out>.state of an interrupted run: restore the weights, optimizer, "
                             "scheduler, lambda ramp, best so far and RNGs, and continue at the "
                             "next epoch. The other arguments must be the run's own; --data may "
                             "add files (dropping or shortening one is refused where it would "
                             "leak into validation). Excludes --init-from.")
    # Ends the run at an epoch boundary of its schedule, as a crash there would,
    # minus the crash: the state of that epoch is on disk and no final
    # checkpoint is written. The way to stop a run on purpose to add data; a
    # run already going is stopped the same way by creating <out>.stop.
    parser.add_argument("--stop-after-epoch", type=int, default=None,
                        help="end the run cleanly after this epoch of the schedule (its .state "
                             "saved, no final checkpoint, exit code 3); continue it with "
                             "--resume-state. Creating <out>.stop does the same at the next "
                             "epoch end.")
    # A resume refuses a data change that would put trained rows into
    # validation (see check_resume_data); this goes ahead regardless.
    parser.add_argument("--accept-val-leak", action="store_true",
                        help="let --resume-state shorten or (under tail-dedup) remove a file "
                             "although validation then holds rows the net trained on")
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
    # Reader threads of the streaming loader, and the loader in a process of
    # its own (2026-10-02). 0 and off keep the loader on the prefetch thread
    # alone. The batches are identical either way, byte for byte and in the
    # same order, and so is the rng a resume saves (dataset._ChunkReader,
    # dataset.LoaderProcess), so like --prefetch both may change on a resume.
    # Pair them with a deeper --prefetch (32): each buffer is still shuffled
    # in one go, and four queued batches do not cover that pause.
    parser.add_argument("--loader-workers", type=int, default=0,
                        help="streaming: threads reading chunks ahead (0 = none); "
                             "batches are identical either way, only faster")
    parser.add_argument("--loader-process", action="store_true",
                        help="streaming: run the loader in a child process, off the "
                             "trainer's GIL; batches are identical either way")
    # Per-source sampling weight (2026-09-18): without this, each --data file's
    # share of every epoch is whatever its on-disk record count happens to be,
    # so a newer/smaller/higher-quality generation is silently outnumbered by
    # an older/bigger one. Format: "substr1=weight1,substr2=weight2", the first
    # substring found in a path's name sets that file's weight (default 1.0).
    # Example: --reweight "datascale2=0.4,datascale4=1.5" leans the epoch
    # toward datascale4 without dropping datascale2 entirely.
    parser.add_argument("--reweight", type=str, default="",
                        help='per-source sampling weight, e.g. "datascale2=0.4,datascale4=1.5"')
    args = parser.parse_args()
    if args.resume_state and args.init_from:
        # Two answers to where the weights come from: a resume restores the
        # optimizer and both schedules with them, a warm start restarts all
        # three. Picking one of them silently would be a guess.
        raise SystemExit("--resume-state and --init-from are mutually exclusive: "
                         "--resume-state continues a run exactly, --init-from starts a "
                         "new one from a checkpoint's weights")
    if args.stop_after_epoch is not None and not 1 <= args.stop_after_epoch <= args.epochs:
        raise SystemExit(f"--stop-after-epoch {args.stop_after_epoch} is not an epoch of "
                         f"this run (1..{args.epochs})")
    refuse_to_share_the_machine(args)
    refuse_a_leaking_warm_start(args)
    resume = load_resume_state(args)
    refuse_missing_data(args)
    if resume is not None:
        check_resume_data(args, resume)
    refuse_to_overwrite_a_run(args)
    # <out>.stop asks the run to end at an epoch boundary (see run_training).
    # One already there was meant for an earlier process. Removed HERE, before
    # the shards and the dedup masks are built - hours on a launch that adds
    # files - so that one created during that build is honoured, not taken
    # for a leftover.
    stop_path = os.path.abspath(args.out + ".stop")
    if os.path.exists(stop_path):
        os.remove(stop_path)
        print(f"note: removed {stop_path}, left over from before this run")
    print(f"to stop at the next epoch end, state saved: create {stop_path}", flush=True)

    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)

    if args.streaming:
        return train_streaming(args, rng, resume)

    if args.coarse:
        # The coarse companions ride the streaming shards; the in-RAM path
        # never learned about them and would unpack the wrong tuple shape.
        raise SystemExit("--coarse requires the streaming path (drop --no-streaming)")
    if args.val_split != "tail":
        # The in-RAM path below only knows the tail cut; ignoring the flag would
        # train with the leaky split while the checkpoint's args claim otherwise.
        raise SystemExit(f"--val-split {args.val_split} requires the streaming path "
                         f"(drop --no-streaming)")

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
        train_total, val_total, rng=rng,
        data_counts=[(_norm_path(p), s) for p, s in zip(args.data, sizes)], resume=resume)


def train_streaming(args, rng, resume=None):
    """
    v4.0.0 default. Features live in memory-mapped shards and batches are read
    off the mapping, so the dataset is bounded by disk instead of by RAM. The
    120M-record cap of the in-RAM path is not merely raised here, it stops
    existing - which is what makes BLOCK 12's 300-500M position target possible
    at all.
    """
    store = dataset.FeatureStore(args.data, val_fraction=args.val_fraction,
                                threats=args.threats, coarse=args.coarse,
                                weights=parse_reweight(args.reweight),
                                val_split=args.val_split)
    split_note = ""
    if args.val_split != "tail":
        total = store.train_total + store.val_total
        split_note = (f", {args.val_split} split: val {store.val_total / max(1, total):.3%} "
                      f"of rows for a target of {args.val_fraction:.3%}")
    print(f"train: {store.train_total:,}  val: {store.val_total:,} "
          f"from {len(args.data)} files (streaming, "
          f"chunk={args.chunk} buffer={args.buffer_chunks}{split_note})")

    if args.drop_zero_scores:
        # The legacy salvage flag filters a resident array, which the streaming
        # path deliberately does not have. It was only ever needed for the
        # pre-2026-07-24 datasets whose labels an engine bug had zeroed.
        raise SystemExit("--drop-zero-scores is not supported with streaming; "
                         "pass --no-streaming, or regenerate the dataset")

    # --reweight resamples each epoch's chunks, so an epoch is not train_total
    # rows; the lambda ramp is sized from what the stream draws, or it would
    # overrun (weights above 1) or fall short of (below 1) each epoch's share.
    epoch_rows = store.train_rows_per_pass(args.chunk)
    if epoch_rows != store.train_total:
        print(f"--reweight: about {epoch_rows:,} training rows drawn per epoch")

    # --loader-process: the same stream_batches, run in a child process that
    # opens the same store (dataset.LoaderProcess); same batches, same rng.
    loader = store
    if args.loader_process:
        started = time.time()
        loader = dataset.LoaderProcess(store)
        print(f"loader process started ({time.time() - started:.1f} s), "
              f"{args.loader_workers} reader threads in it", flush=True)

    # The train stream is handed the same rng every epoch and advances it, so
    # run_training saves and restores that object; the validation stream gets
    # a fresh rng(0) each pass and needs nothing.
    try:
        return run_training(
            args,
            lambda: loader.stream_batches(args.batch, rng, split="train",
                                          chunk=args.chunk, buffer_chunks=args.buffer_chunks,
                                          workers=args.loader_workers),
            lambda: loader.stream_batches(args.batch, np.random.default_rng(0), split="val",
                                          chunk=args.chunk, buffer_chunks=args.buffer_chunks,
                                          workers=args.loader_workers),
            store.train_total, store.val_total, rng=rng,
            data_counts=[(_norm_path(f["path"]), f["count"]) for f in store.files],
            resume=resume, epoch_rows=epoch_rows)
    finally:
        if loader is not store:
            loader.close()


def split_batch(batch, coarse=False):
    """(stm, opp, scores, results, stm_t, opp_t, stm_c, opp_c).

    One function for both the training and the validation loop on purpose: two
    copies of this unpacking is how one of them ends up feeding threats and the
    other not, which would make the validation number measure a different model
    than the one being trained.

    The coarse flag is explicit rather than inferred: a coarse batch and a
    threat batch are both six arrays, and guessing by length is exactly the
    silent mispairing this function exists to prevent. The perspective views
    are derived here from the absolute ids and the stored side to move, so
    both loops feed the model identically.
    """
    if coarse:
        # The absolute ids and the side to move travel as they are; the
        # per-perspective views are built ON THE DEVICE by coarse_views (a
        # 145-entry gather), because the numpy flip measured 48-53 ms per
        # batch on the training thread - 96% of the loader and serialized
        # with the GPU step - which is what held the coarse epoch at 92 min.
        stm, opp, scores, results, cabs, cstm = batch
        return stm, opp, scores, results, None, None, cabs, cstm
    if len(batch) == 6:
        stm, opp, scores, results, stm_t, opp_t = batch
        return stm, opp, scores, results, stm_t, opp_t, None, None
    stm, opp, scores, results = batch
    return stm, opp, scores, results, None, None, None, None



# Device-side twin of dataset.coarse_perspectives. Table entry k+1 holds the
# black view of absolute id k and entry 0 holds the pad (-1), so the gather
# never sees a negative index. Checked once per run against the numpy
# reference on real data, so the two definitions cannot drift apart silently.
_coarse_flip_cache = {}


def coarse_flip_table(device):
    key = str(device)
    if key not in _coarse_flip_cache:
        ids = np.arange(144)
        att, vic = ids // 12, ids % 12
        flip = ((att + 6) % 12) * 12 + (vic + 6) % 12
        table = np.concatenate([np.array([-1], np.int32), flip.astype(np.int32)])
        _coarse_flip_cache[key] = torch.from_numpy(table).to(device)
    return _coarse_flip_cache[key]


def coarse_views(cabs_t, cstm_t):
    black = coarse_flip_table(cabs_t.device)[cabs_t + 1]
    is_black = (cstm_t != 0).unsqueeze(1)
    return torch.where(is_black, black, cabs_t), torch.where(is_black, cabs_t, black)


def batch_problems(model, stm, opp, stm_t, opp_t, cabs, cstm):
    """Everything the device would assert on, checked on the host first.

    Three runs of the fqcohuman training died inside a CUDA kernel with a
    device-side assert, which is what an index gather reports when a feature
    index lies outside its table - and a dead CUDA context cannot be caught
    or resumed, so the run is over at that point with nothing to show which
    batch did it. The tables and their bounds: HalfKA rows below pad_index
    (-1 is the pad and is mapped, anything past the pad row is not), threat
    rows below threat_pad, coarse ids -1..143 (the flip table has 145
    entries and is indexed by id + 1), the coarse side to move 0 or 1, and
    every stream with the same number of rows. Costs a few min/max
    reductions over int16 arrays, well under a millisecond per batch.
    """
    problems = []
    rows = {len(stm), len(opp), len(cabs) if cabs is not None else len(stm),
            len(cstm) if cstm is not None else len(stm),
            len(stm_t) if stm_t is not None else len(stm),
            len(opp_t) if opp_t is not None else len(stm)}
    if len(rows) != 1:
        problems.append(f"streams disagree on rows: {sorted(rows)}")
    for name, a, hi in (("stm", stm, model.pad_index), ("opp", opp, model.pad_index),
                        ("stm_t", stm_t, getattr(model, "threat_pad", None)),
                        ("opp_t", opp_t, getattr(model, "threat_pad", None)),
                        ("coarse", cabs, 144)):
        if a is None or a.size == 0:
            continue
        lo, top = int(a.min()), int(a.max())
        if lo < -1 or top >= hi:
            problems.append(f"{name} indices in [{lo}, {top}], table allows -1..{hi - 1}")
    if cstm is not None and cstm.size and (int(cstm.min()) < 0 or int(cstm.max()) > 1):
        problems.append(f"coarse side to move in [{int(cstm.min())}, {int(cstm.max())}]")
    return problems


def check_coarse_views_once(cabs, cstm, stm_c_t, opp_c_t):
    ref_stm, ref_opp = dataset.coarse_perspectives(cabs, cstm)
    got_stm = stm_c_t.detach().cpu().numpy()
    got_opp = opp_c_t.detach().cpu().numpy()
    if not (np.array_equal(ref_stm, got_stm) and np.array_equal(ref_opp, got_opp)):
        raise SystemExit("coarse_views on the device disagrees with dataset.coarse_perspectives")
    print("coarse views: device flip verified identical to the numpy reference")

def run_training(args, make_train_batches, make_val_batches, train_total, val_total,
                 rng=None, data_counts=None, resume=None, epoch_rows=None):
    """Shared training loop. Both data paths feed it the same batch tuples.

    rng is the Generator make_train_batches shuffles with, saved in the
    resume state; data_counts lists (file, records) to tell a resume whether
    its data changed; resume is what load_resume_state returned, or None;
    epoch_rows is what one training pass draws when --reweight makes it
    differ from train_total.
    """
    epoch_rows = train_total if epoch_rows is None else epoch_rows
    steps_per_epoch = max(1, -(-epoch_rows // args.batch))  # ceiling division
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

    device = torch.device("cpu" if args.cpu
                          else ("cuda" if torch.cuda.is_available() else "cpu"))
    print(f"device: {device}"
          + (f" ({torch.cuda.get_device_name(0)})" if device.type == "cuda" else " (no CUDA GPU)"))

    # Architecture 5 is an int8 head, and int8 forces QA=127 for the VPMADDUBSW
    # lane to stay exact. Training against QA=255 and exporting at 127 would
    # optimise arithmetic the engine never runs, which has already contaminated
    # one measurement in this project through the export default.
    if args.dual and args.qa != 127:
        raise SystemExit("--dual is an int8 architecture: pass --qa 127 "
                         f"(got {args.qa}), or the export will quantize to a "
                         "different grid from the one you trained on.")

    model = NoaNnue(args.ft_out, args.l1_out, args.out_buckets, args.factorized,
                    args.qat, args.qa, threats=args.threats,
                    dual=args.dual, l2_out=args.l2_out, psqt_buckets=args.psqt_buckets,
                    coarse=args.coarse).to(device)

    # Warm start (--init-from). A 60-epoch run is 40 hours here, and until this
    # existed an interrupted one could only be started over: the reboot of
    # 2026-09-09 killed fqcohuman at epoch 39 of 60 with three days of GPU in
    # it. The .partial checkpoint written every improving epoch already holds
    # the best weights, so they are loaded into the fresh model and training
    # continues from there.
    #
    # What this does NOT restore: the optimizer state, the cosine learning-rate
    # schedule and the lambda ramp all start over. A continuation therefore has
    # to be launched with --lr and --start-lambda set to the values the original
    # schedule had reached at the interrupted epoch, and with --epochs set to
    # the number of epochs that were left. That reproduces the remaining
    # schedule closely but not exactly, and a net trained this way says so in
    # its own args (init_from is not None), so nothing downstream can mistake it
    # for an uninterrupted run. A run that wrote a .state continues exactly
    # with --resume-state instead (restored further down, once the optimizer
    # and the scheduler exist).
    if args.init_from:
        start = torch.load(args.init_from, map_location=device, weights_only=False)
        state = start.get("model", start)
        missing, unexpected = model.load_state_dict(state, strict=False)
        if missing or unexpected:
            raise SystemExit(
                f"--init-from {args.init_from} does not match this architecture: "
                f"{len(missing)} missing, {len(unexpected)} unexpected tensors. "
                f"missing={list(missing)[:4]} unexpected={list(unexpected)[:4]}")
        print(f"warm start from {args.init_from} "
              f"(epoch {start.get('epoch', '?')}, val {start.get('val_loss', float('nan')):.6f})",
              flush=True)
    # The banner names the architecture this run will EXPORT as, and that is not
    # decoration. It said "export as arch 2/3" while training an arch 5 net on
    # the first --dual run: the shapes were right, the checkpoint was right, and
    # the only thing that would have told anyone otherwise was reading the
    # tensor shapes by hand. A run that misreports what it is training is how
    # this project already lost a measurement to an export default.
    if args.dual:
        target_arch = "5"
    elif args.qa == QA:
        target_arch = "1"
    elif args.threats:
        target_arch = "4"
    else:
        target_arch = "2/3"
    print(f"net: ft_out={args.ft_out} l1_out={args.l1_out} out_buckets={args.out_buckets} "
          f"factorized={args.factorized} qat={args.qat} threats={args.threats} "
          f"coarse={args.coarse} dual={args.dual}"
          + (f" l2_out={args.l2_out}" if args.dual else "")
          + (f" (QA={args.qa}, export as arch {target_arch})" if args.qat else ""))
    # Two parameter groups so the transformer can be decayed differently from
    # the head. With ft_weight_decay equal to weight_decay this is arithmetically
    # identical to one group, which is what keeps the default run unchanged.
    ft_weight_decay = args.weight_decay if args.ft_weight_decay is None else args.ft_weight_decay
    ft_params, head_params = optimizer_groups(model)
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
            for batch in prefetch(make_val_batches(), args.prefetch):
                stm, opp, scores, results, stm_t, opp_t, stm_c, opp_c = \
                    split_batch(batch, coarse=args.coarse)
                problems = batch_problems(model, stm, opp, stm_t, opp_t, stm_c, opp_c)
                if problems:
                    print(f"  validation: BAD BATCH skipped - {'; '.join(problems)}", flush=True)
                    continue
                # Validation uses a FIXED lambda even when training schedules it.
                # A moving objective would make each epoch's number measure a
                # different thing, and "best epoch" would be picking the epoch
                # whose objective happened to be easiest.
                stm_cd = opp_cd = None
                if stm_c is not None:
                    stm_cd, opp_cd = coarse_views(to_dev(stm_c), to_dev(opp_c))
                out = model(to_dev(stm), to_dev(opp),
                            to_dev(stm_t) if stm_t is not None else None,
                            to_dev(opp_t) if opp_t is not None else None,
                            stm_cd, opp_cd)
                losses.append(loss_fn(out, to_dev(scores), to_dev(results),
                                      val_lambda).item())
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

    best_val_loss = float("inf")
    best_state = None
    best_epoch = 0
    skipped = 0
    # One entry per finished epoch with the exact floats, carried in the state:
    # the epoch lines round them, and a resume check needs them unrounded.
    history = []
    # Which epochs trained on which files; a resume that changes them appends.
    segments = [{"first_epoch": 1, "data": list(args.data),
                 "train_total": train_total, "val_total": val_total}]
    ramp = {"base_epoch": 0, "steps_per_epoch": steps_per_epoch,
            "num": 0, "per_step": 1, "den": max(1, total_steps)}
    first_epoch = 1
    # Empty files are left out on both data paths, as FeatureStore leaves them
    # out, so the list compares equal to check_resume_data's header counts.
    data_counts = [tuple(x) for x in (data_counts or []) if x[1] > 0]
    # The most rows of each file any epoch trained on (check_resume_data).
    trained = {}
    if resume is not None:
        model.load_state_dict(resume["model"])
        # Optimizer first, then the scheduler, in the order torch documents:
        # the scheduler's constructor above reset every lr to --lr, and the
        # optimizer's saved groups put back the lr the cosine had reached,
        # which its recursive formula steps from.
        optimizer.load_state_dict(resume["optimizer"])
        scheduler.load_state_dict(resume["scheduler"])
        done = resume["epoch"]
        first_epoch = done + 1
        skipped = resume["skipped"]
        history = list(resume["history"])
        segments = list(resume["segments"])
        ramp = resume["ramp"]
        if ramp["steps_per_epoch"] != steps_per_epoch:
            ramp = continue_ramp(ramp, done, args.epochs, steps_per_epoch)
        old_counts = [tuple(x) for x in resume["data_counts"]]
        trained = dict(resume.get("trained") or
                       {p: _tail_train_rows(c, args.val_fraction) for p, c in old_counts})
        # Decided on the counts the data path found, which check_resume_data
        # already printed from the headers. Another record count under the
        # same path is a file rewritten in place: another validation set.
        same_data = old_counts == data_counts and resume["reweight"] == args.reweight
        if same_data:
            best_val_loss = resume["best_val_loss"]
            best_epoch = resume["best_epoch"]
            best_state = resume["best_model"]
        else:
            print(f"resume: best validation reset (was {resume['best_val_loss']:.6f} at epoch "
                  f"{resume['best_epoch']}, measured on a validation set this run no longer "
                  f"has); " + (f"epoch {first_epoch} sets the new best, and the final checkpoint "
                               f"is the best of epochs {first_epoch}+" if first_epoch <= args.epochs
                               else "no epoch is left, so the final checkpoint is the last "
                                    "epoch's weights"))
            segments.append({"first_epoch": first_epoch, "data": list(args.data),
                             "train_total": train_total, "val_total": val_total})
        restore_rng_states(resume["rng"], rng, device)
        lam_next = start_lambda + (end_lambda - start_lambda) * ramp_ratio(ramp, first_epoch, 0)
        best_note = (f"best val {best_val_loss:.6f} at epoch {best_epoch}" if same_data
                     else "best reset")
        going_on = (f"continuing at epoch {first_epoch} with lr "
                    f"{optimizer.param_groups[0]['lr']:.6e}, lambda {lam_next:.6f}"
                    if first_epoch <= args.epochs else "nothing left to train")
        print(f"resumed from {args.resume_state}: {done} of {args.epochs} epochs done, "
              f"{going_on}, {best_note}", flush=True)
    for path, count in data_counts:
        trained[path] = max(trained.get(path, 0), _tail_train_rows(count, args.val_fraction))
    state_path = args.out + ".state"
    # Creating <out>.stop asks a running job to end at the next epoch
    # boundary, its state saved - how a run launched without
    # --stop-after-epoch is stopped to add data without losing an epoch. A
    # leftover one was removed by main before the data was built.
    stop_path = os.path.abspath(args.out + ".stop")
    optimizer_params = optimizer_param_names(model)
    start = time.time()

    for epoch in range(first_epoch, args.epochs + 1):
        epoch_losses = []
        lam_first = lam_last = None
        for step, batch in enumerate(
                prefetch(make_train_batches(), args.prefetch)):
            stm, opp, scores, results, stm_t, opp_t, stm_c, opp_c = \
                split_batch(batch, coarse=args.coarse)
            problems = batch_problems(model, stm, opp, stm_t, opp_t, stm_c, opp_c)
            if problems:
                # Keep the evidence and keep the run: the batch is written out
                # whole and skipped, and the epoch line reports the count. One
                # batch in twenty thousand does not change the network; a
                # dead run at epoch 1 costs the day.
                skipped += 1
                dump = f"{args.out}.bad_e{epoch}_s{step}.npz"
                np.savez(dump, stm=stm, opp=opp, scores=scores, results=results,
                         **({"stm_t": stm_t, "opp_t": opp_t} if stm_t is not None else {}),
                         **({"coarse": stm_c, "coarse_stm": opp_c} if stm_c is not None else {}))
                print(f"  epoch {epoch} step {step}: BAD BATCH skipped - "
                      f"{'; '.join(problems)} - saved to {dump}", flush=True)
                continue
            ratio = ramp_ratio(ramp, epoch, step)
            lam = start_lambda + (end_lambda - start_lambda) * ratio
            if lam_first is None:
                lam_first = lam
            lam_last = lam
            stm_cd = opp_cd = None
            if stm_c is not None:
                stm_cd, opp_cd = coarse_views(to_dev(stm_c), to_dev(opp_c))
                if epoch == first_epoch and step == 0:
                    check_coarse_views_once(stm_c, opp_c, stm_cd, opp_cd)
            out = model(to_dev(stm), to_dev(opp),
                        to_dev(stm_t) if stm_t is not None else None,
                        to_dev(opp_t) if opp_t is not None else None,
                        stm_cd, opp_cd)
            loss = loss_fn(out, to_dev(scores), to_dev(results), lam)

            optimizer.zero_grad()
            loss.backward()
            optimizer.step()
            model.clip_weights()
            epoch_losses.append(loss.item())

            if step % 50 == 0:
                elapsed = time.time() - start
                # Counted from this process's first epoch, since that is what
                # 'elapsed' measures; a fresh run gets the old numbers.
                steps_done = (epoch - first_epoch) * steps_per_epoch + step + 1
                steps_left = (args.epochs - epoch + 1) * steps_per_epoch - step - 1
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
            # Land the best weights on DISK as they are found, not only when
            # the whole run ends. The runs this pipeline does now are 40 hours
            # and 60 epochs, and until this line the only copy of the best
            # weights lived in RAM: a reboot at epoch 55 lost everything, with
            # no way to tell from outside how far it had got. The partial file
            # is a recovery point and a progress signal; the final save below
            # is unchanged, so nothing downstream has to know about it.
            #
            # Replaced atomically, and a failure only warns: a disk-full or a
            # copy holding the file open used to raise here and end the run
            # before this epoch's .state, which carries the best weights too.
            try:
                Path(args.out).parent.mkdir(parents=True, exist_ok=True)
                write_train_state(args.out + ".partial",
                                  {"model": best_state, "args": vars(args),
                                   "dataset": args.data, "epoch": epoch,
                                   "val_loss": best_val_loss})
            except Exception as exc:  # noqa: BLE001 - see above
                print(f"WARNING: {args.out}.partial not written ({exc}); the best weights "
                      f"are in the state and training goes on", flush=True)
        print(f"epoch {epoch}: train {np.mean(epoch_losses):.6f}  val {val_loss:.6f}  lr {current_lr:.2e}{marker}"
              + (f"  ({skipped} bad batches skipped so far)" if skipped else ""), flush=True)
        scheduler.step()
        history.append({"epoch": epoch, "val": val_loss, "lr": current_lr,
                        "train": float(np.mean(epoch_losses)) if epoch_losses else float("nan"),
                        "lambda_first": lam_first, "lambda_last": lam_last, "skipped": skipped})

        # The resume point, after the scheduler step so that it holds the lr
        # the next epoch trains with. Every epoch, not only improving ones:
        # it is the current weights that continue, the best ride along. A
        # failed write costs one epoch of redo if the run dies later, not the
        # run, so it warns and goes on.
        state_saved = False
        try:
            Path(state_path).parent.mkdir(parents=True, exist_ok=True)
            write_train_state(state_path, {
                "format": TRAIN_STATE_FORMAT, "epoch": epoch,
                "model": {k: v.detach().cpu().clone() for k, v in model.state_dict().items()},
                "optimizer": optimizer.state_dict(), "scheduler": scheduler.state_dict(),
                "optimizer_params": optimizer_params,
                "ramp": ramp, "best_val_loss": best_val_loss, "best_epoch": best_epoch,
                "best_model": best_state, "skipped": skipped, "history": history,
                "segments": segments, "args": dict(vars(args)), "data": list(args.data),
                "data_counts": data_counts, "trained": trained, "reweight": args.reweight,
                "val_split": args.val_split, "val_fraction": args.val_fraction,
                "train_total": train_total, "val_total": val_total,
                "rng": rng_states(rng, device)})
            state_saved = True
        except Exception as exc:  # noqa: BLE001 - see above
            print(f"WARNING: {state_path} not written ({exc}); the previous one is intact "
                  f"and training goes on", flush=True)
        stop_asked = os.path.exists(stop_path)
        stop_now = ((args.stop_after_epoch is not None and epoch >= args.stop_after_epoch)
                    or stop_asked) and epoch < args.epochs
        if stop_now and not state_saved:
            # Stopping now would throw away the epoch just trained; the
            # request stands and is tried again at the next boundary.
            print(f"stop postponed: the state of epoch {epoch} could not be saved", flush=True)
        elif stop_now:
            if stop_asked:
                os.remove(stop_path)
            print(f"stopped after epoch {epoch} of {args.epochs} "
                  f"({stop_path if stop_asked else '--stop-after-epoch'}); "
                  f"continue with --resume-state {state_path}", flush=True)
            raise SystemExit(EXIT_STOPPED)

    # Never save a checkpoint without weights. best_state stays None when no
    # epoch improved on the initial infinity - which happens whenever the
    # validation loss is nan (see the warning above), and used to produce a
    # checkpoint carrying "model": None that destroyed the run.
    if best_state is None:
        best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
        best_epoch = args.epochs
        print("note: no epoch improved a measurable validation loss; "
              "saving the final epoch instead of nothing.")

    # Atomic like the others: a cut mid-write must not leave a torn <out>
    # that the wrappers would export. A failure still raises - exit code 1,
    # no export - and the last .state holds the best weights to redo it.
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    write_train_state(args.out, {"model": best_state,
                                 "args": vars(args),
                                 "dataset": args.data})
    print(f"saved checkpoint: {args.out} (best epoch {best_epoch}, val {best_val_loss:.6f})")


if __name__ == "__main__":
    main()
