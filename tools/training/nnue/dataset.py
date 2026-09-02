# Reads NOADATA1 datasets (written by tools/NoaChess.DataGen) and converts
# records into sparse HalfKAv2_hm feature indices for training.
#
# The binary layouts and the feature schema are contracts shared with the C#
# side (DatasetFormat.cs and NnueFeatureIndex.cs); any change there requires
# a matching change here and a new schema/version id.

import json
import os
import time

import numpy as np

import threats  # threat feature encoder, for the arch 4 shard cache

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


# ---------------------------------------------------------------------------
# v4.1.0 VECTORISED DECODER
#
# record_to_features above is the readable reference and stays the definition of
# correctness. It is also a per-record Python loop running at ~14k records/s,
# which is 6 hours for 300M positions and 10 for 500M - the volume BLOCK 12
# needs. That is not a tuning problem, it is a wall: every change to the data
# mix would cost most of a day before training could even start.
#
# This decodes whole blocks with numpy instead. The bit twiddling is identical;
# only the loop moves from Python into vectorised operations. decode_block is
# asserted equal to record_to_features over random records by the parity test -
# a decoder that is fast and subtly wrong would poison every net trained after
# it, exactly the class of failure that cost gen7.
# ---------------------------------------------------------------------------

_KING_BUCKETS_ARR = np.array(_KING_BUCKETS, dtype=np.int32)


def decode_block(records):
    """
    Vectorised equivalent of record_to_features over a block of records.
    Returns (stm_feats, opp_feats, scores, results) with -1 padding, already
    ordered (side to move, opponent).
    """
    n = len(records)
    if n == 0:
        return (np.full((0, MAX_ACTIVE), -1, np.int16), np.full((0, MAX_ACTIVE), -1, np.int16),
                np.zeros(0, np.float32), np.zeros(0, np.float32))

    occupancy = np.ascontiguousarray(records["occupancy"])
    # Bit i of the occupancy is square i, so little bit order gives squares in
    # ascending order - the same order the nibbles were written in.
    bits = np.unpackbits(occupancy.view(np.uint8).reshape(n, 8), axis=1, bitorder="little")
    row_idx, square = np.nonzero(bits)          # row-major: rows in order, squares ascending
    row_idx = row_idx.astype(np.int64)
    square = square.astype(np.int32)

    counts = bits.sum(axis=1).astype(np.int64)  # pieces per record
    # Ordinal of each piece within its own record: 0,1,2,... restarting per row.
    starts = np.concatenate(([0], np.cumsum(counts)[:-1]))
    ordinal = (np.arange(len(row_idx), dtype=np.int64) - np.repeat(starts, counts)).astype(np.int32)

    # Nibble j of the 16-byte piece array: low nibble for even j, high for odd.
    pieces = np.ascontiguousarray(records["pieces"]).reshape(n, 16)
    packed = pieces[row_idx, ordinal >> 1]
    code = ((packed >> ((ordinal & 1) * 4).astype(np.uint8)) & 0xF).astype(np.int32)
    ptype = code % 6
    color = code // 6

    # King square per (record, colour). Every record has exactly two kings.
    kings = np.zeros((n, 2), dtype=np.int32)
    is_king = ptype == 5
    kings[row_idx[is_king], color[is_king]] = square[is_king]

    out = []
    for perspective in (0, 1):
        vflip = 0 if perspective == 0 else 56
        king_sq = kings[row_idx, perspective]
        orient = np.where((king_sq & 7) < 4, 7, 0).astype(np.int32)
        oriented = square ^ orient ^ vflip
        enemy = (color != perspective).astype(np.int32)
        plane = np.where(is_king, 10 * 64, (ptype * 2 + enemy) * 64)
        index = oriented + plane + _KING_BUCKETS_ARR[king_sq ^ vflip] * PS_NB

        feats = np.full((n, MAX_ACTIVE), -1, dtype=np.int16)
        feats[row_idx, ordinal] = index.astype(np.int16)
        out.append(feats)

    white_f, black_f = out
    stm = np.ascontiguousarray(records["stm"]).astype(np.int32)
    white_to_move = (stm == 0)[:, None]
    stm_f = np.where(white_to_move, white_f, black_f)
    opp_f = np.where(white_to_move, black_f, white_f)

    scores = np.ascontiguousarray(records["score"]).astype(np.float32)
    results = np.ascontiguousarray(records["result"]).astype(np.float32)
    return stm_f, opp_f, scores, results


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

    # Same vectorised decoder as the streaming path (v4.1.0).
    block = 1_000_000
    for begin in range(0, n, block):
        end = min(begin + block, n)
        stm_f[begin:end], opp_f[begin:end], scores[begin:end], results[begin:end] = \
            decode_block(np.array(records[begin:end]))
        if log_every:
            print(f"  decoded {end:,}/{n:,} records", flush=True)

    if cache_path:
        np.savez_compressed(cache_path, stm=stm_f, opp=opp_f, scores=scores, results=results)
        print(f"feature cache saved: {cache_path}")
    return stm_f, opp_f, scores, results


