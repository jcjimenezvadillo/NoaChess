# Threat features, ported faithfully from the reference engine's source.
#
# WHAT A FEATURE IS. One active feature per (attacker piece, from, to, attacked
# piece): "this piece, standing here, attacks that piece standing there". Both
# colours of attacker and both colours of attacked are recorded, so a defence of
# one's own piece is as much a feature as an attack on an enemy one. 60,720
# dimensions, at most 128 active. The king only orients the board here; unlike
# HalfKA it does not multiply the space.
#
# THE PACKING. A naive (piece, from, to, attacked) index would need 64x64 square
# pairs, and almost all of them are geometrically impossible. Instead `to` is
# stored as its RANK WITHIN the pseudo-attack set of `from`, and `offsets` holds
# the running total of those set sizes over all earlier `from` squares. That is
# what turns a sparse 64x64 into a dense 336 for a knight or 1456 for a queen.
#
# THE TWO MISTAKES THIS FILE EXISTS TO CORRECT. The previous version of this
# module produced 30,360 dimensions, exactly half, and the reason was not a
# missing factor: it doubled the space on the direction bit `from < to`, while
# the reference doubles it on the COLOUR OF THE ATTACKED PIECE. In the source
# both entries of index_lut1[..][..][from < to] hold the SAME index; the second
# one exists only to be thrown out when attacker and attacked share a piece
# type, because then the relation is symmetric and would otherwise be counted
# from both ends. So the old encoding was not a scaled-down version of the right
# one, it was a differently shaped space that happened to be half the size.
#
# VERIFIED, NOT ASSUMED. `verify_threats.py` checks this against python-chess for
# the attack generation, and checks the packing by enumerating every legal
# (attacker, from, to, attacked) combination: a correct dense packing must reach
# exactly 60,720 distinct indices with no collision and no gap. That test is what
# the C# port will be held to as well.
import numpy as np

# Piece types, as in NoaChess.Core.PieceType.
PAWN, KNIGHT, BISHOP, ROOK, QUEEN, KING = range(6)

# A coloured piece is colour * 6 + type, so 0-5 are white and 6-11 are black.
# The reference uses a different packing; only the numbering differs.
WHITE, BLACK = 0, 1


def piece(colour, pt):
    return colour * 6 + pt


# Which (attacker, attacked) type pairs are recorded, and the slot each one gets
# within that attacker's block. -1 means the pair is not a feature at all.
# Straight from the reference's `map`: a pawn records nothing against a bishop or
# a queen, the sliders record nothing against a queen, and a king records nothing
# at all.
MAP = np.full((6, 6), -1, dtype=np.int32)
MAP[PAWN,   [PAWN, KNIGHT, ROOK]]                = [0, 1, 2]
MAP[KNIGHT, [PAWN, KNIGHT, BISHOP, ROOK, QUEEN]] = [0, 1, 2, 3, 4]
MAP[BISHOP, [PAWN, KNIGHT, BISHOP, ROOK]]        = [0, 1, 2, 3]
MAP[ROOK,   [PAWN, KNIGHT, BISHOP, ROOK]]        = [0, 1, 2, 3]
MAP[QUEEN,  [PAWN, KNIGHT, BISHOP, ROOK, QUEEN]] = [0, 1, 2, 3, 4]

# Twice the number of recorded targets: once for an attacked white piece, once
# for an attacked black one. THIS is where the factor of two belongs.
NUM_VALID_TARGETS = np.array([2 * int((MAP[a] >= 0).sum()) for a in range(6)],
                             dtype=np.int64)

# Which piece types are worth attacking, per attacker type. Mirrors the
# bitboard filters the reference applies before generating any index, and agrees
# with MAP by construction.
TARGET_TYPES = {
    PAWN:   (PAWN, KNIGHT, ROOK),
    KNIGHT: (PAWN, KNIGHT, BISHOP, ROOK, QUEEN),
    BISHOP: (PAWN, KNIGHT, BISHOP, ROOK),
    ROOK:   (PAWN, KNIGHT, BISHOP, ROOK),
    QUEEN:  (PAWN, KNIGHT, BISHOP, ROOK, QUEEN),
    KING:   (),
}

