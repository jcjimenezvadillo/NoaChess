# Re-orders the feature-transformer neurons of a checkpoint so neurons that are
# active together share one int32 pair of the engine's sparse L1 kernel, which
# walks only the nonzero pairs: fewer pairs per evaluation, same function.
#
#   python permute_ft.py --checkpoint in.pt --out out.pt --data sample.noadata
#
# Every tensor indexed by transformer neuron moves with the same permutation:
# the ft.weight columns (factor rows included), ft_bias, and both halves of the
# l1.weight columns. Each weight keeps its value and only its column moves, so
# the exported net evaluates every position to exactly the same integer and
# node counts do not change; the float outputs are compared on a sample and
# nothing is written on any mismatch. Nets with the coarse lane are refused
# (the activation statistics would need the coarse features too).
#
# The pairing is greedy: from the integer activations of a sample (as
# exported: round(w * QA) rows plus round(b * QA), active when > 0), the pair
# cost of neurons i and j is P(i active or j active), and the cheapest
# disjoint pairs are taken first. On wide512nc (v6.5.0) it takes the nonzero
# pairs per evaluation from 94.6 to 81.9 of 512.

import argparse

import numpy as np
import torch

import dataset
from model import NoaNnue


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--checkpoint", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--data", required=True, help="a .noadata file to sample positions from")
    ap.add_argument("--positions", type=int, default=60_000)
    ap.add_argument("--qa", type=int, default=255)
    args = ap.parse_args()

    c = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    sd = c.get("model", c)
    ft_out = sd["ft_bias"].shape[0]
    if any(k.startswith("coarse") for k in sd):
        raise SystemExit("nets with the coarse lane are not supported")
    l1_out = sd["l1.weight"].shape[0]
    model = NoaNnue(ft_out, l1_out, 1, True, qa=args.qa, coarse=False)
    model.load_state_dict(sd)
    model.eval()

    recs = dataset.load_records(args.data)
    rng = np.random.default_rng(1)
    idx = np.sort(rng.choice(len(recs), size=min(args.positions, len(recs)), replace=False))
    stm_f, opp_f, _, _ = dataset.decode_block(recs[idx])

    w = torch.round(model.fold_features() * args.qa)
    b = torch.round(model.ft_bias.detach() * args.qa)
    acts = []
    with torch.no_grad():
        for f in (stm_f, opp_f):
            t = torch.from_numpy(f.astype(np.int64))
            valid = t >= 0
            tt = torch.where(valid, t, torch.zeros_like(t))
            for s in range(0, len(tt), 2000):
                acc = (w[tt[s:s + 2000]] * valid[s:s + 2000].float().unsqueeze(-1)).sum(1) + b
                acts.append(acc > 0)
    active = torch.cat(acts).numpy()
    p = active.mean(0)

    def pairs_per_eval(a):
        return (a[:, 0::2] | a[:, 1::2]).sum(1).sum() / (len(a) / 2)

    af = active.astype(np.float32)
    cost = p[:, None] + p[None, :] - (af.T @ af) / len(af)
    np.fill_diagonal(cost, np.inf)
    used = np.zeros(ft_out, bool)
    perm = []
    for k in np.argsort(cost, axis=None):
        i, j = divmod(int(k), ft_out)
        if i == j or used[i] or used[j]:
            continue
        used[i] = used[j] = True
        perm += [i, j]
        if len(perm) == ft_out:
            break
    perm = np.array(perm)
    assert sorted(perm.tolist()) == list(range(ft_out))
    before, after = pairs_per_eval(active), pairs_per_eval(active[:, perm])
    print(f"nonzero pairs per evaluation: {before:.1f} -> {after:.1f} ({100 * (after / before - 1):+.1f}%)")

    for k in sd:
        if k not in ("ft.weight", "ft_bias", "l1.weight") and ft_out in tuple(sd[k].shape):
            raise SystemExit(f"tensor {k} {tuple(sd[k].shape)} is indexed by neuron and not handled")
    pt = torch.from_numpy(perm).long()
    new = dict(sd)
    new["ft.weight"] = sd["ft.weight"][:, pt].clone()
    new["ft_bias"] = sd["ft_bias"][pt].clone()
    l1w = sd["l1.weight"]
    new["l1.weight"] = torch.cat([l1w[:, :ft_out][:, pt], l1w[:, ft_out:][:, pt]], dim=1).clone()

    permuted = NoaNnue(ft_out, l1_out, 1, True, qa=args.qa, coarse=False)
    permuted.load_state_dict(new)
    permuted.eval()
    with torch.no_grad():
        s = torch.from_numpy(stm_f[:5000].astype(np.int64))
        o = torch.from_numpy(opp_f[:5000].astype(np.int64))
        diff = (model(s, o) - permuted(s, o)).abs().max().item()
    print(f"max |output difference| on 5000 positions: {diff:.3e}")
    if diff > 1e-4:
        raise SystemExit("outputs differ: nothing written")

    out = dict(c) if "model" in c else {}
    out["model"] = new
    torch.save(out, args.out)
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