# ---------------------------------------------------------------------------
# v4.0.0 STREAMING PATH
#
# WHY. precompute_features above builds dense arrays in RAM: 32 int16 per
# perspective, plus score and result, is ~136 bytes per record. train_nnue.py
# therefore carried a --max-records safety cap of 120M, which is ~16 GB - and
# that cap is an ARCHITECTURAL CEILING, not a tuning knob. BLOCK 12 targets
# 300-500M positions on the way to a billion; at 136 bytes each that is 40-136
# GB and simply cannot be an in-RAM array on this machine.
#
# HOW. Features are decoded ONCE into memory-mappable .npy shards, and training
# streams batches straight off the mapping. Nothing but the shuffle buffer is
# ever resident, so dataset size is bounded by disk instead of by RAM.
#
# SHUFFLING. A perfect global shuffle would mean random single-record reads
# across a file far larger than RAM, which is pathological on any disk. Instead
# the index space is cut into contiguous chunks, the CHUNK ORDER is shuffled,
# and several chunks are read into a buffer that is then shuffled internally.
# That mixes well across the whole file while keeping reads sequential. The
# NOADATA format stores records game by game, so a chunk is a run of related
# positions - which is exactly why the in-buffer shuffle matters and why the
# buffer holds many chunks rather than one.
# ---------------------------------------------------------------------------

FEATURE_SHARD_SUFFIX = ".features"
_SHARD_ARRAYS = ("stm", "opp", "scores", "results")


def shard_dir_for(path):
    """Directory holding the memory-mapped feature shards of a .noadata file."""
    return path + FEATURE_SHARD_SUFFIX


def _shard_paths(directory):
    return {name: os.path.join(directory, name + ".npy") for name in _SHARD_ARRAYS}


def build_feature_shards(path, log_every=250_000, force=False):
    """
    Decodes a .noadata into memory-mappable .npy shards and returns their
    directory. Re-decodes when the shards are missing, incomplete, or OLDER
    than the source dataset.

    The mtime check is not optional bookkeeping. Keying a feature cache on mere
    existence is what silently trained gen7 on stale random-opening features
    after the dataset had been regenerated under the same name - the run looked
    healthy and measured the wrong net. Provenance has to be checked, never
    assumed.
    """
    directory = shard_dir_for(path)
    paths = _shard_paths(directory)
    meta_path = os.path.join(directory, "meta.json")

    if not force and os.path.exists(meta_path) and all(os.path.exists(p) for p in paths.values()):
        source_mtime = os.path.getmtime(path)
        if min(os.path.getmtime(p) for p in paths.values()) >= source_mtime:
            with open(meta_path) as f:
                meta = json.load(f)
            print(f"feature shards reused: {directory} ({meta['count']:,} records)")
            return directory
        print(f"feature shards STALE (source .noadata is newer), rebuilding: {directory}")

    records = load_records(path)
    n = len(records)
    os.makedirs(directory, exist_ok=True)

    stm_f = np.lib.format.open_memmap(paths["stm"], mode="w+", dtype=np.int16, shape=(n, MAX_ACTIVE))
    opp_f = np.lib.format.open_memmap(paths["opp"], mode="w+", dtype=np.int16, shape=(n, MAX_ACTIVE))
    scores = np.lib.format.open_memmap(paths["scores"], mode="w+", dtype=np.float32, shape=(n,))
    results = np.lib.format.open_memmap(paths["results"], mode="w+", dtype=np.float32, shape=(n,))

    # Block-decoded with numpy (v4.1.0): ~170k records/s against ~14k for the
    # per-record Python loop, i.e. 30 minutes for 300M positions instead of 6
    # hours. Blocks are bounded so peak memory stays flat regardless of dataset
    # size - the whole point of the streaming path.
    block = 1_000_000
    print(f"decoding {n:,} records -> {directory}", flush=True)
    start_time = time.time()
    for begin in range(0, n, block):
        end = min(begin + block, n)
        chunk_stm, chunk_opp, chunk_scores, chunk_results = decode_block(
            np.array(records[begin:end]))
        stm_f[begin:end] = chunk_stm
        opp_f[begin:end] = chunk_opp
        scores[begin:end] = chunk_scores
        results[begin:end] = chunk_results
        elapsed = time.time() - start_time
        rate = end / elapsed if elapsed > 0 else 0
        eta = (n - end) / rate / 60 if rate > 0 else 0
        print(f"  decoded {end:,}/{n:,} records "
              f"({rate:,.0f} rec/s, ETA {eta:.1f} min)", flush=True)

    for array in (stm_f, opp_f, scores, results):
        array.flush()

    # The meta file is written LAST and is what marks the shard set complete: an
    # interrupted decode leaves no meta, so the next run rebuilds instead of
    # training on a half-written mapping.
    with open(meta_path, "w") as f:
        json.dump({"count": int(n), "source": os.path.basename(path),
                   "source_mtime": os.path.getmtime(path),
                   "max_active": MAX_ACTIVE}, f, indent=2)

    print(f"feature shards written: {directory}")
    return directory


