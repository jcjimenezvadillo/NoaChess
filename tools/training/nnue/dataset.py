# Reads NOADATA1 datasets (written by tools/NoaChess.DataGen) and converts
# records into sparse HalfKAv2_hm feature indices for training.
#
# The binary layouts and the feature schema are contracts shared with the C#
# side (DatasetFormat.cs and NnueFeatureIndex.cs); any change there requires
# a matching change here and a new schema/version id.

import os

import numpy as np

HEADER_SIZE = 64
RECORD_SIZE = 40
MAGIC = b"NOADATA1"
FEATURE_SCHEMA_ID = 2

# HalfKAv2_hm dimensions (must match NnueFeatureIndex.cs).
PS_NB = 11 * 64                          # 704: 5x2 piece planes + shared king plane (10)
KING_BUCKET_COUNT = 32                   # 64 king squares mirrored to 32
INPUT_SIZE = KING_BUCKET_COUNT * PS_NB   # 22,528 per perspective
MAX_ACTIVE = 32                          # all pieces, kings included

# KingBuckets[sq] (0..31), A1 = index 0; files a-d mirror e-h within each rank.
# Mirror of NnueFeatureIndex.BuildKingBuckets (stored here unscaled).
_KING_BUCKETS = [
    28, 29, 30, 31, 31, 30, 29, 28,
    24, 25, 26, 27, 27, 26, 25, 24,
    20, 21, 22, 23, 23, 22, 21, 20,
    16, 17, 18, 19, 19, 18, 17, 16,
    12, 13, 14, 15, 15, 14, 13, 12,
     8,  9, 10, 11, 11, 10,  9,  8,
     4,  5,  6,  7,  7,  6,  5,  4,
     0,  1,  2,  3,  3,  2,  1,  0,
]


def _make_index(perspective, king_sq, ptype, color, sq):
    """Mirror of NnueFeatureIndex.Index (HalfKAv2_hm). Raw squares in."""
    vflip = 0 if perspective == 0 else 56
    orient = 7 if (king_sq & 7) < 4 else 0
    oriented = sq ^ orient ^ vflip
    if ptype == 5:                       # king -> shared plane 10
        plane = 10 * 64
    else:
        enemy = 0 if color == perspective else 1
        plane = (ptype * 2 + enemy) * 64
    return oriented + plane + _KING_BUCKETS[king_sq ^ vflip] * PS_NB

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

    Features are HalfKAv2_hm (mirror of NnueFeatureIndex.Index): kings ARE
    features (shared plane 10); the perspective king's raw square drives the
    horizontal mirror and the bucket. The vertical flip is applied inside
    _make_index, so raw squares are passed through.
    """
    squares = _unpack_squares(rec["occupancy"])
    nibbles = rec["pieces"]

    pieces = []          # (square, piece_type 0..5 incl king, color 0 white / 1 black)
    kings = [None, None]
    for i, sq in enumerate(squares):
        # int() casts break out of numpy uint8 arithmetic (which overflows).
        code = (int(nibbles[i // 2]) >> (4 * (i % 2))) & 0xF
        ptype, color = code % 6, code // 6
        pieces.append((sq, ptype, color))
        if ptype == 5:
            kings[color] = sq

    feats = [[], []]
    for perspective in (0, 1):  # 0 white, 1 black
        ksq = kings[perspective]
        for sq, ptype, color in pieces:
            feats[perspective].append(_make_index(perspective, ksq, ptype, color, sq))

    return feats[0], feats[1], int(rec["stm"]), int(rec["score"]), int(rec["result"])


def precompute_features(records, cache_path=None, log_every=250_000):
    """
    Decodes ALL records into dense arrays once (the per-record Python loop is
    the bottleneck; done once, epochs afterwards are pure array slicing):
      stm_feats, opp_feats  int16 [n, MAX_ACTIVE] (-1 = padding)
      scores, results       float32 [n]
    Optionally cached to an .npz next to the dataset.

    Feature indices span [0, INPUT_SIZE-1] = [0, 22527] and the padding sentinel
    is -1, so int16 holds them exactly at 1/4 the RAM of int64. That 4x is what
    lets the whole combined dataset (all generations) fit in memory without
    subsampling. EmbeddingBag needs Long indices, so model.forward casts per
    batch (cheap: batch*32 values).
    """
    # The cache is valid ONLY if it is at least as new as the .noadata it was
    # derived from. Keying on existence alone silently trains on stale features
    # when a dataset is regenerated under the same name (e.g. a re-run with a
    # different opening book): the old .npz survives and the fresh .noadata is
    # ignored. Compare mtimes and recompute when the source dataset is newer.
    if cache_path and os.path.exists(cache_path):
        source = cache_path[:-len(".features.npz")] if cache_path.endswith(".features.npz") else None
        fresh = (source is None or not os.path.exists(source)
                 or os.path.getmtime(cache_path) >= os.path.getmtime(source))
        if not fresh:
            print(f"feature cache STALE (source .noadata is newer), recomputing: {cache_path}")
        else:
            data = np.load(cache_path)
            print(f"feature cache loaded: {cache_path}")
            # Legacy caches were saved as int64; cast down to int16 (lossless, the
            # values fit in [-1, 22527]). No-op if the cache is already int16.
            return (data["stm"].astype(np.int16, copy=False),
                    data["opp"].astype(np.int16, copy=False),
                    data["scores"], data["results"])

    n = len(records)
    stm_f = np.full((n, MAX_ACTIVE), -1, dtype=np.int16)
    opp_f = np.full((n, MAX_ACTIVE), -1, dtype=np.int16)
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

    When precomputed is given, the arrays are shuffled once per call and sliced
    sequentially. Sequential access is 10-20x faster than random fancy-indexing
    on large numpy arrays, which is the main GPU-starvation bottleneck.
    """
    if precomputed is not None:
        stm_all, opp_all, scores_all, results_all = precomputed
        n = len(stm_all)
        if sample_limit:
            n = min(n, sample_limit)
        idx = rng.permutation(n)
        stm_s    = stm_all[idx]
        opp_s    = opp_all[idx]
        scores_s = scores_all[idx]
        results_s = results_all[idx]
        for start in range(0, n - batch_size + 1, batch_size):
            end = start + batch_size
            yield stm_s[start:end], opp_s[start:end], scores_s[start:end], results_s[start:end]
        return

    indices = rng.permutation(len(records))
    if sample_limit:
        indices = indices[:sample_limit]

    for start in range(0, len(indices) - batch_size + 1, batch_size):
        batch = indices[start:start + batch_size]
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
