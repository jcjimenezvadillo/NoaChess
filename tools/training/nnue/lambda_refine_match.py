# Lambda REFINE match: each (lambda, seed) net plays vs the gen3 baseline, then
# scores are AGGREGATED PER LAMBDA across seeds (2 seeds x 30 games = 60 games
# per lambda) and reported WITH ERROR BARS, so you can see whether any lambda is
# really better than 0.925 or whether they all sit inside the noise.
#
# Most likely verdict: they overlap -> 0.925 is fine, lambda is not a lever.
# Only act if one lambda clears the others by more than ~2 standard errors.
#
# Usage:  python lambda_refine_match.py

import os
import sys
import math
import random

import chess
import chess.engine

ENGINE   = r"F:\Works\_______________CHESSTEST\engines\NoaChess-3.0.0\NoaChess.UCI.exe"
BASELINE = r"F:\Works\Programacion\__Repos\NoaChess\models\nnue\noa-gen3.noannue"
NETS_DIR = r"F:\Works\Programacion\__Repos\NoaChess\models\nnue"

LAMBDAS  = [0.85, 0.875, 0.90, 0.925, 0.95]
SEEDS    = [42, 777]
NODES    = 15000    # per move, equal both sides
N_PAIRS  = 15       # per net -> 30 games/net -> 60 games/lambda aggregated
RESIGN   = 1500
MAXMOVES = 120
OPEN_SEED = 2025


def make_engine(net_path):
    e = chess.engine.SimpleEngine.popen_uci(ENGINE)
    e.configure({"UseNNUE": True, "EvalFile": net_path})
    return e


def opening(seed):
    b = chess.Board()
    r = random.Random(seed)
    for _ in range(8):
        moves = list(b.legal_moves)
        if not moves:
            return None
        b.push(r.choice(moves))
        if b.is_game_over():
            return None
    return b


def play_pair(ob, eng_a, eng_b):
    results = []
    for a_white in (True, False):
        b = ob.copy()
        streak = 0
        last_sign = 0
        white_result = None
        while not b.is_game_over(claim_draw=True) and b.fullmove_number < MAXMOVES:
            eng = (eng_a if a_white else eng_b) if b.turn == chess.WHITE else (eng_b if a_white else eng_a)
            res = eng.play(b, chess.engine.Limit(nodes=NODES), info=chess.engine.INFO_SCORE)
            if res.move is None:
                break
            sc = res.info.get("score")
            if sc is not None:
                wcp = sc.white().score(mate_score=100000)
                sign = 1 if wcp > RESIGN else (-1 if wcp < -RESIGN else 0)
                streak = (streak + 1) if (sign != 0 and sign == last_sign) else (1 if sign != 0 else 0)
                last_sign = sign
                if streak >= 5:
                    white_result = sign
                    break
            b.push(res.move)
        if white_result is None:
            oc = b.outcome(claim_draw=True)
            white_result = 0 if (oc is None or oc.winner is None) else (1 if oc.winner == chess.WHITE else -1)
        results.append(white_result if a_white else -white_result)
    return results


def match_net(net_path, tag):
    eng_c = make_engine(net_path)
    eng_b = make_engine(BASELINE)
    w = d = l = 0
    rng = random.Random(OPEN_SEED)
    openings = [opening(rng.randint(0, 1000000)) for _ in range(N_PAIRS)]
    for i, ob in enumerate(openings):
        if ob is None:
            continue
        for r in play_pair(ob, eng_c, eng_b):
            if r > 0:   w += 1
            elif r < 0: l += 1
            else:       d += 1
        n = w + d + l
        print(f"    [{tag}] pair {i+1}/{N_PAIRS}  {w}-{l}-{d}  score={(w+0.5*d)/n:.3f}", flush=True)
    eng_c.quit()
    eng_b.quit()
    return w, d, l


def preflight():
    missing = []
    if not os.path.exists(ENGINE):   missing.append(f"ENGINE   {ENGINE}")
    if not os.path.exists(BASELINE): missing.append(f"BASELINE {BASELINE}")
    for lam in LAMBDAS:
        for s in SEEDS:
            p = f"{NETS_DIR}\\lref-{lam:.3f}-s{s}.noannue"
            if not os.path.exists(p):
                missing.append(f"lambda {lam:.3f} seed {s}  {p}")
    if missing:
        print("PREFLIGHT FAILED - missing files:")
        for m in missing:
            print(f"  MISSING: {m}")
        print("\nRun lambda_refine_train.py first (or lambda_refine.bat).")
        sys.exit(1)
    print(f"preflight OK: engine + baseline + {len(LAMBDAS)*len(SEEDS)} nets present\n")