# ---- threat feature shards (arch 4) ----------------------------------------
#
# A SEPARATE cache, deliberately, and not extra columns in the HalfKA shards.
# The 324M-position corpus already has its .features directories built; adding
# threat columns to them would invalidate every one and force a full re-decode
# for runs that do not want threats at all. This way a threat run pays for the
# threat cache once and a HalfKA run never pays for it.
#
# Same staleness rule as the HalfKA shards, and for the same reason: keying a
# feature cache on mere existence is what silently trained gen7 on stale
# features after its dataset was regenerated under the same name. The run looked
# healthy and measured the wrong net.

THREAT_SHARD_SUFFIX = ".threats"
_THREAT_ARRAYS = ("stm", "opp")

# Real features plus the three virtual rows each one fires. Sized from the
# encoder rather than guessed, so a change there cannot silently truncate here.
THREAT_COLUMNS = threats.MAX_ACTIVE_THREATS * 4


def threat_dir_for(path):
    return path + THREAT_SHARD_SUFFIX


# Variable-length threat storage, written alongside the fixed-width one so the
# two can be compared before either is trusted.
#
# WHY IT HAS TO EXIST. The fixed-width cache reserves MAX_ACTIVE_THREATS = 128
# active threats per position because that is the schema's bound. Measured over
# 30,032 perspectives of the real corpus, the mean is 18.3 and the largest seen
# is 61: it reserves 512 int32 columns to store about 73. Over the 65 shards
# that is 1.33 TB against 0.64 TB of free SSD, so the encode dies of a full disk
# after hours. The same data in CSR form is 0.19 TB.
#
# WHY ONE PASS AND NOT TWO. Measuring the lengths first and allocating after
# would be simpler and costs a second encode of the whole corpus - and the
# encode is the expensive half, 5.4 hours becoming 10.8. So rows are appended to
# an open file in chunks while the offsets accumulate in memory (n+1 int64 is
# 40 MB per shard) and the file is memory-mapped for reading afterwards.
# `tofile` writes no .npy header, so the reader uses np.memmap, not np.load.
#
# AND IT REMOVES A FAILURE MODE. The fixed-width writer TRUNCATES any row past
# 512 columns and only reports a counter, so the positions with the most threats
# - the ones that matter most - would silently train on partial features. A
# variable-length row cannot be truncated.
_CSR_ARRAYS = ("stm_values", "stm_offsets", "opp_values", "opp_offsets")


