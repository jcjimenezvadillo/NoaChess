# Threat features: a PROBE, not the shipping schema.
#
# WHAT THIS IS FOR. docs/THREAT_FEATURES_SPEC.md identifies one risk that has to
# be settled before weeks of C# work: the reference carries these features with a
# transformer 1024 wide, ours is 128, and width has already been MEASURED as a
# loser (fqw256 at -31.9). Feeding an input four times richer into a transformer
# eight times narrower can come out flat, and a flat result would not distinguish
# "threats do not help" from "threats do not fit". That ambiguity is what this
# module exists to resolve, in Python alone, before anything touches the engine.
#
# WHAT IT IS NOT. It is not parity with the reference and does not claim to be.
# The spec's own arithmetic does not close: summing numValidTargets against the
# pseudo-attack counts gives 30,360 dimensions, exactly half the 60,720 quoted,
# so one factor of two in that description is unaccounted for. Rather than guess
# which, this builds the index from its own construction and REPORTS the
# dimension it actually produces. For deciding whether a richer input helps at
# all, the exact reference numbering is irrelevant; the encoding only has to be
# consistent and informative. If the probe says go, parity gets settled then,
# against the source, with the usual C#-versus-Python integer check.
#
# ORIENTATION is the real HalfKAv2_hm one, reused verbatim from dataset.py, so
# the two feature sets agree about which way the board faces.
import numpy as np

# Piece types, as in NoaChess.Core.PieceType.
PAWN, KNIGHT, BISHOP, ROOK, QUEEN, KING = range(6)

# Which (attacker, attacked) pairs are recorded. -1 means "not recorded".
# Straight from the spec's table: a pawn only records against pawn, knight and
# rook; a bishop records nothing against a queen; a king records nothing at all.
PAIR = np.full((6, 6), -1, dtype=np.int16)
PAIR[PAWN,   [PAWN, KNIGHT, ROOK]]                 = [0, 1, 2]
PAIR[KNIGHT, [PAWN, KNIGHT, BISHOP, ROOK, QUEEN]]  = [0, 1, 2, 3, 4]
PAIR[BISHOP, [PAWN, KNIGHT, BISHOP, ROOK]]         = [0, 1, 2, 3]
PAIR[ROOK,   [PAWN, KNIGHT, BISHOP, ROOK]]         = [0, 1, 2, 3]
PAIR[QUEEN,  [PAWN, KNIGHT, BISHOP, ROOK, QUEEN]]  = [0, 1, 2, 3, 4]

# Pairs per attacker, doubled by the direction bit (from < to).
VALID_TARGETS = np.array([2 * int((PAIR[a] >= 0).sum()) for a in range(6)], dtype=np.int64)


def _rays():
    """Pseudo-attacks on an empty board, per piece type and origin square."""
    knight = np.zeros(64, dtype=np.uint64)
    king = np.zeros(64, dtype=np.uint64)
    bishop = np.zeros(64, dtype=np.uint64)
    rook = np.zeros(64, dtype=np.uint64)

    for sq in range(64):
        f, r = sq & 7, sq >> 3
        for df, dr in ((1, 2), (2, 1), (2, -1), (1, -2),
                       (-1, -2), (-2, -1), (-2, 1), (-1, 2)):
            nf, nr = f + df, r + dr
            if 0 <= nf < 8 and 0 <= nr < 8:
                knight[sq] |= np.uint64(1) << np.uint64(nr * 8 + nf)
        for df, dr in ((1, 0), (1, 1), (0, 1), (-1, 1),
                       (-1, 0), (-1, -1), (0, -1), (1, -1)):
            nf, nr = f + df, r + dr
            if 0 <= nf < 8 and 0 <= nr < 8:
                king[sq] |= np.uint64(1) << np.uint64(nr * 8 + nf)
        for df, dr in ((1, 1), (1, -1), (-1, 1), (-1, -1)):
            nf, nr = f + df, r + dr
            while 0 <= nf < 8 and 0 <= nr < 8:
                bishop[sq] |= np.uint64(1) << np.uint64(nr * 8 + nf)
                nf, nr = nf + df, nr + dr
        for df, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nf, nr = f + df, r + dr
            while 0 <= nf < 8 and 0 <= nr < 8:
                rook[sq] |= np.uint64(1) << np.uint64(nr * 8 + nf)
                nf, nr = nf + df, nr + dr

    return knight, king, bishop, rook


