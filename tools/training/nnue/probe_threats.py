# The 2x2 that decides whether threat features are worth weeks of engine work.
#
# THE QUESTION IT ANSWERS, AND THE ONE IT DOES NOT. Adding threats to a 128-wide
# transformer and getting nothing back would not tell us whether the features are
# useless or merely do not fit, because width has already been measured as a
# loser on the CURRENT input (fqw256 at -31.9) and a richer input is exactly the
# thing that could change that answer. So the arms are crossed:
#
#              width 128        width 256
#   HalfKA     baseline         width alone
#   +threats   threats alone    both
#
# Read it as a table, not as four numbers. If threats gain at both widths, they
# are worth building. If they gain only at 256, the input and the capacity are
# coupled and the engine work has to carry a width change with it. If they gain
# at neither, the idea is dead and no C# was written to find out.
#
# WHAT IS BEING COMPARED. Validation loss on held-out positions, which is the
# same quantity the real trainer reports, plus the correlation with the teacher's
# own evaluation. Neither is Elo. A gain here is permission to build the thing
# and measure Elo properly; it is not a strength claim.
import argparse
import time

import numpy as np
import torch
import torch.nn as nn

import dataset
import threats


class Net(nn.Module):
    """The shipping shape, with an optional second input bag bolted on.

    Deliberately the same architecture as the real net apart from the extra
    features, because the point is to measure the features and not a redesign.
    """

    def __init__(self, ft_out, with_threats):
        super().__init__()
        self.halfka = nn.EmbeddingBag(dataset.INPUT_SIZE + 1, ft_out, mode="sum")
        self.with_threats = with_threats
        if with_threats:
            self.threats = nn.EmbeddingBag(threats.THREAT_INPUT_SIZE + 1, ft_out, mode="sum")
        width = 2 * ft_out
        self.l1 = nn.Linear(width, 32)
        self.out = nn.Linear(32, 1)

    def side(self, halfka_idx, threat_idx):
        acc = self.halfka(halfka_idx)
        if self.with_threats:
            acc = acc + self.threats(threat_idx)
        return torch.clamp(acc, 0.0, 1.0)

    def forward(self, stm_ka, opp_ka, stm_th, opp_th):
        both = torch.cat([self.side(stm_ka, stm_th), self.side(opp_ka, opp_th)], dim=1)
        return self.out(torch.clamp(self.l1(both), 0.0, 1.0))


def _kings(occupancy, nibbles):
    """White and black king squares, read straight off the record."""
    white = black = 0
    bb, i = int(occupancy), 0
    while bb:
        sq = (bb & -bb).bit_length() - 1
        code = (int(nibbles[i >> 1]) >> (4 * (i & 1))) & 0xF
        if code % 6 == 5:
            if code // 6 == 0:
                white = sq
            else:
                black = sq
        bb &= bb - 1
        i += 1
    return white, black


def load(paths, limit):
    """Records, HalfKA features and threat features for the same positions."""
    chunks, taken = [], 0
    for path in paths:
        recs = dataset.load_records(path)          # memory-mapped, not read
        want = min(limit - taken, len(recs))
        chunks.append(np.array(recs[:want]))
        taken += want
        if taken >= limit:
            break
    arr = np.concatenate(chunks) if len(chunks) > 1 else chunks[0]
    print(f"  {len(arr):,} positions from {len(chunks)} shard(s)")

    ka_stm, ka_opp, scores, results = dataset.decode_block(arr)

    # The threat side, one position at a time on verified primitives.
    started = time.time()
    th_stm = np.full((len(arr), threats.MAX_ACTIVE_THREATS), -1, dtype=np.int32)
    th_opp = np.full((len(arr), threats.MAX_ACTIVE_THREATS), -1, dtype=np.int32)
    for i, rec in enumerate(arr):
        occ = int(rec["occupancy"])
        nib = rec["pieces"]
        stm = int(rec["stm"])
        wk, bk = _kings(occ, nib)
        us, them = (wk, bk) if stm == 0 else (bk, wk)
        a = threats.threats_of(occ, nib, us, stm)
        b = threats.threats_of(occ, nib, them, 1 - stm)
        th_stm[i, : len(a)] = a
        th_opp[i, : len(b)] = b
        if i and i % 200000 == 0:
            print(f"    threats {i:,}/{len(arr):,}  ({time.time() - started:.0f}s)", flush=True)
    print(f"  threat encoding took {time.time() - started:.0f}s")

    pad_ka, pad_th = dataset.INPUT_SIZE, threats.THREAT_INPUT_SIZE
    return (torch.from_numpy(np.where(ka_stm < 0, pad_ka, ka_stm).astype(np.int64)),
            torch.from_numpy(np.where(ka_opp < 0, pad_ka, ka_opp).astype(np.int64)),
            torch.from_numpy(np.where(th_stm < 0, pad_th, th_stm).astype(np.int64)),
            torch.from_numpy(np.where(th_opp < 0, pad_th, th_opp).astype(np.int64)),
            torch.from_numpy(scores), torch.from_numpy(results))


