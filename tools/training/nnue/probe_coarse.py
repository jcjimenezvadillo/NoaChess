# Coarse-threat probe: does an aggregate threat encoding carry signal WITHOUT
# the per-node geometry that killed the fine set at the clock?
#
# The fine set (60,720 dims, exact squares) measured +60.4 at fixed nodes and
# -14.9 at the clock because its incremental delta costs ~50% of search time
# and cannot be deferred. A COARSE encoding - one bucket per (attacker piece,
# attacked piece), side-relative, multiplicity kept - could be computed at
# evaluation time only, from bitboards, with no lists and no diff. This probe
# answers whether that encoding carries enough signal to justify pricing the
# engine path at all.
#
# KILL CRITERION (written before running, notas/AMENAZAS_COMPACTAS_DISENO.md):
# the fine set moved validation loss 3.96% at ft128. If coarse does not reach
# about a third of that (~1.3%), bury the idea here and spend nothing more.
#
# The relations come from threats.active_threats' own enumeration - the same
# verified geometry, re-labelled - so this probe cannot disagree with the fine
# encoder about WHAT is attacked, only about how finely it is filed.

import argparse
import sys
import time

import numpy as np
import torch
import torch.nn as nn

import dataset
import threats
from threats import (WHITE, BLACK, PAWN, KNIGHT, BISHOP, ROOK, QUEEN,
                     BISHOP_DELTAS, ROOK_DELTAS, KNIGHT_ATT,
                     _bits, _shift, _slider_attacks, board_from_record, piece)
from probe_threats import cap_gpu_memory, refuse_to_share_the_gpu

# (relative attacker: own/enemy x 6 types) x (relative victim: own/enemy x 6)
N_COARSE = 144
MAX_COARSE_ACTIVE = 128
HALFKA_VIRTUAL = 704
MAX_KA_ACTIVE = 32 * 2


def coarse_relations(occupancy, nibbles):
    """The fine enumeration's relation list, as (attacker code, victim code).

    Copied step for step from threats.active_threats up to the point where it
    would call index(); the coarse probe stops there and keeps the pieces.
    """
    board, bbs = board_from_record(occupancy, nibbles)
    occupied = 0
    for b in bbs:
        occupied |= b

    def of(*types):
        bb = 0
        for c in (WHITE, BLACK):
            for t in types:
                bb |= bbs[piece(c, t)]
        return bb

    pawns = of(PAWN)
    pawn_targets = of(PAWN, KNIGHT, ROOK)
    minor_slider_targets = of(PAWN, KNIGHT, BISHOP, ROOK)
    queen_targets = of(PAWN, KNIGHT, BISHOP, ROOK, QUEEN)

    relations = []
    for c in (WHITE, BLACK):
        attacker = piece(c, PAWN)
        c_pawns = bbs[attacker]
        back = -8 if c == WHITE else 8
        pushers = _shift(pawns, back) & c_pawns
        caps = (9, 7) if c == WHITE else (-7, -9)
        push = 8 if c == WHITE else -8
        for d in caps:
            for to in _bits(_shift(c_pawns, d) & pawn_targets):
                relations.append((attacker, board[to]))
        for to in _bits(_shift(pushers, push)):
            relations.append((attacker, board[to]))
        for pt in (KNIGHT, BISHOP, ROOK, QUEEN):
            attacker = piece(c, pt)
            targets = queen_targets if pt in (KNIGHT, QUEEN) else minor_slider_targets
            for frm in _bits(bbs[attacker]):
                if pt == KNIGHT:
                    att = int(KNIGHT_ATT[frm])
                elif pt == BISHOP:
                    att = _slider_attacks(frm, occupied, BISHOP_DELTAS)
                elif pt == ROOK:
                    att = _slider_attacks(frm, occupied, ROOK_DELTAS)
                else:
                    att = _slider_attacks(frm, occupied, BISHOP_DELTAS + ROOK_DELTAS)
                for to in _bits(att & targets):
                    relations.append((attacker, board[to]))
    return relations