def build_threat_shards_csr(path, chunk=200_000, limit=None, force=False):
    """Encodes a .noadata into variable-length threat shards. Returns the dir.

    'limit' caps the record count, for the comparison harness only: a full shard
    is 5M positions and the gate does not need them to prove the two encodings
    agree.
    """
    directory = threat_dir_for(path) + ".csr"
    os.makedirs(directory, exist_ok=True)
    paths = {n: os.path.join(directory, n + ".bin") for n in _CSR_ARRAYS}
    meta_path = os.path.join(directory, "meta.json")

    # Same validity rule as the fixed-width cache, and for the same reason:
    # keying a feature cache on mere existence is what silently trained gen7 on
    # stale features. Existence, then mtime against the source, then the column
    # contract - and 'format', so a leftover fixed-width cache can never be read
    # as this one.
    if not force and os.path.exists(meta_path) and all(os.path.exists(p) for p in paths.values()):
        if min(os.path.getmtime(p) for p in paths.values()) >= os.path.getmtime(path):
            with open(meta_path) as f:
                meta = json.load(f)
            if meta.get("format") == "csr" and meta.get("columns") == THREAT_COLUMNS:
                print(f"threat shards (csr) reused: {directory} ({meta['count']:,} records)")
                return directory
            print(f"threat shards (csr) mismatch: format {meta.get('format')}, "
                  f"columns {meta.get('columns')}; rebuilding: {directory}")
        else:
            print(f"threat shards (csr) STALE (source is newer), rebuilding: {directory}")

    records = load_records(path)
    n = len(records) if limit is None else min(limit, len(records))

    offsets = {"stm": np.zeros(n + 1, dtype=np.int64),
               "opp": np.zeros(n + 1, dtype=np.int64)}
    start_time = time.time()

    with open(paths["stm_values"], "wb") as fs, open(paths["opp_values"], "wb") as fo:
        buffers = {"stm": [], "opp": []}
        for i in range(n):
            rec = records[i]
            white, black = threats.active_threats(int(rec["occupancy"]), rec["pieces"])
            a, b = (white, black) if int(rec["stm"]) == 0 else (black, white)
            a, b = threats.factorize(a), threats.factorize(b)

            buffers["stm"].append(np.asarray(a, dtype=np.int32))
            buffers["opp"].append(np.asarray(b, dtype=np.int32))
            offsets["stm"][i + 1] = offsets["stm"][i] + len(a)
            offsets["opp"][i + 1] = offsets["opp"][i] + len(b)

            if (i + 1) % chunk == 0 or i + 1 == n:
                np.concatenate(buffers["stm"]).tofile(fs)
                np.concatenate(buffers["opp"]).tofile(fo)
                buffers = {"stm": [], "opp": []}
                elapsed = max(time.time() - start_time, 1e-9)
                rate = (i + 1) / elapsed
                print(f"  csr {i + 1:,}/{n:,} ({rate:,.0f} rec/s, "
                      f"ETA {(n - i - 1) / rate / 60:.1f} min)", flush=True)

    offsets["stm"].tofile(paths["stm_offsets"])
    offsets["opp"].tofile(paths["opp_offsets"])

    # Written last, exactly as the fixed-width path does, so an interrupted
    # encode leaves no meta and the next run rebuilds instead of reading a
    # half-written mapping.
    with open(meta_path, "w") as f:
        json.dump({"count": int(n), "format": "csr",
                   "source": os.path.basename(path),
                   "source_mtime": os.path.getmtime(path),
                   "columns": THREAT_COLUMNS,
                   "threat_input_size": threats.THREAT_INPUT_SIZE,
                   "factored_input_size": threats.FACTORED_INPUT_SIZE}, f, indent=2)
    return directory