def run(arm, data, ft_out, with_threats, epochs, batch, lam, device):
    ka_s, ka_o, th_s, th_o, scores, results = data
    n = len(scores)
    cut = int(n * 0.95)

    torch.manual_seed(1)                      # same start for every arm
    net = Net(ft_out, with_threats).to(device)
    opt = torch.optim.Adam(net.parameters(), lr=1e-3, weight_decay=1e-5)

    def target(idx):
        wdl = torch.sigmoid(scores[idx] / 410.0)
        return lam * wdl + (1 - lam) * (results[idx] * 0.5 + 0.5)

    best = float("inf")
    for epoch in range(1, epochs + 1):
        net.train()
        order = torch.randperm(cut)
        for begin in range(0, cut, batch):
            idx = order[begin: begin + batch]
            pred = torch.sigmoid(net(ka_s[idx].to(device), ka_o[idx].to(device),
                                     th_s[idx].to(device), th_o[idx].to(device)).squeeze(1))
            loss = ((pred - target(idx).to(device)) ** 2).mean()
            opt.zero_grad(); loss.backward(); opt.step()

        net.eval()
        with torch.no_grad():
            vs, vp = [], []
            for begin in range(cut, n, batch):
                idx = torch.arange(begin, min(begin + batch, n))
                p = torch.sigmoid(net(ka_s[idx].to(device), ka_o[idx].to(device),
                                      th_s[idx].to(device), th_o[idx].to(device)).squeeze(1))
                vp.append(p.cpu()); vs.append(target(idx))
            vp, vs = torch.cat(vp), torch.cat(vs)
            val = ((vp - vs) ** 2).mean().item()
            corr = float(np.corrcoef(vp.numpy(), vs.numpy())[0, 1])
        best = min(best, val)
        print(f"  [{arm}] epoch {epoch:2d}  val {val:.6f}  corr {corr:.4f}", flush=True)

    return best, corr


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", nargs="+", required=True)
    ap.add_argument("--positions", type=int, default=2_000_000)
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--batch", type=int, default=16384)
    ap.add_argument("--lam", type=float, default=0.85)
    args = ap.parse_args()

    device = "cuda" if torch.cuda.is_available() else "cpu"
    print(threats.describe())
    print(f"device: {device}")
    print("loading:")
    data = load(args.data, args.positions)

    results = {}
    for ft_out in (128, 256):
        for with_threats in (False, True):
            arm = f"ft{ft_out}{'+threats' if with_threats else ''}"
            print(f"=== {arm}")
            results[arm] = run(arm, data, ft_out, with_threats,
                               args.epochs, args.batch, args.lam, device)

    print()
    print("                 val loss     corr")
    for arm, (val, corr) in results.items():
        print(f"  {arm:16s} {val:.6f}   {corr:.4f}")
    print()
    base128 = results["ft128"][0]
    print(f"  threats at 128: {100 * (base128 - results['ft128+threats'][0]) / base128:+.2f}% val loss")
    base256 = results["ft256"][0]
    print(f"  threats at 256: {100 * (base256 - results['ft256+threats'][0]) / base256:+.2f}% val loss")
    print()
    print("  A gain at BOTH widths means build it. Only at 256 means the input and")
    print("  the capacity are coupled. At neither means the idea is dead, cheaply.")


main()
