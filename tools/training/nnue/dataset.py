# Reads NOADATA1 datasets (written by tools/NoaChess.DataGen) and converts
# records into sparse HalfKP feature indices for training.
#
# The binary layouts and the feature schema are contracts shared with the C#
# side (DatasetFormat.cs and NnueFeatureIndex.cs); any change there requires
# a matching change here and a new schema/version id.

import os

import numpy as np

HEADER_SIZE = 64
RECORD_SIZE = 40
MAGIC = b"NOADATA1"
FEATURE_SCHEMA_ID = 1

# HalfKP dimensions (must match NnueFeatureIndex.cs).
FEATURES_PER_KSQ = 10 * 64
INPUT_SIZE = 64 * FEATURES_PER_KSQ  # 40,960 per perspective
MAX_ACTIVE = 30

RECORD_DTYPE = np.dtype([
    ("occupancy", "<u8"),
    ("pieces", "u1", 16),      # nibbles, ascending square order
    ("stm", "u1"),
    ("castling", "u1"),
    ("ep", "u1"),
    ("halfmove", "u1"),
    ("ply", "<u2"),
    ("score", "<i2"),          # cp, side to move
    ("result", "i1"),          # +1/0/-1, side to move
    ("pad", "u1"),
    ("best_move", "<u2"),
    ("reserved", "<u4"),
])
assert RECORD_DTYPE.itemsize == RECORD_SIZE


def load_records(path):
    """Memory-maps a .noadata file and returns the record array."""
    with open(path, "rb") as f:
        header = f.read(HEADER_SIZE)
    if header[:8] != MAGIC:
        raise ValueError(f"{path}: not a NOADATA1 file")
    version = int.from_bytes(header[8:12], "little")
    schema = int.from_bytes(header[12:16], "little")
    record_size = int.from_bytes(header[20:24], "little")
    count = int.from_bytes(header[24:32], "little")
    if version != 1 or schema != FEATURE_SCHEMA_ID or record_size != RECORD_SIZE:
        raise ValueError(f"{path}: incompatible header (v{version} schema {schema} rec {record_size})")

    records = np.memmap(path, dtype=RECORD_DTYPE, mode="r",
                        offset=HEADER_SIZE, shape=(count,))
    return records


def _unpack_squares(occupancy):
    """Square indices (ascending) of the set bits of one occupancy value."""
    squares = []
    occ = int(occupancy)
    while occ:
        lsb = occ & -occ
        squares.append(lsb.bit_length() - 1)
        occ ^= lsb
    return squares


def record_to_features(rec):
    """
    Decodes one record into (white_features, black_features, stm, score, result).

    Feature index (mirror of NnueFeatureIndex.Index):
      index = kingSq * 640 + (pieceType*2 + (0 own | 1 enemy)) * 64 + pieceSq
    with both squares vertically flipped (sq ^ 56) for the black perspective.
    """
    squares = _unpack_squares(rec["occupancy"])
    nibbles = rec["pieces"]

    pieces = []          # (square, piece_type 0..5, color 0 white / 1 black)
    kings = [None, None]
    for i, sq in enumerate(squares):
        # int() casts break out of numpy uint8 arithmetic (which overflows).
        code = (int(nibbles[i // 2]) >> (4 * (i % 2))) & 0xF
        ptype, color = code % 6, code // 6
        if ptype == 5:
            kings[color] = sq
        else:
            pieces.append((sq, ptype, color))

    feats = [[], []]
    for perspective in (0, 1):  # 0 white, 1 black
        ksq = kings[perspective] if perspective == 0 else kings[perspective] ^ 56
        for sq, ptype, color in pieces:
            psq = sq if perspective == 0 else sq ^ 56
            side = 0 if color == perspective else 1
            feats[perspective].append(ksq * FEATURES_PER_KSQ + (ptype * 2 + side) * 64 + psq)

    return feats[0], feats[1], int(rec["stm"]), int(rec["score"]), int(rec["result"])


def precompute_features(records, cache_path=None, log_every=250_000):
    """
    Decodes ALL records into dense arrays once (the per-record Python loop is
    the bottleneck; done once, epochs afterwards are pure array slicing):
      stm_feats, opp_feats  int64 [n, MAX_ACTIVE] (-1 = padding)
      scores, results       float32 [n]
    Optionally cached to an .npz next to the dataset.
    """
    if cache_path and os.path.exists(cache_path):
        data = np.load(cache_path)
        print(f"feature cache loaded: {cache_path}")
        return data["stm"], data["opp"], data["scores"], data["results"]

    n = len(records)
    stm_f = np.full((n, MAX_ACTIVE), -1, dtype=np.int64)
    opp_f = np.full((n, MAX_ACTIVE), -1, dtype=np.int64)
    scores = np.zeros(n, dtype=np.float32)
    results = np.zeros(n, dtype=np.float32)

    for i in range(n):
        white, black, stm, score, result = record_to_features(records[i])
        own, other = (white, black) if stm == 0 else (black, white)
        stm_f[i, :len(own)] = own
        opp_f[i, :len(other)] = other
        scores[i] = score
        results[i] = result
        if log_every and (i + 1) % log_every == 0:
            print(f"  decoded {i + 1:,}/{n:,} records", flush=True)

    if cache_path:
        np.savez_compressed(cache_path, stm=stm_f, opp=opp_f, scores=scores, results=results)
        print(f"feature cache saved: {cache_path}")
    return stm_f, opp_f, scores, results


def batches(records, batch_size, rng, sample_limit=None, precomputed=None):
    """
    Yields training batches of padded sparse features:
      stm_feats, opp_feats  int64 [batch, MAX_ACTIVE] (-1 = padding)
      score                 float32 [batch] (cp, side to move)
      result                float32 [batch] (+1/0/-1, side to move)
    Perspectives are ordered (side to move, opponent) as the network expects.
    Pass 'precomputed' (from precompute_features) for fast epochs.
    """
    indices = rng.permutation(len(records) if precomputed is None else len(precomputed[0]))
    if sample_limit:
        indices = indices[:sample_limit]

    for start in range(0, len(indices) - batch_size + 1, batch_size):
        batch = indices[start:start + batch_size]

        if precomputed is not None:
            stm_all, opp_all, scores_all, results_all = precomputed
            yield stm_all[batch], opp_all[batch], scores_all[batch], results_all[batch]
            continue

        stm_f = np.full((batch_size, MAX_ACTIVE), -1, dtype=np.int64)
        opp_f = np.full((batch_size, MAX_ACTIVE), -1, dtype=np.int64)
        scores = np.zeros(batch_size, dtype=np.float32)
        results = np.zeros(batch_size, dtype=np.float32)

        for row, idx in enumerate(batch):
            white, black, stm, score, result = record_to_features(records[idx])
            own, other = (white, black) if stm == 0 else (black, white)
            stm_f[row, :len(own)] = own
            opp_f[row, :len(other)] = other
            scores[row] = score
            results[row] = result

        yield stm_f, opp_f, scores, results
