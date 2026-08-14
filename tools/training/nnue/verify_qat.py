# Proves that quantization-aware training makes the trained net and the ENGINE
# agree, and measures what they disagree by without it.
#
# WHAT IS BEING CLAIMED. With --qat the model's float forward pass is not an
# approximation of the engine's integer arithmetic - it is algebraically THE
# SAME NUMBER, because every tensor is rounded to the grid export will put it on
# and the activation is floored exactly where the engine's integer division
# truncates. So the two must agree to within the engine's final truncation to a
# whole centipawn, and anything larger is a bug in the correspondence.
#
# The run without --qat is kept as the control: it is the situation the shipping
# net is in, and it shows what the claim is worth.
#
# Usage: python verify_qat.py

import numpy as np
import torch

from dataset import INPUT_SIZE, MAX_ACTIVE
from model import NoaNnue, OUTPUT_SCALE, QA, QB

TOLERANCE_CP = 1.0  # the engine truncates the final centipawn value


def random_batch(rng, rows=1024):
    feats = np.full((rows, MAX_ACTIVE), -1, dtype=np.int16)
    for r in range(rows):
        active = rng.integers(2, MAX_ACTIVE + 1)
        feats[r, :active] = rng.integers(0, INPUT_SIZE, size=active)
    return feats


def engine_arithmetic(model, stm_feats, opp_feats, qa):
    """The integer path from NnueInference, in numpy, vectorised over a batch."""
    ft_w = np.round(model.fold_features().numpy() * qa)
    ft_b = np.round(model.ft_bias.detach().numpy() * qa)
    l1_w = np.round(model.l1.weight.detach().numpy() * QB)
    l1_b = np.round(model.l1.bias.detach().numpy() * qa * QB)
    out_w = np.round(model.out.weight.detach().numpy().flatten() * QB)
    out_b = np.round(model.out.bias.item() * qa * QB)

    def accumulate(feats):
        rows = ft_w[np.where(feats < 0, 0, feats)]
        rows[feats < 0] = 0
        return np.clip(ft_b + rows.sum(axis=1), 0, qa)

    x = np.concatenate([accumulate(stm_feats), accumulate(opp_feats)], axis=1)
    hidden = np.clip((l1_b + x @ l1_w.T) // QB, 0, qa)
    return (out_b + hidden @ out_w) * OUTPUT_SCALE / (qa * QB)


def compare(qat, qa, rng, label):
    torch.manual_seed(7)
    model = NoaNnue(128, 32, 1, factorized=False, qat=qat, qa=qa)
    # Realistic magnitudes: the shipping net's l1/out weights sit well inside
    # their clip bounds, and the transformer's are small. Random weights of the
    # wrong size would make the comparison either trivially exact or absurd.
    with torch.no_grad():
        torch.nn.init.uniform_(model.ft.weight, -0.02, 0.02)
        model.ft.weight[model.pad_index].zero_()
        torch.nn.init.uniform_(model.l1.weight, -0.5, 0.5)
        torch.nn.init.uniform_(model.out.weight, -0.5, 0.5)
    model.eval()

    stm, opp = random_batch(rng), random_batch(rng)
    with torch.no_grad():
        trained_cp = model(torch.from_numpy(stm), torch.from_numpy(opp)).numpy() * OUTPUT_SCALE
    engine_cp = engine_arithmetic(model, stm, opp, qa)

    error = np.abs(trained_cp - engine_cp)
    print(f"{label:<34} mean {error.mean():7.2f} cp   max {error.max():7.2f} cp"
          f"   mean|eval| {np.abs(trained_cp).mean():6.1f} cp")
    return error.max()


def main():
    rng = np.random.default_rng(3)
    print("Disagreement between the TRAINED net and the ENGINE's integer arithmetic:\n")
    without = compare(False, QA, rng, "float training (what ships today)")
    with_qat = compare(True, QA, rng, "quantization-aware, QA=255 (arch 1)")
    compare(True, 127, rng, "quantization-aware, QA=127 (arch 2/3)")

    print()
    if with_qat > TOLERANCE_CP:
        raise SystemExit(
            f"FAILED: with --qat the two must agree to within {TOLERANCE_CP} cp "
            f"(the engine's final truncation), got {with_qat:.2f} cp. The fake "
            f"quantization does not match export_model.py's scales.")
    if without <= TOLERANCE_CP:
        raise SystemExit(
            "FAILED: the control agrees too. The comparison cannot detect a "
            "mismatch, so the result above proves nothing.")
    print(f"PASSED. With --qat the trained net and the engine agree to "
          f"{with_qat:.2f} cp, inside the {TOLERANCE_CP:.0f} cp truncation.")
    print(f"Without it they differ by up to {without:.0f} cp, which is the gap "
          f"quantization-aware training closes.")


if __name__ == "__main__":
    main()
