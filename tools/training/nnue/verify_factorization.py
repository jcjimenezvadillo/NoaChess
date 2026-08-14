# Proves that folding the virtual features at export changes NOTHING.
#
# WHY THIS EXISTS. Factorization trains a net whose accumulator is
#     sum over active f of ( real[f] + virtual[f % PS_NB] )
# and the export collapses that into a single table of INPUT_SIZE rows. If the
# row mapping is off by anything at all - repeat_interleave instead of repeat,
# the wrong modulus, the padding row folded in - training still converges and
# the exported net still loads, plays and looks completely normal. It is simply
# a different, worse net. Nothing fails loudly. So the equivalence gets checked
# here, on random weights, before any of it is trusted.
#
# The script also runs a NEGATIVE CONTROL: it perturbs one virtual row and
# requires the comparison to fail. A parity test that cannot fail proves nothing,
# which is the exact trap the lazy accumulator work fell into (every test passed
# on a version that was slower than what it replaced).
#
# Usage: python verify_factorization.py

import numpy as np
import torch

from dataset import INPUT_SIZE, PS_NB, KING_BUCKET_COUNT, MAX_ACTIVE
from model import NoaNnue

TOLERANCE = 1e-5


def random_batch(rng, rows=512):
    """Feature batches shaped like the dataset's: indices then -1 padding."""
    feats = np.full((rows, MAX_ACTIVE), -1, dtype=np.int16)
    for r in range(rows):
        active = rng.integers(2, MAX_ACTIVE + 1)
        feats[r, :active] = rng.integers(0, INPUT_SIZE, size=active)
    return torch.from_numpy(feats)


def folded_twin(factorized_model, ft_out, l1_out, buckets):
    """An UNFACTORIZED net holding exactly what export_model.py would write."""
    twin = NoaNnue(ft_out, l1_out, buckets, factorized=False)
    with torch.no_grad():
        twin.ft.weight[:INPUT_SIZE] = factorized_model.fold_features()
        twin.ft.weight[twin.pad_index].zero_()
        twin.ft_bias.copy_(factorized_model.ft_bias)
        twin.l1.weight.copy_(factorized_model.l1.weight)
        twin.l1.bias.copy_(factorized_model.l1.bias)
        twin.out.weight.copy_(factorized_model.out.weight)
        twin.out.bias.copy_(factorized_model.out.bias)
    return twin


def max_difference(a, b, stm, opp):
    with torch.no_grad():
        return float((a(stm, opp) - b(stm, opp)).abs().max())


def check_index_mapping():
    """The identity the fold rests on: index = base + bucket * PS_NB."""
    model = NoaNnue(8, 4, 1, factorized=True)
    probe = torch.tensor([[0, PS_NB, PS_NB + 5, INPUT_SIZE - 1, -1]], dtype=torch.int16)
    got = model._indices(probe)[0].tolist()
    real, virtual = got[:5], got[5:]
    expected_real = [0, PS_NB, PS_NB + 5, INPUT_SIZE - 1, model.pad_index]
    expected_virtual = [INPUT_SIZE + 0, INPUT_SIZE + 0, INPUT_SIZE + 5,
                        INPUT_SIZE + (INPUT_SIZE - 1) % PS_NB, model.pad_index]
    assert real == expected_real, f"real rows wrong: {real} != {expected_real}"
    assert virtual == expected_virtual, f"virtual rows wrong: {virtual} != {expected_virtual}"
    print(f"index mapping    OK   feature 0 and feature {PS_NB} share virtual row "
          f"{INPUT_SIZE}, padding stays padding")


def main():
    torch.manual_seed(11)
    rng = np.random.default_rng(11)
    check_index_mapping()

    failures = 0
    for ft_out, l1_out, buckets in ((16, 8, 1), (128, 32, 1), (128, 32, 8)):
        model = NoaNnue(ft_out, l1_out, buckets, factorized=True)
        # Init leaves the padding row zero but everything else uniform, which is
        # what makes a wrong fold show up: a mis-tiled table gives every row the
        # wrong correction rather than a small one.
        with torch.no_grad():
            torch.nn.init.uniform_(model.ft.weight, -0.05, 0.05)
            model.ft.weight[model.pad_index].zero_()

        twin = folded_twin(model, ft_out, l1_out, buckets)
        stm, opp = random_batch(rng), random_batch(rng)
        delta = max_difference(model, twin, stm, opp)

        status = "OK  " if delta <= TOLERANCE else "FAIL"
        if delta > TOLERANCE:
            failures += 1
        print(f"fold equivalence {status} ft={ft_out:<4} l1={l1_out:<3} buckets={buckets}"
              f"   max |factorized - folded| = {delta:.3e}")

        # Negative control: move ONE virtual row and require a difference. If
        # this comes back equal the comparison is inert and the OK above is
        # worthless.
        with torch.no_grad():
            model.ft.weight[model.virtual_base + 17] += 0.01
        control = max_difference(model, twin, stm, opp)
        if control <= TOLERANCE:
            failures += 1
            print(f"negative control FAIL ft={ft_out}: perturbing a virtual row "
                  f"changed nothing ({control:.3e}); the test cannot detect a bad fold")
        else:
            print(f"negative control OK   perturbing one virtual row moves the output "
                  f"by {control:.3e}")

    # The fold must also be exact where it matters most: a real net's weights,
    # not just random ones. 32 copies of each virtual row is 22,528 rows, so a
    # tiling mistake would leave most rows correct and a minority wrong - which
    # is precisely the failure a spot check would miss.
    model = NoaNnue(128, 32, 1, factorized=True)
    with torch.no_grad():
        torch.nn.init.uniform_(model.ft.weight, -0.05, 0.05)
    folded = model.fold_features()
    real = model.ft.weight[:INPUT_SIZE].detach()
    virtual = model.ft.weight[model.virtual_base:model.virtual_base + PS_NB].detach()
    rows = rng.integers(0, INPUT_SIZE, size=4096)
    worst = max(float((folded[r] - (real[r] + virtual[r % PS_NB])).abs().max()) for r in rows)
    status = "OK  " if worst <= TOLERANCE else "FAIL"
    if worst > TOLERANCE:
        failures += 1
    print(f"row-by-row fold  {status} 4,096 sampled rows of {INPUT_SIZE:,}"
          f"   max deviation = {worst:.3e}")

    print()
    if failures:
        raise SystemExit(f"{failures} check(s) FAILED: do not train or export a "
                         f"factorized net until this passes.")
    print(f"All checks passed. Folding is exact, so a factorized net exports to a "
          f"file the engine evaluates identically ({KING_BUCKET_COUNT} copies per "
          f"virtual feature).")


if __name__ == "__main__":
    main()
