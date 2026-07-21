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
from model import NoaNnue, OUTPUT_SCALE, QA, QB
from train_nnue import wdl_target


def quantized_eval(model, stm_feats, opp_feats):
    """Integer forward pass mirroring NnueInference.EvaluateScalar (float
    simulation of the exact rounding steps)."""
    ft_w = np.round(model.ft.weight.detach().numpy() * QA)
    ft_b = np.round(model.ft_bias.detach().numpy() * QA)
    l1_w = np.round(model.l1.weight.detach().numpy() * QB)
    l1_b = np.round(model.l1.bias.detach().numpy() * QA * QB)
    out_w = np.round(model.out.weight.detach().numpy().flatten() * QB)
    out_b = np.round(model.out.bias.item() * QA * QB)

    results = []
    for row in range(stm_feats.shape[0]):
        acc = []
        for feats in (stm_feats[row], opp_feats[row]):
            active = feats[feats >= 0]
            acc.append(np.clip(ft_b + ft_w[active].sum(axis=0), 0, QA))
        x = np.concatenate(acc)
        hidden = np.clip((l1_b + l1_w @ x) // QB, 0, QA)
        out = out_b + out_w @ hidden
        results.append(out * OUTPUT_SCALE / (QA * QB))
    return np.array(results)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--data", required=True)
    parser.add_argument("--batch", type=int, default=4096)
    parser.add_argument("--samples", type=int, default=20000)
    args = parser.parse_args()

    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = NoaNnue()
    model.load_state_dict(checkpoint["model"])
    model.eval()
    lam = checkpoint["args"].get("lam", 0.7)

    records = dataset.load_records(args.data)
    rng = np.random.default_rng(0)

    losses, quant_errors = [], []
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

    print(f"records evaluated : {min(args.samples, len(records)):,}")
    print(f"validation loss   : {np.mean(losses):.6f}")
    print(f"quantization error: {np.mean(quant_errors):.2f} cp (mean abs, float vs int)")


if __name__ == "__main__":
    main()
