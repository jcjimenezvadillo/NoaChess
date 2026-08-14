# Checks the threat encoder against an independent implementation.
#
# The project's recurring failure is not the algorithm, it is the parity between
# two implementations of it. python-chess is the independent one here: it knows
# nothing about this code and generates attacks its own way, so agreeing with it
# on real positions is evidence, where agreeing with myself would be none.
#
# Four properties, and the third is the one that catches real bugs:
#   1. the packing is injective and inside 60,720
#   2. every relation the encoder emits IS an attack
#   3. every attack that should be recorded IS emitted  (omissions hide here)
#   4. a mirrored board produces the same features
import sys

import chess

import threats as T

# python-chess numbers piece types 1-6 starting at PAWN; this file numbers them
# 0-5. One table, converted once, rather than an offset scattered everywhere.
FROM_CHESS = {chess.PAWN: T.PAWN, chess.KNIGHT: T.KNIGHT, chess.BISHOP: T.BISHOP,
              chess.ROOK: T.ROOK, chess.QUEEN: T.QUEEN, chess.KING: T.KING}


def to_record(board):
    """The NOADATA occupancy + nibble encoding, from a python-chess board."""
    occupancy = 0
    codes = []
    for sq in range(64):
        p = board.piece_at(sq)
        if p is None:
            continue
        occupancy |= 1 << sq
        colour = T.WHITE if p.color == chess.WHITE else T.BLACK
        codes.append(colour * 6 + FROM_CHESS[p.piece_type])

    nibbles = bytearray(16)
    for i, code in enumerate(codes):
        nibbles[i >> 1] |= code << (4 * (i & 1))
    return occupancy, nibbles


def expected_relations(board):
    """Every relation that must be recorded, derived from python-chess alone."""
    targets = {
        T.PAWN:   {T.PAWN, T.KNIGHT, T.ROOK},
        T.KNIGHT: {T.PAWN, T.KNIGHT, T.BISHOP, T.ROOK, T.QUEEN},
        T.BISHOP: {T.PAWN, T.KNIGHT, T.BISHOP, T.ROOK},
        T.ROOK:   {T.PAWN, T.KNIGHT, T.BISHOP, T.ROOK},
        T.QUEEN:  {T.PAWN, T.KNIGHT, T.BISHOP, T.ROOK, T.QUEEN},
    }

    out = set()
    for frm in range(64):
        p = board.piece_at(frm)
        if p is None or p.piece_type == chess.KING:
            continue
        a_type = FROM_CHESS[p.piece_type]
        a_col = T.WHITE if p.color == chess.WHITE else T.BLACK
        attacker = a_col * 6 + a_type

        for to in board.attacks(frm):
            q = board.piece_at(to)
            if q is None or q.piece_type == chess.KING:
                continue
            d_type = FROM_CHESS[q.piece_type]
            if d_type not in targets[a_type]:
                continue
            d_col = T.WHITE if q.color == chess.WHITE else T.BLACK
            out.add((attacker, frm, d_col * 6 + d_type, to))

        # python-chess reports only the diagonals for a pawn, so the push that
        # is blocked by another pawn has to be added here by hand. It is a real
        # feature in the reference and forgetting it would look like an omission
        # on the encoder's side.
        if a_type == T.PAWN:
            to = frm + (8 if a_col == T.WHITE else -8)
            if 0 <= to < 64:
                q = board.piece_at(to)
                if q is not None and q.piece_type == chess.PAWN:
                    d_col = T.WHITE if q.color == chess.WHITE else T.BLACK
                    out.add((attacker, frm, d_col * 6 + T.PAWN, to))
    return out


def sample_boards(n, seed=7):
    import numpy as np
    rng = np.random.default_rng(seed)
    out = []
    while len(out) < n:
        b = chess.Board()
        for _ in range(int(rng.integers(0, 80))):
            moves = list(b.legal_moves)
            if not moves:
                break
            b.push(moves[int(rng.integers(0, len(moves)))])
        if not b.is_game_over() and b.king(chess.WHITE) is not None \
           and b.king(chess.BLACK) is not None:
            out.append(b)
    return out


def main():
    print(T.describe())
    fails = 0

    # --- 1. the packing -----------------------------------------------------
    seen, rec, bad_range = {}, 0, 0
    for att in range(12):
        for dfd in range(12):
            for frm in range(64):
                for to in range(64):
                    i = T.index(0, 0, att, frm, dfd, to)
                    if i < 0:
                        continue
                    rec += 1
                    if not 0 <= i < T.THREAT_INPUT_SIZE:
                        bad_range += 1
                    seen[i] = 1
    injective = rec == len(seen)
    print(f"  packing       : {rec:,} relations, {len(seen):,} distinct, "
          f"{'injective' if injective else 'COLLIDING'}, "
          f"{'in range' if not bad_range else f'{bad_range} OUT OF RANGE'}")
    fails += (not injective) + bad_range

    boards = sample_boards(400)
    print(f"  positions     : {len(boards)}")

    # --- 2 and 3. the extractor against python-chess -------------------------
    missing = spurious = dup = 0
    worst_active = 0
    for b in boards:
        occ, nib = to_record(b)
        white, black = T.active_threats(occ, nib)
        worst_active = max(worst_active, len(white), len(black))
        if len(set(white)) != len(white) or len(set(black)) != len(black):
            dup += 1

        want = expected_relations(b)
        ksq_w = b.king(chess.WHITE)
        # A relation is "emitted" if it produced an index from white's view or,
        # when the dedup drops it there, from black's. Compare on indices.
        want_idx = set()
        for att, frm, dfd, to in want:
            i = T.index(T.WHITE, ksq_w, att, frm, dfd, to)
            if i >= 0:
                want_idx.add(i)

        missing += len(want_idx - set(white))
        spurious += len(set(white) - want_idx)

    print(f"  omissions     : {missing}   {'OK' if not missing else 'MISSING FEATURES'}")
    print(f"  spurious      : {spurious}   {'OK' if not spurious else 'EXTRA FEATURES'}")
    print(f"  duplicates    : {dup} positions   {'OK' if not dup else 'DOUBLE COUNTED'}")
    print(f"  max active    : {worst_active} of {T.MAX_ACTIVE_THREATS} "
          f"{'OK' if worst_active <= T.MAX_ACTIVE_THREATS else 'OVER THE LIMIT'}")
    fails += missing + spurious + dup + (worst_active > T.MAX_ACTIVE_THREATS)

    # --- 4. orientation ------------------------------------------------------
    # Mirroring the board vertically and swapping colours must leave the feature
    # multiset of the corresponding perspective unchanged. That is the property
    # a wrong flip breaks, and it breaks silently.
    asym = 0
    for b in boards[:150]:
        occ, nib = to_record(b)
        w, _ = T.active_threats(occ, nib)
        occ2, nib2 = to_record(b.mirror())
        _, bl2 = T.active_threats(occ2, nib2)
        if sorted(w) != sorted(bl2):
            asym += 1
    print(f"  orientation   : {asym} asymmetric   {'OK' if not asym else 'MIRROR BROKEN'}")
    fails += asym

    print()
    print("THREAT ENCODER OK" if fails == 0 else f"{fails} PROBLEM(S)")
    return 1 if fails else 0


# Guarded so the module can be imported for its helpers. Without this, `import
# verify_threats` runs the whole verification and then calls sys.exit, which
# kills the importing script mid-way - it silently ate the first attempt to
# generate the C# parity fixture.
if __name__ == "__main__":
    sys.exit(main())