BISHOP_DELTAS = ((1, 1), (1, -1), (-1, 1), (-1, -1))
ROOK_DELTAS = ((1, 0), (-1, 0), (0, 1), (0, -1))
KNIGHT_DELTAS = ((1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2))
KING_DELTAS = ((1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1))


def _leaper(deltas):
    out = np.zeros(64, dtype=np.uint64)
    for sq in range(64):
        f, r = sq & 7, sq >> 3
        bb = 0
        for df, dr in deltas:
            nf, nr = f + df, r + dr
            if 0 <= nf < 8 and 0 <= nr < 8:
                bb |= 1 << (nr * 8 + nf)
        out[sq] = bb
    return out


def _slider(deltas):
    out = np.zeros(64, dtype=np.uint64)
    for sq in range(64):
        f, r = sq & 7, sq >> 3
        bb = 0
        for df, dr in deltas:
            nf, nr = f + df, r + dr
            while 0 <= nf < 8 and 0 <= nr < 8:
                bb |= 1 << (nr * 8 + nf)
                nf, nr = nf + df, nr + dr
        out[sq] = bb
    return out


KNIGHT_ATT = _leaper(KNIGHT_DELTAS)
KING_ATT = _leaper(KING_DELTAS)
BISHOP_ATT = _slider(BISHOP_DELTAS)
ROOK_ATT = _slider(ROOK_DELTAS)
QUEEN_ATT = BISHOP_ATT | ROOK_ATT


def _pawn_push_or_attacks():
    """Both captures and the single push, which is what the reference indexes.

    A pawn feature is not only "attacks that piece": a pawn blocked by another
    pawn directly in front is a positional fact the net is told about, and it
    lives in the same table.
    """
    out = np.zeros((2, 64), dtype=np.uint64)
    for colour, step in ((WHITE, 8), (BLACK, -8)):
        for sq in range(64):
            f, r = sq & 7, sq >> 3
            nr = r + (1 if colour == WHITE else -1)
            if not 0 <= nr < 8:
                continue
            bb = 1 << (sq + step)                       # the push
            if f > 0:
                bb |= 1 << (nr * 8 + f - 1)
            if f < 7:
                bb |= 1 << (nr * 8 + f + 1)
            out[colour][sq] = bb
    return out


PAWN_PUSH_OR_ATT = _pawn_push_or_attacks()


def _pseudo(coloured_piece, sq):
    """The empty-board attack set the packing is built from."""
    colour, pt = divmod(coloured_piece, 6)
    if pt == PAWN:
        # Only ranks 2-7 contribute: a pawn cannot stand on the first or last.
        return PAWN_PUSH_OR_ATT[colour][sq] if 8 <= sq < 56 else np.uint64(0)
    return (KNIGHT_ATT, BISHOP_ATT, ROOK_ATT, QUEEN_ATT, KING_ATT)[pt - 1][sq]


def _build_offsets():
    """Per piece: where its block starts, and how big one target slot is.

    `offsets[p][from]` is the running count of attack targets over every earlier
    `from` square, so a (from, to) pair costs one index and only the reachable
    ones are numbered.
    """
    piece_offset = np.zeros(12, dtype=np.int64)      # slot size for this piece
    block_start = np.zeros(12, dtype=np.int64)       # where this piece's block begins
    offsets = np.zeros((12, 64), dtype=np.int64)

    cumulative = 0
    for p in range(12):
        run = 0
        for sq in range(64):
            offsets[p][sq] = run
            run += int(bin(int(_pseudo(p, sq))).count("1"))
        piece_offset[p] = run
        block_start[p] = cumulative
        cumulative += int(NUM_VALID_TARGETS[p % 6]) * run

    return block_start, piece_offset, offsets, cumulative