def main():
    preflight()
    print(f"Lambda REFINE - {len(LAMBDAS)} lambdas x {len(SEEDS)} seeds x {N_PAIRS*2} games")
    print(f"Baseline: noa-gen3.noannue   Nodes/move: {NODES}\n")

    agg = {lam: [0, 0, 0] for lam in LAMBDAS}   # w, d, l aggregated over seeds
    per_seed = {}
    for lam in LAMBDAS:
        for s in SEEDS:
            tag = f"{lam:.3f}-s{s}"
            print(f"\n--- lambda={lam:.3f} seed={s} ---")
            w, d, l = match_net(f"{NETS_DIR}\\lref-{lam:.3f}-s{s}.noannue", tag)
            per_seed[tag] = (w, d, l)
            agg[lam][0] += w; agg[lam][1] += d; agg[lam][2] += l

    def score_se(w, d, l):
        n = w + d + l
        if n == 0:
            return 0.5, 0.0
        p = (w + 0.5 * d) / n
        se = math.sqrt(max(p * (1 - p), 1e-9) / n)
        return p, se

    def seed_score(lam, s):
        w, d, l = per_seed[f"{lam:.3f}-s{s}"]
        n = w + d + l
        return (w + 0.5 * d) / n if n else 0.5

    rows = []
    for lam in LAMBDAS:
        w, d, l = agg[lam]
        p, se = score_se(w, d, l)
        rows.append((lam, w, d, l, p, se))
    rows.sort(key=lambda r: -r[4])

    print("\n" + "=" * 64)
    print("  LAMBDA REFINE - AGGREGATED PER LAMBDA (vs gen4 baseline)")
    print("=" * 64)
    print("  rank  lambda   W-D-L      score   +/-SE    per-seed scores")
    for rank, (lam, w, d, l, p, se) in enumerate(rows, 1):
        seeds_txt = "  ".join(f"s{s}={seed_score(lam, s):.3f}" for s in SEEDS)
        print(f"  #{rank:<3d} {lam:.3f}  {w:3d}-{l:3d}-{d:3d}   {p:.3f}   {se:.3f}   {seeds_txt}")

    best_lam, _, _, _, best_p, best_se = rows[0]
    # Is the winner clearly separated from the rest? Compare to 2nd place.
    second = rows[1]
    diff = best_p - second[4]
    se_diff = math.sqrt(best_se ** 2 + second[5] ** 2)
    significant = diff > 2 * se_diff

    # AUTO-APPLY: write the chosen lambda to LAMBDA_BEST.txt, which the pipeline
    # PS1 reads on every run. Only override 0.925 if a lambda is SIGNIFICANTLY
    # better; otherwise keep 0.925 (chasing a within-noise winner would hurt).
    lambda_file = r"F:\Works\_______________CHESSTEST\LAMBDA_BEST.txt"
    chosen = f"{best_lam:.3f}" if significant else "0.925"
    try:
        with open(lambda_file, "w", encoding="ascii") as f:
            f.write(chosen)
        wrote = f"written to {lambda_file}"
    except OSError as e:
        wrote = f"COULD NOT WRITE {lambda_file} ({e}) -- set it by hand to {chosen}"

    print("\n" + "-" * 64)
    if significant:
        print(f"  VERDICT: lambda={best_lam:.3f} is SIGNIFICANTLY best")
        print(f"           (+{diff:.3f} over #2, {diff/max(se_diff,1e-9):.1f} SE).")
        print(f"  APPLIED: pipeline lambda -> {chosen}  ({wrote})")
    else:
        print(f"  VERDICT: TOP LAMBDAS WITHIN NOISE (best beats #2 by {diff:.3f}, only")
        print(f"           {diff/max(se_diff,1e-9):.1f} SE). No lambda is provably better.")
        print(f"  APPLIED: pipeline lambda stays 0.925  ({wrote})")
        print(f"           Lambda is not a real lever in this range. Do not tune it again.")
    print("-" * 64 + "\n")


if __name__ == "__main__":
    main()