KNIGHT_ATT, KING_ATT, BISHOP_ATT, ROOK_ATT = _rays()
QUEEN_ATT = BISHOP_ATT | ROOK_ATT


def _pawn_push_or_attacks():
    """Pushes AND captures, white's view, and only from ranks 2..7.

    The spec is explicit that pawns count their advances as well as their
    captures, which is what makes a pawn feature say "this pawn is coming" and
    not only "this pawn can take".
    """
    out = np.zeros(64, dtype=np.uint64)
    for sq in range(64):
        f, r = sq & 7, sq >> 3
        if not (1 <= r <= 6):            # ranks 2..7 only
            continue
        for df, dr in ((0, 1), (-1, 1), (1, 1)):
            nf, nr = f + df, r + dr
            if 0 <= nf < 8 and 0 <= nr < 8:
                out[sq] |= np.uint64(1) << np.uint64(nr * 8 + nf)
    return out


PAWN_ATT = _pawn_push_or_attacks()
ATTACKS = [PAWN_ATT, KNIGHT_ATT, BISHOP_ATT, ROOK_ATT, QUEEN_ATT, KING_ATT]


def _build_index():
    """offsets[piece][from] and slot[piece][from][to], exactly as the spec lays
    them out: a running total of how many squares each piece attacks from each
    origin, and which of those destinations a given (from, to) is."""
    offsets = np.zeros((6, 64), dtype=np.int64)
    slot = np.full((6, 64, 64), -1, dtype=np.int64)
    total = np.zeros(6, dtype=np.int64)

    for piece in range(6):
        running = 0
        for frm in range(64):
            offsets[piece][frm] = running
            bb = int(ATTACKS[piece][frm])
            k = 0
            while bb:
                to = (bb & -bb).bit_length() - 1
                slot[piece][frm][to] = k
                bb &= bb - 1
                k += 1
            running += k
        total[piece] = running

    return offsets, slot, total


OFFSETS, SLOT, ATTACKED_SQUARES = _build_index()

# Where each attacker's block starts, and how big the whole thing is.
BLOCK_SIZE = VALID_TARGETS * ATTACKED_SQUARES
BLOCK_BASE = np.concatenate(([0], np.cumsum(BLOCK_SIZE)[:-1]))
THREAT_INPUT_SIZE = int(BLOCK_SIZE.sum())


def describe():
    lines = [f"threat features: {THREAT_INPUT_SIZE:,} dimensions"]
    names = "PNBRQK"
    for p in range(6):
        lines.append(f"  {names[p]}  pairs*2 {VALID_TARGETS[p]:2d} "
                     f" attacked squares {ATTACKED_SQUARES[p]:5d} "
                     f" block {BLOCK_SIZE[p]:6,d}")
    return "\n".join(lines)


def index(perspective, king_sq, attacker, frm, attacked, to):
    """One threat feature index, or -1 when the pair is not recorded.

    Orientation is HalfKAv2_hm's, the same one dataset.py uses: mirror by the
    king's file, flip the board for black. The PIECES are oriented too, so a
    black knight seen from black's perspective indexes as a white knight would.
    """
    pair = PAIR[attacker][attacked]
    if pair < 0:
        return -1

    vflip = 0 if perspective == 0 else 56
    orient = 7 if (king_sq & 7) < 4 else 0
    frm_o = frm ^ orient ^ vflip
    to_o = to ^ orient ^ vflip

    if SLOT[attacker][frm_o][to_o] < 0:
        return -1                        # not a square this piece attacks

    direction = 1 if frm_o < to_o else 0
    pair_base = (2 * pair + direction) * ATTACKED_SQUARES[attacker]
    return int(BLOCK_BASE[attacker] + pair_base
               + OFFSETS[attacker][frm_o] + SLOT[attacker][frm_o][to_o])


# ---------------------------------------------------------------------------
# Attack generation over a whole block of positions at once.
#
# Sliding attacks depend on occupancy, so they cannot be a table lookup. They
# are walked one step at a time across EVERY position simultaneously: eight
# directions, seven steps, all numpy. That is 56 vector operations per block,
# which is the difference between minutes and days at twenty million positions.
# ---------------------------------------------------------------------------

_NOT_A = np.uint64(0xFEFEFEFEFEFEFEFE)
_NOT_H = np.uint64(0x7F7F7F7F7F7F7F7F)


