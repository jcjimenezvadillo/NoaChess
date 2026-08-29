# Validation utility: loss of a checkpoint over a dataset plus a simulation
# of the QUANTIZED forward pass (the integer math the C# engine will run),
# reporting how much precision the int16 conversion costs.
#
# Usage:
#   python validate_nnue.py --checkpoint checkpoints/run1.pt --data ../../data/val.noadata

import argparse

import numpy as np
import torch

import dataset
from model import NoaNnue, OUTPUT_SCALE, QA, QB, FT_OUT, L1_OUT
from train_nnue import wdl_target


def quantized_eval(model, stm_feats, opp_feats):
    """Integer forward pass mirroring NnueInference.EvaluateScalar (float
    simulation of the exact rounding steps)."""
    # The FOLDED table, which is what the engine will hold. Reading ft.weight
    # directly would skip the virtual rows on a factorized net and simulate a
    # net that is never exported.
    ft_w = np.round(model.fold_features().numpy() * QA)
    ft_b = np.round(model.ft_bias.detach().numpy() * QA)
    l1_w = np.round(model.l1.weight.detach().numpy() * QB)
    l1_b = np.round(model.l1.bias.detach().numpy() * QA * QB)
    out_w = np.round(model.out.weight.detach().numpy().flatten() * QB)
    out_b = np.round(model.out.bias.item() * QA * QB)

    psqt_rows = None
    if getattr(model, "psqt_buckets", 0) > 0:
        # The engine stores these rows as int32 at OUTPUT_SCALE and adds
        # (stm - opp) / 2 in centipawns OUTSIDE the quantized path, so the
        # simulation must do the same or the whole material term reads as
        # quantization error.
        psqt_rows = np.round(model.fold_psqt().numpy() * OUTPUT_SCALE)

    results = []
    for row in range(stm_feats.shape[0]):
        acc = []
        for feats in (stm_feats[row], opp_feats[row]):
            active = feats[feats >= 0]
            acc.append(np.clip(ft_b + ft_w[active].sum(axis=0), 0, QA))
        x = np.concatenate(acc)
        hidden = np.clip((l1_b + l1_w @ x) // QB, 0, QA)
        out = out_b + out_w @ hidden
        cp = out * OUTPUT_SCALE / (QA * QB)
        if psqt_rows is not None:
            stm_act = stm_feats[row][stm_feats[row] >= 0]
            opp_act = opp_feats[row][opp_feats[row] >= 0]
            cp += (psqt_rows[stm_act, 0].sum() - psqt_rows[opp_act, 0].sum()) // 2
        results.append(cp)
    return np.array(results)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--data", required=True)
    parser.add_argument("--batch", type=int, default=4096)
    parser.add_argument("--samples", type=int, default=20000)
    # Exclude the datagen's spurious score==0 labels from the diagnostics.
    parser.add_argument("--nonzero-only", action="store_true")
    args = parser.parse_args()

    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    ckpt_args = checkpoint.get("args", {})
    buckets = max(1, ckpt_args.get("out_buckets", 1))
    if buckets > 1:
        # quantized_eval below reads a single output row and bias, so a bucketed
        # checkpoint would have to pick the bucket per position. Say so here
        # rather than fail three screens later inside numpy.
        raise SystemExit(f"checkpoint has {buckets} output buckets; this script "
                         f"only simulates the single-bucket head.")
    # qat is carried through so the reported loss describes the net the ENGINE
    # runs. Without it a quantization-aware run would be scored on a float
    # forward pass it was never trained to produce.
    model = NoaNnue(ckpt_args.get("ft_out", FT_OUT), ckpt_args.get("l1_out", L1_OUT),
                    buckets, bool(ckpt_args.get("factorized", False)),
                    bool(ckpt_args.get("qat", False)), ckpt_args.get("qa", QA),
                    psqt_buckets=int(ckpt_args.get("psqt_buckets", 0) or 0))
    model.load_state_dict(checkpoint["model"])
    model.eval()
    lam = checkpoint["args"].get("lam", 0.7)

    records = dataset.load_records(args.data)
    rng = np.random.default_rng(0)

    losses, quant_errors = [], []
    all_net_cp, all_label_cp = [], []
    with torch.no_grad():
        for stm, opp, scores, results in dataset.batches(
                records, args.batch, rng, sample_limit=args.samples):
            raw = model(torch.from_numpy(stm), torch.from_numpy(opp))
            pred = torch.sigmoid(raw)
            target = wdl_target(torch.from_numpy(scores), torch.from_numpy(results), lam)
            losses.append(torch.mean((pred - target) ** 2).item())

            float_cp = raw.numpy() * OUTPUT_SCALE
            quant_cp = quantized_eval(model, stm, opp)
            quant_errors.append(np.abs(float_cp - quant_cp).mean())

            if args.nonzero_only:
                m = scores != 0
                float_cp, scores = float_cp[m], scores[m]
            all_net_cp.append(float_cp)
            all_label_cp.append(scores)

    net_cp = np.concatenate(all_net_cp).astype(np.float64)
    label_cp = np.concatenate(all_label_cp).astype(np.float64)
    corr = np.corrcoef(net_cp, label_cp)[0, 1]
    slope = np.polyfit(label_cp, net_cp, 1)[0]  # net = slope*label + b
    rms = float(np.sqrt(np.mean((net_cp - label_cp) ** 2)))
    sign_agree = float(np.mean(np.sign(net_cp) == np.sign(label_cp)))

    print(f"records evaluated : {min(args.samples, len(records)):,}")
    print(f"validation loss   : {np.mean(losses):.6f}")
    print(f"quantization error: {np.mean(quant_errors):.2f} cp (mean abs, float vs int)")
    print("--- net eval vs teacher label (both cp, side to move) ---")
    print(f"pearson corr      : {corr:.4f}   (negative => sign/perspective bug)")
    print(f"regression slope  : {slope:.3f}   (1.0 = same scale; <1 = compressed)")
    print(f"RMS error         : {rms:.1f} cp")
    print(f"sign agreement    : {sign_agree*100:.1f}%")
    print(f"mean |net|        : {np.mean(np.abs(net_cp)):.1f} cp")
    print(f"mean |label|      : {np.mean(np.abs(label_cp)):.1f} cp")


if __name__ == "__main__":
    main()