def expand_csr(values, offsets, begin, end, columns=None):
    """Rebuilds rows [begin, end) as a fixed-width block padded with -1.

    THE SAVING IS ON DISK, NOT IN RAM, and that is what makes this safe to drop
    in. The cache shrinks 7x because rows are stored at their real length, but
    the block handed to the training loop is the same fixed-width int32 array
    the fixed-width cache produced, so everything downstream - the shuffling,
    the concatenation, the fancy indexing, the model - sees byte-identical
    input. A batch of 16384 at 512 columns is 67 MB; the disk was the problem,
    never the batch.
    """
    columns = THREAT_COLUMNS if columns is None else columns
    rows = end - begin
    out = np.full((rows, columns), -1, dtype=np.int32)
    base = int(offsets[begin])
    # One read of the whole span rather than one per row: the span is
    # contiguous by construction, and paging it in row by row is what made the
    # HalfKA path slow enough to need np.asarray in the first place.
    span = np.asarray(values[base:int(offsets[end])])
    starts = np.asarray(offsets[begin:end + 1], dtype=np.int64) - base

    # Checked, not assumed. Forgetting to subtract the base reads past the span
    # and numpy happens to raise a broadcast error - but only when the span runs
    # short. With a longer span the same bug fills rows with the WRONG values
    # and says nothing, which is the failure this whole cache must not have. One
    # comparison turns luck into a guarantee.
    if starts[0] != 0 or starts[-1] != len(span) or np.any(np.diff(starts) < 0):
        raise ValueError(
            f"expand_csr: offsets incoherentes para [{begin},{end}) - "
            f"empiezan en {starts[0]}, acaban en {starts[-1]}, tramo de {len(span)}")
    for i in range(rows):
        a, b = int(starts[i]), int(starts[i + 1])
        n = min(b - a, columns)
        if n > 0:
            out[i, :n] = span[a:a + n]
    return out


class _CsrView:
    """Makes a CSR pair behave like the 2D array the consumer already expects.

    The streaming loop slices these caches by row range and hands the result
    straight on. Rather than teach that loop about two storage formats - the
    exact drift that puts a stream in one place and forgets it in another - the
    format is hidden behind the one operation the loop performs. len() and
    [begin:end] are all it ever asks for, and both mean here what they meant
    before, so nothing downstream changes or needs to know.
    """

    __slots__ = ("_values", "_offsets", "_columns")

    def __init__(self, values, offsets, columns=None):
        self._values = values
        self._offsets = offsets
        self._columns = columns

    def __len__(self):
        return len(self._offsets) - 1

    def __getitem__(self, key):
        if not isinstance(key, slice):
            raise TypeError("los caches de amenazas solo se cortan por rangos")
        begin, end, step = key.indices(len(self))
        if step != 1:
            raise ValueError("los caches de amenazas no admiten paso distinto de 1")
        return expand_csr(self._values, self._offsets, begin, end,
                          columns=self._columns)


# ---- Coarse-threat companions (gate 2b of the coarse-threats design) ----
#
# The C# encoder (DataGen coarse-encode) writes one .coarsedata per shard:
# NOACRS1 magic + u64 count, then per record u8 n + n x u8 ABSOLUTE pair ids
# (attacker piece code * 12 + victim code, colours absolute). That file IS
# the CSR cache - 19 bytes per record against the 1.24 TB the fine cache
# once asked for - so the build step below only parses it once into npz
# form and adds the one thing the feature shards do not keep: each record's
# side to move (byte 24 of the .noadata record), which the perspective flip
# needs at batch time.

COARSE_DIR = os.environ.get("NOA_COARSE_DIR", r"C:\NoaData\coarse")
COARSE_COLUMNS = 96


def coarse_companion_for(path):
    parent = os.path.basename(os.path.dirname(os.path.abspath(path)))
    stem = os.path.splitext(os.path.basename(path))[0]
    return os.path.join(COARSE_DIR, parent, stem + ".coarsedata")


def build_coarse_shards(path, force=False):
    comp = coarse_companion_for(path)
    if not os.path.exists(comp):
        raise SystemExit(f"{path}: missing coarse companion {comp} - "
                         f"run Noa-CoarseEncodeAll.ps1 first")
    cache = comp + ".npz"
    if (not force and os.path.exists(cache)
            and os.path.getmtime(cache) > os.path.getmtime(comp)):
        return cache

    raw = np.fromfile(comp, dtype=np.uint8)
    if raw[:8].tobytes() != b"NOACRS1\0":
        raise SystemExit(f"{comp}: unknown header")
    count = int(np.frombuffer(raw[8:16].tobytes(), dtype="<u8")[0])

    counts = np.empty(count, dtype=np.int64)
    values = np.empty(len(raw), dtype=np.int16)
    pos = 16
    vpos = 0
    for i in range(count):
        n = int(raw[pos])
        pos += 1
        counts[i] = n
        values[vpos:vpos + n] = raw[pos:pos + n]
        pos += n
        vpos += n
    if pos != len(raw):
        raise SystemExit(f"{comp}: {len(raw) - pos} bytes left over after "
                         f"{count} records - corrupt file")
    if counts.max(initial=0) > COARSE_COLUMNS:
        raise SystemExit(f"{comp}: a record carries {int(counts.max())} relations "
                         f"and the fixed window is {COARSE_COLUMNS}; raise "
                         f"COARSE_COLUMNS rather than truncate silently")
    offsets = np.concatenate(([0], np.cumsum(counts))).astype(np.int64)

    rec = np.fromfile(path, dtype=np.uint8)
    stm = rec[64:64 + count * 40].reshape(count, 40)[:, 24].copy()

    tmp = cache + ".tmp.npz"
    np.savez(tmp, values=values[:vpos], offsets=offsets, stm=stm)
    os.replace(tmp, cache)
    return cache


