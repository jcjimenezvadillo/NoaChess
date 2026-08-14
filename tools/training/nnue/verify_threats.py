# Checks the threat encoder against an independent implementation.
#
# The project's recurring failure is not the algorithm, it is the parity between
# two implementations of it. python-chess is the independent one here: it knows
# nothing about this code and generates attacks its own way, so agreeing with it
# on real positions is evidence, where agreeing with myself would be none.
import sys
import numpy as np
import chess

import threats


def bitboard_of(board, colour, piece_type):
    bb = 0
    for sq in board.pieces(piece_type + 1, colour):   # python-chess: PAWN = 1
        bb |= 1 << sq
    return bb


def attacks_reference(board, colour, piece_type):
    """Every square attacked by every piece of that type and colour."""
    out = 0
    for sq in board.pieces(piece_type + 1, colour):
        out |= int(board.attacks(sq))
    return out


def main(sample=300):
    rng = np.random.default_rng(7)
    boards = []

    # Positions from real games, walked to a random depth, so the occupancies
    # are the shapes the encoder will actually meet.
    for _ in range(sample):
        b = chess.Board()
        for _ in range(int(rng.integers(0, 60))):
            moves = list(b.legal_moves)
            if not moves:
                break
            b.push(moves[int(rng.integers(0, len(moves)))])
        if not b.is_game_over():
            boards.append(b)

    print(f"{len(boards)} positions")
    failures = 0

    # --- sliding attacks, the only part that is not a table lookup ----------
    for piece, deltas, name in ((chess.BISHOP - 1, threats.BISHOP_DELTAS, "bishop"),
                                (chess.ROOK - 1, threats.ROOK_DELTAS, "rook"),
                                (chess.QUEEN - 1, threats.BISHOP_DELTAS + threats.ROOK_DELTAS,
                                 "queen")):
        bad = 0
        for b in boards:
            for colour in (chess.WHITE, chess.BLACK):
                frm = bitboard_of(b, colour, piece)
                if frm == 0:
                    continue
                occ = int(b.occupied)
                mine = threats.slider_attacks(np.array([frm], dtype=np.uint64),
                                              np.array([occ], dtype=np.uint64),
                                              deltas)[0]
                ref = attacks_reference(b, colour, piece)
                if int(mine) != ref:
                    bad += 1
        print(f"  {name:7s} attacks: {'OK' if bad == 0 else f'{bad} MISMATCHES'}")
        failures += bad

    # --- knights and kings, which are pure lookups ---------------------------
    for piece, table, name in ((chess.KNIGHT - 1, threats.KNIGHT_ATT, "knight"),
                               (chess.KING - 1, threats.KING_ATT, "king")):
        bad = 0
        for b in boards:
            for colour in (chess.WHITE, chess.BLACK):
                mine = 0
                for sq in b.pieces(piece + 1, colour):
                    mine |= int(table[sq])
                if mine != attacks_reference(b, colour, piece):
                    bad += 1
        print(f"  {name:7s} attacks: {'OK' if bad == 0 else f'{bad} MISMATCHES'}")
        failures += bad

    # --- the index itself ----------------------------------------------------
    seen = {}
    collisions = out_of_range = 0
    for attacker in range(6):
        for attacked in range(6):
            for frm in range(64):
                for to in range(64):
                    i = threats.index(0, 4, attacker, frm, attacked, to)
                    if i < 0:
                        continue
                    if not (0 <= i < threats.THREAT_INPUT_SIZE):
                        out_of_range += 1
                    key = i
                    if key in seen and seen[key] != (attacker, frm, attacked, to):
                        collisions += 1
                    seen[key] = (attacker, frm, attacked, to)

    print(f"  index range   : {'OK' if out_of_range == 0 else f'{out_of_range} OUT OF RANGE'}"
          f"   ({len(seen):,} of {threats.THREAT_INPUT_SIZE:,} reachable from one king square)")
    print(f"  index unique  : {'OK' if collisions == 0 else f'{collisions} COLLISIONS'}")
    failures += out_of_range + collisions

    # --- orientation: the mirror has to be a mirror --------------------------
    # A threat seen from white on a board, and the same threat with everything
    # flipped seen from black, must land on the SAME feature. That is the whole
    # point of orienting, and it is the property a wrong flip breaks.
    bad = 0
    for attacker in (threats.KNIGHT, threats.ROOK, threats.QUEEN):
        for attacked in (threats.PAWN, threats.KNIGHT):
            for frm, to in ((11, 27), (36, 44), (1, 18), (60, 44)):
                if threats.SLOT[attacker][frm][to] < 0:
                    continue
                white = threats.index(0, 4, attacker, frm, attacked, to)
                black = threats.index(1, 4 ^ 56, attacker, frm ^ 56, attacked, to ^ 56)
                if white != black:
                    bad += 1
    print(f"  orientation   : {'OK' if bad == 0 else f'{bad} ASYMMETRIC'}")
    failures += bad

    print()
    print("THREAT ENCODER OK" if failures == 0 else f"{failures} PROBLEM(S)")
    return 1 if failures else 0


sys.exit(main())
