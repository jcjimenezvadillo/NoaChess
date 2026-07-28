# Lambda sweep: A/B each lsweep-*.noannue vs the gen3 net (lambda=0.9 baseline)
# at equal nodes per move — speed-neutral comparison of eval quality only.
#
# Usage:
#   python lambda_sweep_match.py
#
# Output: ranked table of lambda scores vs baseline (gen3 = lambda 0.9).

import os
import sys

import chess
import chess.engine
import random

ENGINE    = r"F:\Works\_______________CHESSTEST\engines\NoaChess-3.0.0\NoaChess.UCI.exe"
BASELINE  = r"F:\Works\Programacion\__Repos\NoaChess\models\nnue\noa-gen3.noannue"
NETS_DIR  = r"F:\Works\Programacion\__Repos\NoaChess\models\nnue"

LAMBDAS   = [0.75, 0.775, 0.80, 0.825, 0.85, 0.875, 0.90, 0.925, 0.95]
NODES     = 15000   # per move, equal for both sides
N_PAIRS   = 20      # each played as white+black = 40 games per lambda
RESIGN    = 1500    # |cp| for 5 consecutive moves -> decisive
MAXMOVES  = 120
SEED      = 2025


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


def play_game(eng_white, eng_black):
    """Returns +1 white wins, -1 black wins, 0 draw."""
    # We need a fresh board — caller passes opening board copy
    raise NotImplementedError  # replaced below


def play_pair(ob, eng_a, eng_b):
    """Plays two games from opening ob: a=white then a=black. Returns (a_result_g1, a_result_g2)."""
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
        a_result = white_result if a_white else -white_result
        results.append(a_result)
    return results


def ab_match(challenger_path, lam):
    """Plays challenger (lsweep) vs BASELINE (gen3). Returns (wins, draws, losses, score)."""
    eng_c = make_engine(challenger_path)
    eng_b = make_engine(BASELINE)
    w = d = l = 0
    rng = random.Random(SEED)
    openings = [opening(rng.randint(0, 1000000)) for _ in range(N_PAIRS)]
    for i, ob in enumerate(openings):
        if ob is None:
            continue
        r1, r2 = play_pair(ob, eng_c, eng_b)
        for r in (r1, r2):
            if r > 0:
                w += 1
            elif r < 0:
                l += 1
            else:
                d += 1
        n = w + d + l
        score = (w + 0.5 * d) / n if n > 0 else 0.5
        print(f"    [{lam:.3f}] pair {i+1}/{N_PAIRS}  {w}-{l}-{d}  score={score:.3f}", flush=True)
    eng_c.quit()
    eng_b.quit()
    n = w + d + l
    score = (w + 0.5 * d) / n if n > 0 else 0.5
    return w, d, l, score


def preflight():
    """Verify every net file exists BEFORE playing. If a .noannue is missing,
    the engine silently keeps whatever eval it had (embedded net or classical),
    so that lambda's games would be meaningless and read as a false ~0.500 or a
    false loss. Fail loudly instead."""
    missing = []
    if not os.path.exists(ENGINE):
        missing.append(f"ENGINE   {ENGINE}")
    if not os.path.exists(BASELINE):
        missing.append(f"BASELINE {BASELINE}")
    for lam in LAMBDAS:
        p = f"{NETS_DIR}\\lsweep-{lam:.3f}.noannue"
        if not os.path.exists(p):
            missing.append(f"lambda {lam:.3f}  {p}")
    if missing:
        print("PREFLIGHT FAILED — these files are missing:")
        for m in missing:
            print(f"  MISSING: {m}")
        print("\nRun lambda_sweep_export.py first (or the full lambda_sweep.bat).")
        sys.exit(1)
    print(f"preflight OK: engine + baseline + {len(LAMBDAS)} lambda nets present\n")


def main():
    preflight()
    print(f"Lambda sweep A/B — {N_PAIRS*2} games per lambda vs gen3 (baseline=0.90)")
    print(f"Nodes/move: {NODES}  |  Baseline net: noa-gen3.noannue\n")

    results = {}
    for lam in LAMBDAS:
        tag  = f"lsweep-{lam:.3f}"
        path = f"{NETS_DIR}\\{tag}.noannue"
        print(f"\n--- Lambda={lam:.3f} ---")
        w, d, l, score = ab_match(path, lam)
        results[lam] = (w, d, l, score)
        marker = " <-- CURRENT" if abs(lam - 0.90) < 0.001 else ""
        print(f"  RESULT  lambda={lam:.3f}  {w}-{l}-{d}  score={score:.3f}{marker}")

    print("\n" + "=" * 55)
    print(f"  LAMBDA SWEEP FINAL RANKING (vs gen3 baseline 0.90)")
    print("=" * 55)
    ranked = sorted(results.items(), key=lambda x: -x[1][3])
    for rank, (lam, (w, d, l, score)) in enumerate(ranked, 1):
        marker = " <-- BEST" if rank == 1 else (" <-- CURRENT" if abs(lam - 0.90) < 0.001 else "")
        print(f"  #{rank:2d}  lambda={lam:.3f}  {w}-{l}-{d}  score={score:.3f}{marker}")
    print()
    best_lam = ranked[0][0]
    print(f"  >> Use --lambda {best_lam:.3f} for gen4 training.\n")


if __name__ == "__main__":
    main()