def load_coarse_csr(cache):
    data = np.load(cache, mmap_mode="r")
    return data["values"], data["offsets"], data["stm"]


def coarse_perspectives(abs_ids, stm):
    """Per-perspective bucket views from ABSOLUTE pair ids, vectorized.

    White's view is the absolute id itself; black's flips both colour bits:
    ((att+6)%12)*12 + (vic+6)%12. Padding (-1) survives both views - the
    flip of a pad would be garbage, so it is masked back explicitly.
    """
    att = abs_ids // 12
    vic = abs_ids % 12
    flip = ((att + 6) % 12) * 12 + (vic + 6) % 12
    black = np.where(abs_ids < 0, -1, flip)
    is_black = (stm != 0)[:, None]
    stm_view = np.where(is_black, black, abs_ids)
    opp_view = np.where(is_black, abs_ids, black)
    return stm_view, opp_view


def load_threat_csr(directory):
    """Memory-maps a CSR threat shard. Returns (values, offsets) per perspective."""
    out = {}
    for side in ("stm", "opp"):
        values = np.memmap(os.path.join(directory, f"{side}_values.bin"),
                           dtype=np.int32, mode="r")
        offsets = np.memmap(os.path.join(directory, f"{side}_offsets.bin"),
                            dtype=np.int64, mode="r")
        out[side] = (values, offsets)
    return out