BLOCK_START, PIECE_OFFSET, OFFSETS, THREAT_INPUT_SIZE = _build_offsets()

# The reference declares 60,720. If this ever stops matching, the packing has
# drifted and every trained net on the old numbering is invalid.
assert THREAT_INPUT_SIZE == 60720, f"packing gives {THREAT_INPUT_SIZE}, reference says 60720"

MAX_ACTIVE_THREATS = 128


def _build_slot_lut():
    """`to`'s rank within the attack set of `from`, for every piece and pair."""
    lut = np.full((12, 64, 64), -1, dtype=np.int32)
    for p in range(12):
        for frm in range(64):
            attacks = int(_pseudo(p, frm))
            for to in range(64):
                if attacks >> to & 1:
                    lut[p][frm][to] = bin(attacks & ((1 << to) - 1)).count("1")
    return lut


SLOT = _build_slot_lut()


def _build_pair_lut():
    """[attacker][attacked][from < to] -> base index, or -1 when dropped.

    Both entries hold the SAME base. The second exists only so that a symmetric
    relation - a knight attacking a knight, which is also that knight attacking
    this one - is counted from one end and not from both. Friendly pawns are the
    exception: pawn A defending pawn B is not the same fact as B defending A,
    because a pawn cannot defend backwards.
    """
    lut = np.full((12, 12, 2), -1, dtype=np.int64)
    for attacker in range(12):
        for attacked in range(12):
            a_colour, a_type = divmod(attacker, 6)
            d_colour, d_type = divmod(attacked, 6)

            slot = int(MAP[a_type][d_type])
            if slot < 0:
                continue

            base = (BLOCK_START[attacker]
                    + (d_colour * (NUM_VALID_TARGETS[a_type] // 2) + slot)
                    * PIECE_OFFSET[attacker])

            enemy = a_colour != d_colour
            semi_excluded = a_type == d_type and (enemy or a_type != PAWN)

            lut[attacker][attacked][0] = base
            lut[attacker][attacked][1] = -1 if semi_excluded else base
    return lut


PAIR_LUT = _build_pair_lut()

# Horizontal mirror, and it deliberately does NOT copy the reference here.
#
# The reference mirrors so its king ends on files a-d; NoaChess mirrors so the
# king ends on files e-h, which is what NnueFeatureIndex.Orient has always done
# for HalfKAv2_hm. Either is correct on its own - the orientation is a pure
# relabelling of the space - but the two feature sets inside this engine have to
# face the same way, and HalfKA is the one already frozen into every shipped net.
#
# Copying the reference here cost a full parity failure: 252 of 256 cases, all
# from this one XOR, with the packing tables byte-identical on both sides.
ORIENT = np.array([7 if (sq & 7) < 4 else 0 for sq in range(64)], dtype=np.int32)


# Flat Python lists for the index hot path, and this is a measurement rather
# than a preference.
#
# Profiling the encoder put 40.5% of its time inside `index` and only 8.2% in
# slider attack generation - the opposite of the guess, which was about to send
# me vectorising the sliders. The cost is not arithmetic, it is numpy: every
# `SLOT[a][f][t]` builds intermediate array views and returns a numpy scalar,
# and `index` does four of those per call, sixty-two times per position.
#
# Plain lists with the offsets multiplied out avoid all of it. Same lesson the
# engine already learned in C#, where flattening the history and piece tables
# was worth +6.1% nps at identical node counts: look at the layout before the
# algorithm.
#
# The numpy tables above are kept as they are - the verifier and the virtual
# table build read them, they are not hot, and duplicating 50k entries costs
# nothing next to being able to check one against the other.
_ORIENT_F = [int(v) for v in ORIENT]
_OFFSETS_F = [int(v) for v in OFFSETS.reshape(-1)]
_SLOT_F = [int(v) for v in SLOT.reshape(-1)]
_PAIR_F = [int(v) for v in PAIR_LUT.reshape(-1)]


def index(perspective, king_sq, attacker, frm, attacked, to):
    """The feature index, or -1 when this relation is not recorded.

    `attacker` and `attacked` are coloured pieces in absolute terms; the
    perspective flip is applied here, once, exactly as the reference does it.
    """
    orientation = _ORIENT_F[king_sq] ^ (56 * perspective)
    frm_o = frm ^ orientation
    to_o = to ^ orientation

    # Flipping perspective swaps the colours of both pieces.
    att_o = (attacker + 6) % 12 if perspective else attacker
    dfd_o = (attacked + 6) % 12 if perspective else attacked

    base = _PAIR_F[(att_o * 12 + dfd_o) * 2 + (1 if frm_o < to_o else 0)]
    if base < 0:
        return -1

    # The geometry check the reference does not need and this does. Over there
    # make_index is only ever reached from a bitboard of real attacks, so `to`
    # is always in `from`'s attack set. Here the function is also called from
    # tests and from the feature extractor, and without this line a pair that is
    # not an attack at all returns base + offset - 1: a perfectly valid-looking
    # index belonging to some other relation. That is a silent wrong label, the
    # worst kind, and it took a white pawn "attacking" the square behind it to
    # surface.
    slot = _SLOT_F[(att_o * 64 + frm_o) * 64 + to_o]
    if slot < 0:
        return -1

    return base + _OFFSETS_F[att_o * 64 + frm_o] + slot


NOT_A = ~0x0101010101010101 & 0xFFFFFFFFFFFFFFFF
NOT_H = ~0x8080808080808080 & 0xFFFFFFFFFFFFFFFF


def _shift(bb, d):
    """One-square shift, with the wrap off the a and h files removed."""
    if d == 8:
        return (bb << 8) & 0xFFFFFFFFFFFFFFFF
    if d == -8:
        return bb >> 8
    if d == 9:
        return (bb & NOT_H) << 9 & 0xFFFFFFFFFFFFFFFF
    if d == 7:
        return (bb & NOT_A) << 7 & 0xFFFFFFFFFFFFFFFF
    if d == -7:
        return (bb & NOT_H) >> 7
    if d == -9:
        return (bb & NOT_A) >> 9
    raise ValueError(d)


def _slider_attacks(frm, occupied, deltas):
    """Attacks from one square, stopping on the first blocker and including it."""
    bb = 0
    f, r = frm & 7, frm >> 3
    for df, dr in deltas:
        nf, nr = f + df, r + dr
        while 0 <= nf < 8 and 0 <= nr < 8:
            sq = nr * 8 + nf
            bb |= 1 << sq
            if occupied >> sq & 1:
                break
            nf, nr = nf + df, nr + dr
    return bb


def _bits(bb):
    while bb:
        low = bb & -bb
        yield low.bit_length() - 1
        bb ^= low


def board_from_record(occupancy, nibbles):
    """(board array, per-piece bitboards) from one NOADATA record.

    Piece codes are colour * 6 + type with type 5 for the king, which is the
    same coding `dataset.py` uses, so the two feature sets read the record the
    same way.
    """
    board = [-1] * 64
    bbs = [0] * 12
    for i, sq in enumerate(_bits(int(occupancy))):
        code = (int(nibbles[i >> 1]) >> (4 * (i & 1))) & 0xF
        board[sq] = code
        bbs[code] |= 1 << sq
    return board, bbs


def active_threats(occupancy, nibbles):
    """Active threat features for both perspectives.

    Follows the reference's append_active_indices: every attacker of every
    colour, filtered to the target types that attacker actually records, plus
    the pawn push that is blocked by another pawn. Returns (white, black).
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

    kings = [None, None]
    for c in (WHITE, BLACK):
        k = bbs[piece(c, KING)]
        kings[c] = (k & -k).bit_length() - 1 if k else None

    # Collect the relations once; the two perspectives only renumber them.
    relations = []

    for c in (WHITE, BLACK):
        attacker = piece(c, PAWN)
        c_pawns = bbs[attacker]

        # A pawn stopped by a pawn directly in front. `pawns` is both colours,
        # exactly as the reference has it: what matters is that the blocker is a
        # pawn, not whose it is.
        back = -8 if c == WHITE else 8
        pushers = _shift(pawns, back) & c_pawns
        caps = (9, 7) if c == WHITE else (-7, -9)
        push = 8 if c == WHITE else -8

        for d in caps:
            for to in _bits(_shift(c_pawns, d) & pawn_targets):
                relations.append((attacker, to - d, board[to], to))
        for to in _bits(_shift(pushers, push)):
            relations.append((attacker, to - push, board[to], to))

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
                    relations.append((attacker, frm, board[to], to))

    out = ([], [])
    for perspective in (WHITE, BLACK):
        ksq = kings[perspective]
        if ksq is None:
            continue
        for att, frm, dfd, to in relations:
            i = index(perspective, ksq, att, frm, dfd, to)
            if i >= 0:
                out[perspective].append(i)
    return out


# ---- feature factorization -------------------------------------------------
#
# WHY THIS IS HERE AT ALL. Factorization is the largest measured gain this
# project has ever made (+128 Elo in the field, v4.6.0), and what it does is
# narrow: it adds shared "virtual" rows that every real feature fires alongside
# its own, so a row that would starve for gradient collects it 32 times over.
# That is a cure for feature SPARSITY, and threat features are far sparser than
# HalfKA - 60,720 dimensions against 22,528, with roughly half the updates per
# weight on the same corpus. Measuring threats WITHOUT factorization tests them
# with their characteristic defect left in.
#
# THE THREE FACTORS, by analogy with HalfKA dropping the king bucket:
#   (attacker, attacked)  - what threatens what, regardless of where
#   (attacker, from)      - this piece standing here, threatening anything
#   (attacked, to)        - this piece standing here, threatened by anything
# Each is dense enough to collect gradient from thousands of real features.
VIRTUAL_PAIR = 12 * 12               # 144
VIRTUAL_FROM = 12 * 64               # 768
VIRTUAL_TO = 12 * 64                 # 768
VIRTUAL_SIZE = VIRTUAL_PAIR + VIRTUAL_FROM + VIRTUAL_TO
FACTORED_INPUT_SIZE = THREAT_INPUT_SIZE + VIRTUAL_SIZE


def _build_virtual_table():
    """For every reachable threat index, the three virtual rows it also fires.

    Built by enumerating the packing in ORIENTED space (a king on e1 gives
    orientation 0), which is the same space `index` produces, so the table can
    be looked up directly by feature index with no decoding at run time.
    """
    table = np.full((THREAT_INPUT_SIZE, 3), -1, dtype=np.int32)
    king_no_mirror = 4                       # e1: file 4, so ORIENT is 0
    for att in range(12):
        for dfd in range(12):
            for frm in range(64):
                for to in range(64):
                    i = index(0, king_no_mirror, att, frm, dfd, to)
                    if i < 0:
                        continue
                    table[i] = (
                        THREAT_INPUT_SIZE + att * 12 + dfd,
                        THREAT_INPUT_SIZE + VIRTUAL_PAIR + att * 64 + frm,
                        THREAT_INPUT_SIZE + VIRTUAL_PAIR + VIRTUAL_FROM + dfd * 64 + to,
                    )
    return table


VIRTUALS = _build_virtual_table()


def factorize(indices):
    """Real threat indices plus the virtual rows they fire, as one flat list."""
    out = list(indices)
    for i in indices:
        out.extend(int(v) for v in VIRTUALS[i] if v >= 0)
    return out


def describe():
    return (f"threat features: {THREAT_INPUT_SIZE:,} dimensions, "
            f"<= {MAX_ACTIVE_THREATS} active, ported from the reference source")