def coarse_buckets(relations, perspective):
    """Side-relative bucket ids, multiplicity kept (EmbeddingBag sums rows,
    so a repeated id IS the count)."""
    out = []
    for att, vic in relations:
        ra = (att // 6) ^ perspective          # 0 = own piece attacks
        rv = (vic // 6) ^ perspective
        out.append((ra * 6 + att % 6) * 12 + (rv * 6 + vic % 6))
    return out


class Net(nn.Module):
    """probe_threats.Net with the fine bag swapped for the 144-bucket one."""

    def __init__(self, ft_out, with_coarse):
        super().__init__()
        self.halfka = nn.EmbeddingBag(dataset.INPUT_SIZE + HALFKA_VIRTUAL + 1, ft_out, mode="sum")
        self.with_coarse = with_coarse
        if with_coarse:
            self.coarse = nn.EmbeddingBag(N_COARSE + 1, ft_out, mode="sum")
            nn.init.zeros_(self.coarse.weight)
        width = 2 * ft_out
        self.l1 = nn.Linear(width, 32)
        self.out = nn.Linear(32, 1)

    def side(self, halfka_idx, coarse_idx):
        acc = self.halfka(halfka_idx)
        if self.with_coarse:
            acc = acc + self.coarse(coarse_idx)
        return torch.clamp(acc, 0.0, 1.0)

    def forward(self, stm_ka, opp_ka, stm_co, opp_co):
        both = torch.cat([self.side(stm_ka, stm_co), self.side(opp_ka, opp_co)], dim=1)
        return self.out(torch.clamp(self.l1(both), 0.0, 1.0))


def load(paths, limit):
    chunks, taken = [], 0
    for path in paths:
        recs = dataset.load_records(path)
        want = min(limit - taken, len(recs))
        chunks.append(np.array(recs[:want]))
        taken += want
        if taken >= limit:
            break
    arr = np.concatenate(chunks) if len(chunks) > 1 else chunks[0]
    print(f"  {len(arr):,} positions from {len(chunks)} shard(s)", flush=True)

    ka_stm, ka_opp, scores, results = dataset.decode_block(arr)

    def factor_ka(block):
        out = np.full((len(block), MAX_KA_ACTIVE), -1, dtype=np.int32)
        real = block.shape[1]
        out[:, :real] = block
        virtual = np.where(block < 0, -1, dataset.INPUT_SIZE + (block % HALFKA_VIRTUAL))
        out[:, real:real + real] = virtual
        return out

    ka_stm, ka_opp = factor_ka(ka_stm), factor_ka(ka_opp)

    started = time.time()
    co_stm = np.full((len(arr), MAX_COARSE_ACTIVE), -1, dtype=np.int32)
    co_opp = np.full((len(arr), MAX_COARSE_ACTIVE), -1, dtype=np.int32)
    overflow = 0
    for i, rec in enumerate(arr):
        rel = coarse_relations(int(rec["occupancy"]), rec["pieces"])
        stm = int(rec["stm"])
        a = coarse_buckets(rel, stm)
        b = coarse_buckets(rel, 1 - stm)
        if len(a) > MAX_COARSE_ACTIVE:
            overflow += 1
        co_stm[i, : min(len(a), MAX_COARSE_ACTIVE)] = a[:MAX_COARSE_ACTIVE]
        co_opp[i, : min(len(b), MAX_COARSE_ACTIVE)] = b[:MAX_COARSE_ACTIVE]
        if i and i % 200000 == 0:
            print(f"    coarse {i:,}/{len(arr):,}  ({time.time() - started:.0f}s)", flush=True)
    print(f"  coarse encoding took {time.time() - started:.0f}s", flush=True)
    if overflow:
        print(f"  WARNING: {overflow:,} positions passed {MAX_COARSE_ACTIVE} and were truncated")

    pad_ka = dataset.INPUT_SIZE + HALFKA_VIRTUAL
    return (torch.from_numpy(np.where(ka_stm < 0, pad_ka, ka_stm).astype(np.int64)),
            torch.from_numpy(np.where(ka_opp < 0, pad_ka, ka_opp).astype(np.int64)),
            torch.from_numpy(np.where(co_stm < 0, N_COARSE, co_stm).astype(np.int64)),
            torch.from_numpy(np.where(co_opp < 0, N_COARSE, co_opp).astype(np.int64)),
            torch.from_numpy(scores), torch.from_numpy(results))


def run(arm, data, ft_out, with_coarse, epochs, batch, lam, device):
    ka_stm, ka_opp, co_stm, co_opp, scores, results = data
    n = len(scores)
    split = int(n * 0.95)
    net = Net(ft_out, with_coarse).to(device)
    opt = torch.optim.Adam(net.parameters(), lr=1e-3)
    target = lam * torch.sigmoid(scores / 400.0) + (1 - lam) * results
    started = time.time()
    best = float("inf")
    for epoch in range(epochs):
        net.train()
        perm = torch.randperm(split)
        for s in range(0, split, batch):
            idx = perm[s:s + batch]
            opt.zero_grad()
            pred = torch.sigmoid(net(ka_stm[idx].to(device), ka_opp[idx].to(device),
                                     co_stm[idx].to(device), co_opp[idx].to(device)))
            loss = torch.mean((pred.squeeze(1) - target[idx].to(device)) ** 2)
            loss.backward()
            opt.step()
        net.eval()
        with torch.no_grad():
            vals = []
            for s in range(split, n, batch):
                pred = torch.sigmoid(net(ka_stm[s:s + batch].to(device), ka_opp[s:s + batch].to(device),
                                         co_stm[s:s + batch].to(device), co_opp[s:s + batch].to(device)))
                vals.append(torch.mean((pred.squeeze(1) - target[s:s + batch].to(device)) ** 2).item()
                            * (min(s + batch, n) - s))
            val = sum(vals) / (n - split)
        best = min(best, val)
        print(f"  [{arm}] epoch {epoch + 1}/{epochs}  val {val:.6f}  best {best:.6f}"
              f"  ({time.time() - started:.0f}s)", flush=True)
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", nargs="+", required=True)
    ap.add_argument("--positions", type=int, default=2_000_000)
    ap.add_argument("--epochs", type=int, default=12)
    ap.add_argument("--batch", type=int, default=16384)
    ap.add_argument("--lam", type=float, default=0.85)
    ap.add_argument("--ft-out", type=int, default=128)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--gpu-fraction", type=float, default=0.25)
    args = ap.parse_args()

    refuse_to_share_the_gpu(args.force)
    device = "cuda" if torch.cuda.is_available() else "cpu"
    if device == "cuda":
        cap_gpu_memory(args.gpu_fraction)

    data = load(args.data, args.positions)

    print(f"\n=== base: factorized HalfKA at ft{args.ft_out}", flush=True)
    base = run("base", data, args.ft_out, False, args.epochs, args.batch, args.lam, device)
    print(f"\n=== base + coarse threats (144 buckets)", flush=True)
    coarse = run("coarse", data, args.ft_out, True, args.epochs, args.batch, args.lam, device)

    gain = 100 * (base - coarse) / base
    print("\n================= VERDICT =================")
    print(f"base val {base:.6f}   coarse val {coarse:.6f}   gain {gain:+.2f}%")
    print("fine-set reference at this width: +3.96%. Kill line: +1.30%.")
    print("ABOVE the line -> price the engine path (gate 2)."
          if gain >= 1.30 else
          "BELOW the line -> bury the idea, spend nothing more on it.")


if __name__ == "__main__":
    main()