def build_threat_shards(path, force=False, fixed_width=False):
    """Decodes a .noadata into memory-mappable threat feature shards.

    MEASURED 2026-08-16 and the old figure here was wrong: 4,313 records per
    second, which is 20.9 hours for the 325M corpus, not the 5.4 this used to
    claim. Taken while a training run had the machine, so it is an upper bound
    and an idle box will do better - but not four times better. Paid once per
    corpus and only by runs that ask for threats.
    """
    # DESVIADO AL FORMATO DE LONGITUD VARIABLE, y este es el sitio.
    #
    # La primera version de este desvio acabo dentro de build_feature_shards
    # -la de HalfKA- porque el patron que se busco para insertarlo existe en
    # LAS DOS funciones y la sustitucion cogio la primera. Rompio TODO el
    # entrenamiento con un NameError, no solo el de amenazas, y ni los tres
    # gates del CSR lo vieron: llamaban a build_threat_shards_csr directamente
    # y nunca pasaban por este punto de entrada. Probar el camino nuevo no es
    # probar que el camino viejo lleva a el.
    if not fixed_width:
        return build_threat_shards_csr(path, force=force)
    directory = threat_dir_for(path)
    paths = {n: os.path.join(directory, n + ".npy") for n in _THREAT_ARRAYS}
    meta_path = os.path.join(directory, "meta.json")

    if not force and os.path.exists(meta_path) and all(os.path.exists(p) for p in paths.values()):
        if min(os.path.getmtime(p) for p in paths.values()) >= os.path.getmtime(path):
            with open(meta_path) as f:
                meta = json.load(f)
            # The column count is part of the contract: a cache built when the
            # encoder emitted a different number of virtuals would load without
            # complaint and feed the net truncated rows.
            if meta.get("columns") == THREAT_COLUMNS:
                print(f"threat shards reused: {directory} ({meta['count']:,} records)")
                return directory
            print(f"threat shards have {meta.get('columns')} columns, encoder now emits "
                  f"{THREAT_COLUMNS}; rebuilding: {directory}")
        else:
            print(f"threat shards STALE (source .noadata is newer), rebuilding: {directory}")

    records = load_records(path)
    n = len(records)
    os.makedirs(directory, exist_ok=True)

    stm_t = np.lib.format.open_memmap(paths["stm"], mode="w+", dtype=np.int32,
                                      shape=(n, THREAT_COLUMNS))
    opp_t = np.lib.format.open_memmap(paths["opp"], mode="w+", dtype=np.int32,
                                      shape=(n, THREAT_COLUMNS))
    stm_t[:] = -1
    opp_t[:] = -1

    print(f"encoding threats for {n:,} records -> {directory}", flush=True)
    start_time = time.time()
    overflow = 0
    for i in range(n):
        rec = records[i]
        white, black = threats.active_threats(int(rec["occupancy"]), rec["pieces"])
        a, b = (white, black) if int(rec["stm"]) == 0 else (black, white)
        a, b = threats.factorize(a), threats.factorize(b)
        if len(a) > THREAT_COLUMNS or len(b) > THREAT_COLUMNS:
            overflow += 1
        stm_t[i, :min(len(a), THREAT_COLUMNS)] = a[:THREAT_COLUMNS]
        opp_t[i, :min(len(b), THREAT_COLUMNS)] = b[:THREAT_COLUMNS]

        if (i + 1) % 200_000 == 0:
            elapsed = time.time() - start_time
            rate = (i + 1) / elapsed
            print(f"  {i + 1:,}/{n:,} ({rate:,.0f} rec/s, "
                  f"ETA {(n - i - 1) / rate / 60:.1f} min)", flush=True)

    for array in (stm_t, opp_t):
        array.flush()

    if overflow:
        print(f"  WARNING: {overflow:,} positions exceeded {THREAT_COLUMNS} columns "
              f"and were TRUNCATED - features were dropped")

    # Written last, so an interrupted encode leaves no meta and the next run
    # rebuilds instead of training on a half-written mapping.
    with open(meta_path, "w") as f:
        json.dump({"count": int(n), "source": os.path.basename(path),
                   "source_mtime": os.path.getmtime(path),
                   "columns": THREAT_COLUMNS,
                   "threat_input_size": threats.THREAT_INPUT_SIZE,
                   "factored_input_size": threats.FACTORED_INPUT_SIZE}, f, indent=2)

    print(f"threat shards written: {directory}")
    return directory