def _shift(bb, delta):
    """One step in a direction, with the file wrap masked off."""
    if delta == 1:    return (bb & _NOT_H) << np.uint64(1)
    if delta == -1:   return (bb & _NOT_A) >> np.uint64(1)
    if delta == 8:    return bb << np.uint64(8)
    if delta == -8:   return bb >> np.uint64(8)
    if delta == 9:    return (bb & _NOT_H) << np.uint64(9)
    if delta == 7:    return (bb & _NOT_A) << np.uint64(7)
    if delta == -7:   return (bb & _NOT_H) >> np.uint64(7)
    if delta == -9:   return (bb & _NOT_A) >> np.uint64(9)
    raise ValueError(delta)


def slider_attacks(from_bb, occupancy, deltas):
    """Squares reachable from every set bit of from_bb, stopping ON blockers.

    from_bb and occupancy are arrays of u64, one per position; the walk runs
    for all of them at the same time.
    """
    result = np.zeros_like(from_bb)
    for d in deltas:
        ray = from_bb
        for _ in range(7):
            ray = _shift(ray, d)
            if not ray.any():
                break
            # The blocker's own square IS attacked; what it stops is everything
            # beyond it, so it is added to the result and dropped from the ray.
            result |= ray
            ray &= ~occupancy
    return result


BISHOP_DELTAS = (9, 7, -7, -9)
ROOK_DELTAS = (1, -1, 8, -8)


# ---------------------------------------------------------------------------
# From a decoded record to its threat features.
#
# One position at a time, but every operation inside is a bitboard operation on
# the verified primitives above. At roughly a tenth of a millisecond each this
# is minutes for the few million positions a probe needs, which is the right
# trade: the fully vectorised version is worth writing only if the probe says
# these features are worth having at all.
# ---------------------------------------------------------------------------

MAX_ACTIVE_THREATS = 128


def _piece_map(occupancy, nibbles):
    """(square, type, colour) for every occupied square, in ascending order."""
    out = []
    bb = int(occupancy)
    i = 0
    while bb:
        sq = (bb & -bb).bit_length() - 1
        code = (int(nibbles[i >> 1]) >> (4 * (i & 1))) & 0xF
        out.append((sq, code % 6, code // 6))
        bb &= bb - 1
        i += 1
    return out


def threats_of(occupancy, nibbles, king_sq, perspective):
    """Threat feature indices for one position, from one perspective.

    A threat is recorded when a piece attacks an ENEMY piece and the pair is one
    the table keeps. The attacker's type is oriented for the perspective, which
    is what makes black's own knight index as white's would.
    """
    pieces = _piece_map(occupancy, nibbles)
    occ = np.array([int(occupancy)], dtype=np.uint64)
    by_square = {sq: (t, c) for sq, t, c in pieces}
    out = []

    for sq, ptype, colour in pieces:
        if ptype == KING:
            continue                      # the king records nothing

        one = np.array([np.uint64(1) << np.uint64(sq)], dtype=np.uint64)
        if ptype == KNIGHT:
            att = int(KNIGHT_ATT[sq])
        elif ptype == BISHOP:
            att = int(slider_attacks(one, occ, BISHOP_DELTAS)[0])
        elif ptype == ROOK:
            att = int(slider_attacks(one, occ, ROOK_DELTAS)[0])
        elif ptype == QUEEN:
            att = int(slider_attacks(one, occ, BISHOP_DELTAS + ROOK_DELTAS)[0])
        else:                             # pawn: pushes and captures, its way up
            att = int(PAWN_ATT[sq] if colour == 0 else PAWN_ATT[sq ^ 56] )
            if colour == 1:
                att = int(np.uint64(_flip_bb(att)))

        att &= int(occupancy)             # only threats against something
        while att:
            to = (att & -att).bit_length() - 1
            att &= att - 1
            target = by_square.get(to)
            if target is None or target[1] == colour:
                continue                  # own piece: not a threat
            # The attacker is oriented with the perspective, so both sides index
            # into the same rows.
            i = index(perspective, king_sq, ptype, sq, target[0], to)
            if i >= 0:
                out.append(i)

    return out[:MAX_ACTIVE_THREATS]


def _flip_bb(bb):
    """Mirror a bitboard vertically, rank 1 <-> rank 8."""
    out = 0
    for r in range(8):
        byte = (bb >> (8 * r)) & 0xFF
        out |= byte << (8 * (7 - r))
    return out