class FeatureStore:
    """
    Memory-mapped features for one or more datasets, addressed as one logical
    array. Nothing is loaded until a batch asks for it.

    Train/validation are split per FILE by a tail cut, exactly as the in-RAM
    path did: splitting the concatenation instead would make the validation set
    come only from the last file, and a tail cut keeps whole games on one side
    because the record format is ordered by game.
    """

    def __init__(self, paths, val_fraction=0.05, threats=False, coarse=False):
        # Threat shards are OPTIONAL and live in their own directory, so a run
        # without them never touches - or builds - that cache.
        self.threats = bool(threats)
        self.coarse = bool(coarse)
        self.files = []
        for path in paths:
            directory = build_feature_shards(path)
            shards = _shard_paths(directory)
            stm = np.load(shards["stm"], mmap_mode="r")
            opp = np.load(shards["opp"], mmap_mode="r")
            scores = np.load(shards["scores"], mmap_mode="r")
            results = np.load(shards["results"], mmap_mode="r")
            count = len(stm)
            if count == 0:
                # A shard whose header still says zero records was never
                # finalized - the datagen was interrupted while writing it. It
                # reads as empty rather than corrupt, so it would silently
                # contribute nothing; say so instead of letting it look fine.
                print(f"WARNING: {path} holds 0 records (interrupted shard, never "
                      f"finalized). It contributes nothing - re-run the datagen "
                      f"with --resume, or drop the file.")
                continue
            train_count = count - int(count * val_fraction)
            entry = {
                "path": path, "stm": stm, "opp": opp,
                "scores": scores, "results": results,
                "count": count, "train_count": train_count,
            }
            if self.threats:
                tdir = build_threat_shards(path)
                csr = load_threat_csr(tdir)
                entry["stm_t"] = _CsrView(*csr["stm"])
                entry["opp_t"] = _CsrView(*csr["opp"])
                # A threat cache built from a different slice of the same corpus
                # would line up row for row with the wrong positions and train
                # on labels that belong to other boards. Cheap to check, and
                # impossible to notice afterwards.
                if len(entry["stm_t"]) != count:
                    raise SystemExit(
                        f"{path}: threat shards hold {len(entry['stm_t']):,} rows but the "
                        f"HalfKA shards hold {count:,}. They describe different data.")
            if self.coarse:
                cache = build_coarse_shards(path)
                values, offsets, stm_side = load_coarse_csr(cache)
                entry["coarse"] = _CsrView(values, offsets, columns=COARSE_COLUMNS)
                entry["coarse_stm"] = stm_side
                # Same alignment guarantee the threat cache demands: a coarse
                # companion built from another slice would pair the wrong
                # relations with every position and never say so.
                if len(entry["coarse"]) != count:
                    raise SystemExit(
                        f"{path}: coarse companion holds {len(entry['coarse']):,} rows "
                        f"but the HalfKA shards hold {count:,}. They describe "
                        f"different data.")
            self.files.append(entry)
            print(f"dataset: {count:,} records from {path} "
                  f"(train {train_count:,} / val {count - train_count:,})")

    @property
    def train_total(self):
        return sum(f["train_count"] for f in self.files)

    @property
    def val_total(self):
        return sum(f["count"] - f["train_count"] for f in self.files)

    def _spans(self, split):
        """(file, start, stop) ranges for the requested split."""
        for f in self.files:
            if split == "train":
                yield f, 0, f["train_count"]
            else:
                yield f, f["train_count"], f["count"]

    def stream_batches(self, batch_size, rng, split="train",
                       chunk=8192, buffer_chunks=64):
        """
        Yields (stm, opp, scores, results) batches, plus (stm_t, opp_t) when
        the store was opened with threats. Chunk order is shuffled
        globally across every file, then each buffer of chunks is shuffled
        internally before being cut into batches.
        """
        chunks = []
        for file_index, (f, start, stop) in enumerate(self._spans(split)):
            for begin in range(start, stop, chunk):
                chunks.append((file_index, begin, min(begin + chunk, stop)))
        if not chunks:
            return

        order = rng.permutation(len(chunks))

        # Six streams with threats, four without. Built from a key list rather
        # than hard-coded tuples so the two paths cannot drift: adding a stream
        # in one place and forgetting the other is how a buffer ends up shuffled
        # with a permutation of the wrong length.
        keys = ["stm", "opp", "scores", "results"]
        if self.threats:
            keys += ["stm_t", "opp_t"]
        if self.coarse:
            keys += ["coarse", "coarse_stm"]

        pending = tuple([] for _ in keys)
        pending_rows = 0
        # Rows left over when a buffer does not divide evenly into batches. They
        # are CARRIED into the next buffer rather than dropped: discarding a
        # partial batch per buffer would quietly throw away real training data
        # on every epoch, and the smaller the buffer the worse it gets (measured
        # at 0.29% loss with a deliberately tiny buffer). Only the final tail of
        # the whole pass is dropped, exactly like the in-RAM path.
        carry = None

        for position, chunk_index in enumerate(order):
            file_index, begin, end = chunks[chunk_index]
            f = self.files[file_index]
            # np.asarray forces the mapped slice into real memory once, so the
            # later fancy-indexing does not fault page by page.
            for slot, key in enumerate(keys):
                pending[slot].append(np.asarray(f[key][begin:end]))
            pending_rows += end - begin

            is_last = position == len(order) - 1
            if len(pending[0]) < buffer_chunks and not is_last:
                continue

            arrays = [np.concatenate(part) for part in pending]
            perm = rng.permutation(pending_rows)
            arrays = [a[perm] for a in arrays]
            if carry is not None:
                # Prepend before batching, after the shuffle, so carried rows do
                # not all land together at the head of one batch.
                arrays = [np.concatenate([c, a]) for c, a in zip(carry, arrays)]
                mixed = rng.permutation(len(arrays[0]))
                arrays = [a[mixed] for a in arrays]
                carry = None

            rows = len(arrays[0])
            start = 0
            while start + batch_size <= rows:
                stop = start + batch_size
                yield tuple(a[start:stop] for a in arrays)
                start = stop
            if start < rows and not is_last:
                carry = [a[start:] for a in arrays]

            pending = tuple([] for _ in keys)
            pending_rows = 0


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
