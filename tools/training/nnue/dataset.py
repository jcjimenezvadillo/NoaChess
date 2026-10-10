# Reads NOADATA1 datasets (written by tools/NoaChess.DataGen) and converts
# records into sparse HalfKAv2_hm feature indices for training.
#
# The binary layouts and the feature schema are contracts shared with the C#
# side (DatasetFormat.cs and NnueFeatureIndex.cs); any change there requires
# a matching change here and a new schema/version id.

import contextlib
import hashlib
import io
import json
import os
import sys
import time
import zipfile

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


def expand_csr(values, offsets, begin, end, columns=None, rows=None):
    """Rebuilds rows [begin, end) as a fixed-width block padded with -1.

    THE SAVING IS ON DISK, NOT IN RAM, and that is what makes this safe to drop
    in. The cache shrinks 7x because rows are stored at their real length, but
    the block handed to the training loop is the same fixed-width int32 array
    the fixed-width cache produced, so everything downstream - the shuffling,
    the concatenation, the fancy indexing, the model - sees byte-identical
    input. A batch of 16384 at 512 columns is 67 MB; the disk was the problem,
    never the batch.

    'rows' (2026-10-01, hash validation split): sorted indices RELATIVE to
    begin; only those rows are rebuilt, in that order. The validation pass of
    the hash split reads every chunk and keeps about 5% of it, and expanding the
    other 95% only to throw them away was most of what that pass cost: measured
    2.72 s against 0.37 s over 2M rows of a real coarse companion. The span is
    still read and checked whole, so the selection cannot hide an incoherent
    offset table.
    """
    columns = THREAT_COLUMNS if columns is None else columns
    out_rows = end - begin if rows is None else len(rows)
    out = np.full((out_rows, columns), -1, dtype=np.int32)
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
    # The offsets of a short mapping come back short instead of failing, and a
    # row selection must not be applied to the wrong number of rows.
    if rows is not None and len(starts) != end - begin + 1:
        raise ValueError(
            f"expand_csr: {len(starts) - 1} filas de offsets para [{begin},{end})")
    # Vectorized scatter (2026-09-03): the per-row Python loop this replaced
    # cost 14.8 ms per 8192-row chunk on the coarse companions, the producer
    # thread's largest remaining item once the perspective flip moved to the
    # device. Same rows, same truncation at 'columns', same padding; proved
    # identical on real chunks, a shard tail and synthetic edge cases.
    lengths = np.minimum(np.diff(starts), columns)
    row_starts = starts[:-1]
    if rows is not None:
        lengths = lengths[rows]
        row_starts = row_starts[rows]
    total = int(lengths.sum())
    if total == 0:
        return out
    row_idx = np.repeat(np.arange(out_rows), lengths)
    first = np.cumsum(lengths) - lengths
    col_idx = np.arange(total) - np.repeat(first, lengths)
    src_idx = np.repeat(row_starts, lengths) + col_idx
    out[row_idx, col_idx] = span[src_idx]
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

    def take(self, begin, end, rows):
        """Rows 'rows' (sorted indices relative to begin) of [begin, end).

        The one other operation the streaming loop needs, for the hash split:
        equal to self[begin:end][rows], without expanding the rows it drops.
        """
        rows = np.asarray(rows, dtype=np.int64)
        if (begin < 0 or end > len(self) or begin > end
                or (len(rows) and (rows[0] < 0 or rows[-1] >= end - begin
                                   or np.any(np.diff(rows) <= 0)))):
            raise ValueError(f"take: rows outside [{begin},{end}) or unsorted, "
                             f"on a cache of {len(self)} rows")
        return expand_csr(self._values, self._offsets, begin, end,
                          columns=self._columns, rows=rows)


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


def _npz_member_memmap(path, name):
    """Memory-maps member 'name' of an UNCOMPRESSED .npz, or returns None.

    np.load ignores mmap_mode for an .npz and reads every member whole into
    RAM. For the coarse companions that is about 46 bytes per record held for
    the whole run: 51.8 GB over the 263 caches of the full corpus
    (2026-10-01), on a machine with 64. np.savez stores its members
    uncompressed, so each one is a plain .npy file at some offset inside the
    zip, and np.memmap can map it there; the page cache then keeps what fits.
    Measured on 2M rows of a real shard, one core: the train pass costs the
    same either way once the pages are warm (2.07-2.22 s against 2.15 s per 1M
    rows). Anything that is not exactly a stored .npy - a compressed member, a
    header version this does not parse, sizes that do not add up - returns
    None and the caller falls back to the in-RAM read.
    """
    with zipfile.ZipFile(path) as z:
        info = z.getinfo(name)
    if info.compress_type != zipfile.ZIP_STORED or info.file_size != info.compress_size:
        return None
    with open(path, "rb") as f:
        f.seek(info.header_offset)
        local = f.read(30)
        if len(local) != 30 or local[:4] != b"PK\x03\x04":
            return None
        # The local header's own name and extra lengths, not the central
        # directory's: numpy writes a zip64 extra field into the local header.
        start = (info.header_offset + 30 + int.from_bytes(local[26:28], "little")
                 + int.from_bytes(local[28:30], "little"))
        f.seek(start)
        version = np.lib.format.read_magic(f)
        if version == (1, 0):
            shape, fortran, dtype = np.lib.format.read_array_header_1_0(f)
        elif version == (2, 0):
            shape, fortran, dtype = np.lib.format.read_array_header_2_0(f)
        else:
            return None
        data_start = f.tell()
    nbytes = int(np.prod(shape, dtype=np.int64)) * dtype.itemsize
    if dtype.hasobject or data_start - start + nbytes != info.file_size:
        return None
    if nbytes == 0:
        return np.zeros(shape, dtype=dtype)      # np.memmap refuses an empty map
    return np.memmap(path, dtype=dtype, mode="r", offset=data_start, shape=shape,
                     order="F" if fortran else "C")


def load_coarse_csr(cache):
    out = []
    data = None
    for name in ("values", "offsets", "stm"):
        array = _npz_member_memmap(cache, name + ".npy")
        if array is None:
            if data is None:
                data = np.load(cache)
            array = data[name]
        out.append(array)
    values, offsets, stm = out
    # The two ends of the offset table and the row counts, checked once per
    # file: a mapping at the wrong offset would read as plausible numbers.
    if (len(offsets) != len(stm) + 1 or (len(offsets) and int(offsets[0]) != 0)
            or (len(offsets) and int(offsets[-1]) != len(values))):
        raise SystemExit(f"{cache}: offsets [{int(offsets[0]) if len(offsets) else '-'}.."
                         f"{int(offsets[-1]) if len(offsets) else '-'}] over {len(values):,} "
                         f"values and {len(stm):,} rows do not describe one CSR table")
    return values, offsets, stm


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


# ---- Position-hash validation split (2026-10-01) ---------------------------
#
# WHY. The tail cut keeps whole games on one side, but not whole POSITIONS. The
# same position recurs across files and corpora - opening lines, repeated
# self-play, common endgames - so a row of one file's validation tail is often
# a training row of another file. Measured 2026-09-23: 5.48% of one shard's
# validation tail appeared verbatim among the training rows of other files.
# The trainer keeps the checkpoint with the best validation loss, and a
# validation set that partly repeats the training set rewards memorisation:
# the choice drifts toward overfit checkpoints.
#
# HOW. A row goes to validation iff a hash of its position falls below
# val_fraction. The hash is a function of the position alone, never of the
# file or the row number, so a position lands on the same side wherever it
# occurs. One mask per file, packed 8 rows to the byte, cached next to the
# feature shards and computed once.
#
# WHAT "THE POSITION" IS. The pair of HalfKA rows the net reads (stm, opp),
# each taken as a SET: one random 64-bit value per (perspective, feature),
# summed with wrapping arithmetic, then an avalanche mix. Order-free on
# purpose. The decoder emits features in raw-square order, so a mirrored board
# or a colour-flipped one (colours swapped, ranks flipped, the other side to
# move) gives the very same features in another order - the same input to the
# net, and the same leak. Measured on datascale5 (the validation tail of one
# shard against the training rows of three others): identical rows 2.47%,
# identical feature sets 3.63%. Hashing the rows in order would have left a
# third of the leak in place. The perspectives draw from separate tables, so
# the same board with the other side to move is a different position, and the
# pad (-1) adds nothing, so the identity does not depend on the row width.
#
# WHAT IT COSTS, found in review the same day. Records are stored game by game
# (about 79 per game in datascale5) and the hash sends ~5% of every game's
# positions to validation and the rest to training. Measured on 1M rows of
# datascale5/bulk.0000: 99.99% of the hash split's validation rows have
# training rows from their own game, 99.6% one row away - 81% of the stm
# features in common and the same game result, which the target weights by
# 1 - lambda - against 0.04% for the tail cut. A net that memorises games
# scores well on that validation set, the bias the split was meant to remove.
# Its validation pass also reads every chunk of the corpus to keep 5% of it.
# "tail-dedup" below closes the cross-file leak without either cost; this split
# stays for comparison.

VAL_SPLITS = ("tail", "hash", "tail-dedup")
VAL_HASH_VERSION = 1   # part of the cache name and meta: a new hash rebuilds
# Rows per hashing block, a multiple of 8 for packing. Measured on one core over
# real rows: 16K 2.7M rows/s, 64K 1.9M - the per-column temporaries stop
# fitting in cache.
_VAL_MASK_BLOCK = 16384
_val_hash_tables = None


def _mix64(z):
    """splitmix64 finalizer over a uint64 array (wrapping arithmetic)."""
    z = z ^ (z >> np.uint64(30))
    z = z * np.uint64(0xBF58476D1CE4E5B9)
    z = z ^ (z >> np.uint64(27))
    z = z * np.uint64(0x94D049BB133111EB)
    return z ^ (z >> np.uint64(31))


def _position_hash_tables():
    """(stm table, opp table): entry f+1 is feature f's value, entry 0 the pad."""
    global _val_hash_tables
    if _val_hash_tables is None:
        keys = np.arange(INPUT_SIZE, dtype=np.uint64) * np.uint64(2)
        tables = []
        for side in (0, 1):
            table = np.zeros(INPUT_SIZE + 1, dtype=np.uint64)
            table[1:] = _mix64(keys + np.uint64(0x9E3779B97F4A7C15 + side))
            tables.append(table)
        _val_hash_tables = tuple(tables)
    return _val_hash_tables


def position_hashes(stm, opp):
    """64-bit position identity of every row of two [n, k] feature blocks.

    Column by column rather than one [n, k] gather: same result, a fraction of
    the temporary memory, and measured faster. Sorting the rows and hashing
    them in order would be the other way to be order-free; the sort alone
    measured slower than this whole function.
    """
    t_stm, t_opp = _position_hash_tables()
    h = np.zeros(len(stm), dtype=np.uint64)
    for table, block in ((t_stm, stm), (t_opp, opp)):
        for j in range(block.shape[1]):
            h += table[block[:, j].astype(np.intp) + 1]
    return _mix64(h)


def val_hash_threshold(val_fraction):
    """A row is validation iff its hash < this. Exact: the float times 2^64."""
    return min(max(int(val_fraction * 2.0 ** 64), 0), 2 ** 64 - 1)


def position_val_mask(stm, opp, val_fraction):
    """Boolean [n]: True where the row's position belongs to validation."""
    return position_hashes(stm, opp) < np.uint64(val_hash_threshold(val_fraction))


def position_hashes_range(stm, opp, begin, end):
    """position_hashes of rows [begin, end) of two (memory-mapped) feature
    arrays, read in blocks so a 10M-row shard never needs to be resident."""
    out = np.empty(max(end - begin, 0), dtype=np.uint64)
    for b in range(begin, end, _VAL_MASK_BLOCK):
        e = min(b + _VAL_MASK_BLOCK, end)
        out[b - begin:e - begin] = position_hashes(np.asarray(stm[b:e]), np.asarray(opp[b:e]))
    return out


def packed_val_mask(stm, opp, val_fraction):
    """(packed mask, validation rows) of two [n, k] feature arrays, read in
    blocks so a memory-mapped 10M-row shard never needs to be resident."""
    n = len(stm)
    packed = np.zeros((n + 7) // 8, dtype=np.uint8)
    threshold = np.uint64(val_hash_threshold(val_fraction))
    val_count = 0
    for begin in range(0, n, _VAL_MASK_BLOCK):
        end = min(begin + _VAL_MASK_BLOCK, n)
        is_val = position_hashes(np.asarray(stm[begin:end]),
                                 np.asarray(opp[begin:end])) < threshold
        packed[begin >> 3:(end + 7) >> 3] = np.packbits(is_val, bitorder="little")
        val_count += int(is_val.sum())
    return packed, val_count


def _val_mask_paths(directory, val_fraction):
    stem = os.path.join(directory, f"valmask_h{VAL_HASH_VERSION}_{val_fraction!r}")
    return stem + ".npy", stem + ".json"


def build_val_mask(path, val_fraction, force=False):
    """Returns (packed mask, validation rows) of one file's feature shards.

    The packed mask is uint8, little bit order, row i at bit i % 8 of byte
    i // 8, so a 10M-row file is 1.25 MB and stays in RAM. Cached as
    valmask_h<version>_<fraction>.npy plus a .json meta in the .features
    directory, under the same rules as every other cache of this module, and
    for the same reason - keying a cache on mere existence is what silently
    trained gen7 on stale features: reused only when both files exist, both are
    at least as new as the stm and opp shards they were computed from, the meta
    names this hash and this fraction and the shard's row count, and the mask
    itself counts the validation rows the meta says. Anything else rebuilds.
    """
    val_fraction = float(val_fraction)   # a plain float names the file and the meta
    directory = shard_dir_for(path)
    shards = _shard_paths(directory)
    sources = (shards["stm"], shards["opp"])
    mask_path, meta_path = _val_mask_paths(directory, val_fraction)
    stm = np.load(shards["stm"], mmap_mode="r")
    opp = np.load(shards["opp"], mmap_mode="r")
    n = len(stm)
    if len(opp) != n:
        raise SystemExit(f"{directory}: stm holds {n:,} rows and opp {len(opp):,}")

    if not force and os.path.exists(meta_path) and os.path.exists(mask_path):
        newest_source = max(os.path.getmtime(p) for p in sources)
        if min(os.path.getmtime(mask_path), os.path.getmtime(meta_path)) >= newest_source:
            # A cache that cannot even be read is just another reason to rebuild.
            try:
                with open(meta_path) as f:
                    meta = json.load(f)
                packed = np.load(mask_path)
                valid = (meta.get("hash_version") == VAL_HASH_VERSION
                         and meta.get("val_fraction") == val_fraction
                         and meta.get("count") == n
                         and packed.dtype == np.uint8 and packed.shape == ((n + 7) // 8,)
                         and int(np.unpackbits(packed).sum()) == meta.get("val_count"))
            except Exception as exc:  # noqa: BLE001 - unreadable cache, rebuilt below
                meta, valid = {"error": str(exc)}, False
            if valid:
                print(f"val mask reused: {mask_path}")
                return packed, int(meta["val_count"])
            print(f"val mask mismatch ({meta}, shard holds {n:,} rows), "
                  f"rebuilding: {mask_path}")
        else:
            print(f"val mask STALE (feature shards are newer), rebuilding: {mask_path}")

    # The meta marks the mask complete, so it goes first: an interrupted
    # rebuild must never leave the old meta in front of a new mask.
    if os.path.exists(meta_path):
        os.remove(meta_path)
    start_time = time.time()
    packed, val_count = packed_val_mask(stm, opp, val_fraction)

    tmp = mask_path[:-len(".npy")] + ".tmp.npy"
    np.save(tmp, packed)
    os.replace(tmp, mask_path)
    with open(meta_path, "w") as f:
        json.dump({"count": int(n), "val_count": int(val_count),
                   "val_fraction": val_fraction,
                   "hash_version": VAL_HASH_VERSION,
                   "threshold": val_hash_threshold(val_fraction)}, f, indent=2)
    elapsed = max(time.time() - start_time, 1e-9)
    print(f"val mask built: {mask_path} ({val_count:,} of {n:,} rows, "
          f"{val_count / max(n, 1):.4%}; {n / elapsed:,.0f} rows/s)", flush=True)
    return packed, int(val_count)


def mask_bits(packed, begin, end):
    """Boolean rows [begin, end) of a packed (little bit order) mask."""
    if end > len(packed) * 8:
        raise ValueError(f"mask_bits: [{begin},{end}) past a mask of {len(packed) * 8} bits")
    bits = np.unpackbits(packed[begin >> 3:(end + 7) >> 3], bitorder="little")
    offset = begin & 7
    return bits[offset:offset + (end - begin)].astype(bool)


# ---- Tail split without the recurring positions (2026-10-01) ---------------
#
# WHY. The tail cut keeps whole games apart; what it leaks is the 3.6-5.5% of
# its validation rows whose position is also a training row somewhere. The
# hash split above closes that and opens a bigger leak inside every game. This
# keeps the tail cut and removes only that: a tail row stays in validation iff
# its position (the same order-free hash) occurs among NO training row of ANY
# file of the store. Training is untouched, row for row and batch for batch,
# and the validation pass still reads only the tails.
#
# ONE GLOBAL PASS. Whether a tail row stays depends on every file's training
# rows, so the masks are built together: hash every tail and keep the distinct
# hashes, sorted; hash every training row and mark the distinct hashes it hits;
# then one mask per file over its tail. The training hashes are looked up in
# sorted blocks of 8M: binary search over ~65M distinct hashes (520 MB) measured
# 0.3M lookups/s with the queries in random order and 4-5M/s sorted, cache
# misses either way. Estimated for 65M distinct hashes: about 1.1 GB resident
# while it runs, 2.6 GB at the peak of the unique, nothing after.
#
# THE CACHE. One mask per file next to its feature shards, named by a digest of
# the WHOLE file list - each file's path, rows and cut, and the size and mtime
# of its stm and opp shards - with the fraction and the hash version. A file
# rebuilt, added or dropped changes the digest, so every mask is rebuilt; and
# the module's usual rules hold on top: both files present, newer than the
# file's shards, the meta matching, the mask counting the rows its meta says.
# A run over another file list writes its own masks beside these and leaves
# them alone (a few MB per corpus).

_DEDUP_LOOKUP_BLOCK = 1 << 23   # training rows hashed, sorted and looked up at a time


def _dedup_digest(files, val_fraction):
    items = []
    for f in files:
        shards = _shard_paths(shard_dir_for(f["path"]))
        stats = [os.stat(shards[k]) for k in ("stm", "opp")]
        items.append([os.path.normcase(os.path.abspath(f["path"])), int(f["count"]),
                      int(f["train_count"])] + [[s.st_size, s.st_mtime_ns] for s in stats])
    items.sort()
    blob = json.dumps({"files": items, "val_fraction": val_fraction,
                       "hash_version": VAL_HASH_VERSION}, sort_keys=True)
    return hashlib.sha1(blob.encode("ascii")).hexdigest()[:16]


def _dedup_mask_paths(path, val_fraction, digest):
    stem = os.path.join(shard_dir_for(path),
                        f"valdedup_h{VAL_HASH_VERSION}_{val_fraction!r}_{digest}")
    return stem + ".npy", stem + ".json"


def _load_dedup_mask(f, val_fraction, digest):
    """(packed, kept) of one file's cached mask, or (None, why it is not usable)."""
    mask_path, meta_path = _dedup_mask_paths(f["path"], val_fraction, digest)
    if not (os.path.exists(mask_path) and os.path.exists(meta_path)):
        return None, "no mask for this file list"
    shards = _shard_paths(shard_dir_for(f["path"]))
    newest_source = max(os.path.getmtime(shards[k]) for k in ("stm", "opp"))
    if min(os.path.getmtime(mask_path), os.path.getmtime(meta_path)) < newest_source:
        return None, "STALE (feature shards are newer)"
    tail_rows = f["count"] - f["train_count"]
    try:
        with open(meta_path) as fh:
            meta = json.load(fh)
        packed = np.load(mask_path)
        valid = (meta.get("digest") == digest
                 and meta.get("hash_version") == VAL_HASH_VERSION
                 and meta.get("val_fraction") == val_fraction
                 and meta.get("count") == f["count"]
                 and meta.get("tail_begin") == f["train_count"]
                 and packed.dtype == np.uint8 and packed.shape == ((tail_rows + 7) // 8,)
                 and int(np.unpackbits(packed).sum()) == meta.get("kept"))
    except Exception as exc:  # noqa: BLE001 - unreadable cache, rebuilt by the caller
        return None, f"unreadable ({exc})"
    if not valid:
        return None, f"mismatch ({meta})"
    return packed, int(meta["kept"])


def build_dedup_val_masks(files, val_fraction, force=False):
    """[(packed, kept)] per entry of 'files' (FeatureStore entries, already cut).

    Each packed mask covers its file's tail, rows [train_count, count): bit
    i - train_count set = row i stays in validation. Reused only when EVERY
    file's mask is valid for this file list; otherwise all are rebuilt, since
    any one of them depends on all the others.
    """
    val_fraction = float(val_fraction)
    digest = _dedup_digest(files, val_fraction)
    if not force:
        cached = []
        for f in files:
            packed, info = _load_dedup_mask(f, val_fraction, digest)
            if packed is None:
                print(f"val dedup masks to build for all {len(files)} files "
                      f"(digest {digest}); first unusable: {f['path']}: {info}", flush=True)
                break
            cached.append((packed, info))
        else:
            print(f"val dedup masks reused for {len(files)} files (digest {digest})")
            return cached

    start_time = time.time()
    # 1. Every tail hashed; the distinct hashes sorted, and each tail row's
    #    place among them kept for step 3.
    tails = [position_hashes_range(f["stm"], f["opp"], f["train_count"], f["count"])
             for f in files]
    tail_lengths = [len(h) for h in tails]
    distinct, where = np.unique(np.concatenate(tails), return_inverse=True)
    del tails
    print(f"  val dedup: {sum(tail_lengths):,} tail rows hashed, {len(distinct):,} distinct "
          f"positions ({time.time() - start_time:.0f} s)", flush=True)

    # 2. Every training row hashed and looked up; a distinct hash it hits is
    #    a validation position the net trains on.
    in_train = np.zeros(len(distinct), dtype=bool)
    train_total = sum(f["train_count"] for f in files)
    done = 0
    phase_start = time.time()
    for f in files:
        if not len(distinct):
            break
        for b in range(0, f["train_count"], _DEDUP_LOOKUP_BLOCK):
            e = min(b + _DEDUP_LOOKUP_BLOCK, f["train_count"])
            h = position_hashes_range(f["stm"], f["opp"], b, e)
            h.sort()
            at = np.searchsorted(distinct, h)
            np.minimum(at, len(distinct) - 1, out=at)
            in_train[at[distinct[at] == h]] = True
            done += e - b
        elapsed = max(time.time() - phase_start, 1e-9)
        rate = done / elapsed
        print(f"  val dedup: training rows of {f['path']} looked up "
              f"({done:,}/{train_total:,}, {rate:,.0f} rows/s, "
              f"ETA {(train_total - done) / max(rate, 1e-9) / 60:.1f} min)", flush=True)

    # 3. One mask per file over its tail. The meta marks a mask complete, so
    #    any old one goes first and the new one is written last.
    keep_all = ~in_train[where]
    out = []
    first = 0
    for f, tail_rows in zip(files, tail_lengths):
        keep = keep_all[first:first + tail_rows]
        first += tail_rows
        packed = np.packbits(keep, bitorder="little")
        kept = int(keep.sum())
        mask_path, meta_path = _dedup_mask_paths(f["path"], val_fraction, digest)
        if os.path.exists(meta_path):
            os.remove(meta_path)
        tmp = mask_path[:-len(".npy")] + ".tmp.npy"
        np.save(tmp, packed)
        os.replace(tmp, mask_path)
        with open(meta_path, "w") as fh:
            json.dump({"digest": digest, "count": int(f["count"]),
                       "tail_begin": int(f["train_count"]), "kept": kept,
                       "val_fraction": val_fraction,
                       "hash_version": VAL_HASH_VERSION,
                       "files": len(files)}, fh, indent=2)
        out.append((packed, kept))
    tail_total = sum(tail_lengths)
    kept_total = sum(k for _, k in out)
    print(f"val dedup masks built for {len(files)} files: {kept_total:,} of {tail_total:,} "
          f"tail rows kept, {tail_total - kept_total:,} ({(tail_total - kept_total) / max(tail_total, 1):.2%}) "
          f"dropped as positions the training rows contain "
          f"({time.time() - start_time:.0f} s)", flush=True)
    return out


def _read_selected(stream, begin, end, rows):
    """Rows 'rows' (sorted, relative to begin) of [begin, end) of one stream.

    The row count is checked BEFORE the selection: a mapping that comes back
    short would otherwise be filtered by a mask meant for other rows, and every
    stream after it would pair its rows with the wrong positions. Index gathers
    rather than a boolean mask: measured 2-3x cheaper on the 2-D streams.
    """
    if isinstance(stream, _CsrView):
        return stream.take(begin, end, rows)
    block = np.asarray(stream[begin:end])
    if len(block) != end - begin:
        raise ValueError(f"stream holds {len(block)} rows of [{begin},{end})")
    return block[rows] if block.ndim == 1 else block.take(rows, axis=0)


class FeatureStore:
    """
    Memory-mapped features for one or more datasets, addressed as one logical
    array. Nothing is loaded until a batch asks for it.

    Train/validation are split per FILE by a tail cut, exactly as the in-RAM
    path did: splitting the concatenation instead would make the validation set
    come only from the last file, and a tail cut keeps whole games on one side
    because the record format is ordered by game.

    val_split="hash" (2026-10-01) splits by POSITION instead: a row is
    validation iff the hash of its position says so (see build_val_mask), so a
    position that recurs across files can no longer be validation in one and
    training in another. Both splits then span every file whole and each chunk
    is filtered row by row before it is queued. It splits every game across
    both sides, though (see the section header).

    val_split="tail-dedup" (2026-10-01) keeps the tail cut and drops from
    validation the tail rows whose position is a training row of any file (see
    build_dedup_val_masks). Training is the tail split's, unchanged; only the
    validation chunks are filtered.
    """

    def __init__(self, paths, val_fraction=0.05, threats=False, coarse=False, weights=None,
                 val_split="tail"):
        # Threat shards are OPTIONAL and live in their own directory, so a run
        # without them never touches - or builds - that cache.
        self.threats = bool(threats)
        self.coarse = bool(coarse)
        if val_split not in VAL_SPLITS:
            raise ValueError(f"val_split must be one of {VAL_SPLITS}, got {val_split!r}")
        self.val_split = val_split
        # What LoaderProcess opens the same store again from, in its child.
        self.init_args = {"paths": list(paths), "val_fraction": val_fraction,
                          "threats": self.threats, "coarse": self.coarse,
                          "weights": dict(weights or {}), "val_split": val_split}
        self.files = []
        # Per-file sampling weight (2026-09-18, audit find): stream_batches used
        # to build its chunk list straight from every file's own record count,
        # so the mix between sources was implicitly whatever their on-disk sizes
        # happened to be - a newer, higher-quality generation could be
        # outnumbered 3:1 by an older one with nobody having decided that.
        # `weights` is a dict {substring: weight}; the first substring found in
        # a path sets that file's weight (default 1.0, i.e. today's behaviour
        # when weights is None or nothing matches).
        weights = weights or {}
        def weight_for(path):
            for substr, w in weights.items():
                if substr in path:
                    return w
            return 1.0
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
            # train_count is the number of training rows in every mode. The
            # tail and tail-dedup splits also use it as the cut index; the hash
            # split carries the mask instead and never cuts.
            val_mask = None
            if self.val_split == "hash":
                val_mask, val_count = build_val_mask(path, val_fraction)
                train_count = count - val_count
            else:
                train_count = count - int(count * val_fraction)
            entry = {
                "path": path, "stm": stm, "opp": opp,
                "scores": scores, "results": results,
                "count": count, "train_count": train_count,
                "weight": weight_for(path),
            }
            if val_mask is not None:
                entry["val_mask"] = val_mask
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
            weight_note = f", weight {entry['weight']:g}x" if entry["weight"] != 1.0 else ""
            split_note = ""
            if val_mask is not None:
                split_note = ", hash split"
            elif self.val_split == "tail-dedup":
                split_note = ", val before dedup"
            print(f"dataset: {count:,} records from {path} "
                  f"(train {train_count:,} / val {count - train_count:,}{split_note}){weight_note}")

        # The dedup masks need every file's training rows, so they come after
        # the loop; val_count then replaces the tail length in val_total.
        if self.val_split == "tail-dedup" and self.files:
            for entry, (packed, kept) in zip(self.files,
                                             build_dedup_val_masks(self.files, val_fraction)):
                entry["val_keep"] = packed
                entry["val_count"] = kept

    @property
    def train_total(self):
        return sum(f["train_count"] for f in self.files)

    @property
    def val_total(self):
        return sum(f.get("val_count", f["count"] - f["train_count"]) for f in self.files)

    def _spans(self, split):
        """(file, start, stop) ranges for the requested split. The hash split
        reads every file whole for both splits; its mask picks the rows. The
        tail-dedup split cuts like the tail split; its mask filters the tail."""
        for f in self.files:
            if self.val_split == "hash":
                yield f, 0, f["count"]
            elif split == "train":
                yield f, 0, f["train_count"]
            else:
                yield f, f["train_count"], f["count"]

    def train_rows_per_pass(self, chunk=8192):
        """Training rows one stream_batches pass yields, in expectation.

        train_total when every weight is 1. A weighted file draws
        round(n * w) of its n chunks, each equally likely, so it yields its
        training rows times that over n, with replacement or without. The
        trainer sizes its lambda ramp from this, so that a --reweight run
        ramps over the batches it actually draws.
        """
        rows = 0
        for f, start, stop in self._spans("train"):
            w = f.get("weight", 1.0)
            n = len(range(start, stop, chunk))
            if w == 1.0 or not n:
                rows += f["train_count"]
            else:
                rows += f["train_count"] * max(1, int(round(n * w))) / n
        return int(round(rows))

    def stream_batches(self, batch_size, rng, split="train",
                       chunk=8192, buffer_chunks=64, workers=0):
        """
        Yields (stm, opp, scores, results) batches, plus (stm_t, opp_t) when
        the store was opened with threats. Chunk order is shuffled
        globally across every file, then each buffer of chunks is shuffled
        internally before being cut into batches.

        With the hash split every chunk spans both splits and keeps only the
        rows of the requested one (about 95% for train, 5% for val); a buffer
        is still counted in chunks, so a validation buffer simply holds fewer
        rows. The tail-dedup split filters its validation chunks the same way
        and its training chunks not at all.

        workers > 0 (2026-10-02) reads the chunks on that many threads, up to
        two buffers ahead, and shuffles each buffer's streams in parallel. The
        batches and their order are the same as with workers=0, byte for
        byte, and the rng is drawn the same number of times with the same
        arguments in the same order (see _ChunkReader).
        """
        chunks = []
        for file_index, (f, start, stop) in enumerate(self._spans(split)):
            file_chunks = [(file_index, begin, min(begin + chunk, stop))
                            for begin in range(start, stop, chunk)]
            w = f.get("weight", 1.0)
            if w == 1.0 or not file_chunks:
                chunks.extend(file_chunks)
                continue
            # Resample this file's own chunk list to w times its natural
            # count: w>1 oversamples (with replacement, since there is
            # nothing further to draw from), w<1 undersamples (without
            # replacement - never invent rows that were never read).
            target = max(1, int(round(len(file_chunks) * w)))
            if w > 1.0:
                idx = rng.integers(0, len(file_chunks), size=target)
            else:
                idx = rng.choice(len(file_chunks), size=target, replace=False)
            chunks.extend(file_chunks[i] for i in idx)
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
        _warned_rows = False
        _skipped_chunks = [0]
        # Rows left over when a buffer does not divide evenly into batches. They
        # are CARRIED into the next buffer rather than dropped: discarding a
        # partial batch per buffer would quietly throw away real training data
        # on every epoch, and the smaller the buffer the worse it gets (measured
        # at 0.29% loss with a deliberately tiny buffer). Only the final tail of
        # the whole pass is dropped, exactly like the in-RAM path.
        carry = None
        by_mask = self.val_split == "hash" or (self.val_split == "tail-dedup" and split == "val")

        def read_chunk(chunk_index):
            """(slices, rows) of one chunk, every stream of it; raises on failure."""
            file_index, begin, end = chunks[chunk_index]
            f = self.files[file_index]
            if by_mask:
                if self.val_split == "hash":
                    select = mask_bits(f["val_mask"], begin, end)
                    if split == "train":
                        select = ~select
                else:
                    # tail-dedup validation: the mask starts at the cut.
                    cut = f["train_count"]
                    select = mask_bits(f["val_keep"], begin - cut, end - cut)
                picked = np.flatnonzero(select)
                return [_read_selected(f[key], begin, end, picked) for key in keys], len(picked)
            return [np.asarray(f[key][begin:end]) for key in keys], end - begin

        reader = _ChunkReader(read_chunk, order, workers, 2 * buffer_chunks) if workers > 0 else None
        try:
            for position, chunk_index in enumerate(order):
                file_index, begin, end = chunks[chunk_index]
                f = self.files[file_index]
                # np.asarray forces the mapped slice into real memory once, so the
                # later fancy-indexing does not fault page by page.
                # Every stream of the chunk is read BEFORE any of it is queued, so a
                # chunk that cannot be read leaves the buffer untouched and aligned.
                # The fqcohuman training died four times in two days inside this
                # loop or one step past it (2026-09-10), the last time on a coarse
                # slice whose offsets were not monotonic in memory although they
                # are on disk. A chunk is 8,192 of ~900M rows: it is logged with
                # its file and range, skipped, and the run goes on. The log line is
                # the evidence the next repair works from.
                #
                # Masked chunks (the hash split, and the validation of the
                # tail-dedup split): ONE selection, computed from the mask before
                # anything is read, is applied to every stream of the chunk - the
                # same rows, in the same order, in each of them - and the filtered
                # slices are what gets queued. Everything after this point (the
                # buffer, the one-length cut, the permutation, the carry) sees
                # ordinary aligned slices and does not know a mask exists. A
                # stream that cannot deliver the chunk whole raises inside this
                # try, so the chunk is skipped exactly like any other unreadable
                # one. With workers the chunk was read on a reader thread, and a
                # failure there is raised here, at the chunk's own position.
                try:
                    if reader is not None:
                        slices, chunk_rows = reader.get(position)
                    else:
                        slices, chunk_rows = read_chunk(chunk_index)
                except Exception as exc:  # noqa: BLE001 - any read failure of any stream
                    _skipped_chunks[0] += 1
                    print(f"  warning: chunk {chunk_index} of {f.get('path', '?')} rows [{begin},{end}) "
                          f"could not be read and was skipped ({_skipped_chunks[0]} so far): {exc}",
                          flush=True)
                    slices = None
                if slices is not None:
                    for slot, sl in enumerate(slices):
                        pending[slot].append(sl)
                    pending_rows += chunk_rows

                # A skipped chunk queues nothing but still reaches this test, so a
                # pass whose LAST chunk cannot be read still flushes its buffer.
                # The except used to end in `continue`, which jumped over the flush
                # and lost up to buffer_chunks chunks plus the carry (2026-10-01,
                # review: 241 rows of a pass instead of under one batch). An empty
                # buffer has nothing to flush; the carry is under one batch, the
                # final tail every pass drops anyway.
                is_last = position == len(order) - 1
                if not pending[0] or (len(pending[0]) < buffer_chunks and not is_last):
                    continue

                # Masked chunks: the rows carried from the last buffer join this one
                # as one more part BEFORE its single shuffle, instead of going
                # through the second concatenate-and-shuffle below. Same outcome -
                # one uniform permutation of carry plus buffer, so they still do not
                # land together - at half the cost. It matters here and not in the
                # tail split: 64 full chunks of 8192 are an exact number of batches,
                # so a tail buffer almost never leaves a carry, while a filtered one
                # leaves one every time. Measured on a real shard with its coarse
                # companion, one core: the train pass at +44% over the tail split
                # with the second shuffle, +5% with this. The tail split, and the
                # training side of tail-dedup, keep the path below untouched, so
                # their batches stay byte-for-byte what they were.
                if by_mask and carry is not None:
                    for slot, rows_carried in enumerate(carry):
                        pending[slot].append(rows_carried)
                    pending_rows += len(carry[0])
                    carry = None

                # The permutation must be sized from the DATA, and every stream must
                # be cut to the same length before it is applied (2026-09-10).
                #
                # Two crashes taught this in one day. Sizing it from pending_rows, a
                # counter of what the chunk table SAYS each slice holds, killed a run
                # at epoch 17 of 21: "index 268859706 is out of bounds for axis 0
                # with size 524288". Sizing it from arrays[0] alone then killed the
                # next run inside CUDA, because a permutation valid for the first
                # stream silently reorders a LONGER one into a different order - the
                # features of one position paired with the score of another, which is
                # not a crash, it is training on nonsense until something downstream
                # trips over it.
                #
                # So: one length, the shortest, applied to all of them. Equal lengths
                # is the normal case and this costs a min() to guarantee.
                #
                # The lengths are summed over the parts rather than read off their
                # concatenation (2026-10-02): along axis 0 they are the same number,
                # and knowing them first lets each stream be concatenated, cut and
                # shuffled in one task, the streams in parallel with workers.
                lengths = [sum(len(sl) for sl in part) for part in pending]
                rows_now = min(lengths)
                if (max(lengths) != rows_now or rows_now != pending_rows) and not _warned_rows:
                    _warned_rows = True
                    print(f"  warning: the streams of this buffer disagree - lengths {lengths}, "
                          f"chunk table says {pending_rows}. Cutting all of them to {rows_now} so "
                          f"they stay aligned, and continuing. Reported once per pass.", flush=True)
                perm = rng.permutation(rows_now)
                arrays = _shuffle_streams(reader, pending, rows_now, perm)
                if carry is not None:
                    # Prepend before batching, after the shuffle, so carried rows do
                    # not all land together at the head of one batch.
                    # Same rule as above: the carried rows go through a second
                    # shuffle, so the same one-length guarantee has to hold here or
                    # it reintroduces exactly the misalignment the first one closed.
                    mixed_rows = min(len(c) + len(a) for c, a in zip(carry, arrays))
                    mixed = rng.permutation(mixed_rows)
                    arrays = _shuffle_streams(reader, [[c, a] for c, a in zip(carry, arrays)],
                                              mixed_rows, mixed)
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
        finally:
            # Also on an abandoned pass (the consumer stopped early or raised):
            # no read goes on for a generator nobody will advance again.
            if reader is not None:
                reader.close()


def _shuffle_streams(reader, parts, rows, perm):
    """[np.concatenate(p)[:rows][perm] for p in parts], on the reader's mixing
    threads when there is a reader. Each stream is its own task and the results
    come back in stream order, so the arrays are the ones the serial loop
    builds; the rng was already drawn by the caller."""
    def one(part):
        return np.concatenate(part)[:rows][perm]
    if reader is None:
        return [one(part) for part in parts]
    return reader.map_streams(one, parts)


class _ChunkReader:
    """Reads the chunks of one stream_batches pass ahead of the loop, in threads.

    WHY (2026-10-02). The fqblind run (282 files, --coarse) did 10.5 steps/s
    with the GPU idle half the time, in bursts of ~1.4 s: the loader built each
    64-chunk buffer on one thread - 0.67 s expanding the coarse CSR, 0.44 s of
    concatenation that is mostly page faults on cold shards, 0.36 s shuffling -
    while --prefetch 4 covered only four 50 ms steps of it. Measured on the CPU
    over the real files: 16.6-17.6 batches/s on one thread, 46-60 with four
    readers, against ~20 the GPU takes.

    WHAT CHANGES AND WHAT DOES NOT. Only where the work runs. The chunk table,
    its order and every rng draw stay on the stream_batches thread, in the
    same sequence: reading a chunk draws nothing, and the buffer permutations
    are drawn by the loop before the shuffle tasks are handed their streams.
    Chunks come back by position, so the buffer is assembled in the same order;
    a failed read is raised at its own position and skipped there by the same
    except as before. The one difference is WHEN a mapped slice is paged in: a
    reader copies it out of the mapping on its thread (np.array of a view, the
    same bytes), instead of leaving the page faults to the concatenation.
    """

    def __init__(self, read_chunk, order, workers, lookahead):
        import concurrent.futures
        self._read = read_chunk
        self._order = order
        self._lookahead = max(1, int(lookahead))
        self._pool = concurrent.futures.ThreadPoolExecutor(
            max_workers=workers, thread_name_prefix="noa-read")
        # A pool of its own: queued behind up to 'lookahead' reads, a shuffle
        # would wait for the next buffer to be read before this one is served.
        self._mix = concurrent.futures.ThreadPoolExecutor(
            max_workers=workers, thread_name_prefix="noa-mix")
        self._futures = {}
        self._next = 0

    def _read_owned(self, chunk_index):
        slices, rows = self._read(chunk_index)
        return [sl if sl.flags.owndata else np.array(sl) for sl in slices], rows

    def get(self, position):
        """The chunk at 'position' of the order: (slices, rows), or its exception."""
        while self._next < len(self._order) and self._next <= position + self._lookahead:
            self._futures[self._next] = self._pool.submit(self._read_owned,
                                                          self._order[self._next])
            self._next += 1
        return self._futures.pop(position).result()

    def map_streams(self, fn, parts):
        return list(self._mix.map(fn, parts))

    def close(self):
        for future in self._futures.values():
            future.cancel()
        self._futures.clear()
        self._pool.shutdown(wait=False, cancel_futures=True)
        self._mix.shutdown(wait=False, cancel_futures=True)


# ---- The loader in a process of its own (2026-10-02) ----------------------
#
# WHY. The reader threads above take the loader off the critical path but not
# off the GIL. Simulated on the CPU against the real files (the trainer's
# prefetch and host work per batch, plus 300 small torch calls and a 45 ms
# GIL-free wait standing in for the GPU): 20.3 steps/s with no loader at all,
# 10.4 with the loader as it was (the real run did 10.5), 16.7 with four
# reader threads and --prefetch 32 beside the step - every torch call gives
# the GIL up and has to win it back from them. With the same loader running
# flat out in ANOTHER process the step kept its 20.2. So the whole
# stream_batches runs in a child, reader threads included, and the trainer
# only copies finished batches out of shared memory: 19.7 steps/s.
#
# SAME BATCHES, SAME RNG. The child opens the same FeatureStore (its file
# table is checked against the parent's before anything is streamed), and each
# pass hands it the parent rng's exact state; the child runs the unchanged
# stream_batches on a Generator set to that state and, at the end of the pass,
# sends the state back, which the parent rng takes. Draws, order and batches
# are therefore those of the in-process loader, and so is the rng a resume
# saves. Whatever the child prints (a skipped chunk) comes back through the
# pipe and is printed by the parent in its place among the batches.

def _store_summary(store):
    """What the parent and the child must agree on before a pass is served."""
    return [(f["path"], int(f["count"]), int(f["train_count"]),
             int(f.get("val_count", -1)), float(f.get("weight", 1.0))) for f in store.files]


class LoaderFailed(RuntimeError):
    """The loader process reported an error, or died."""


class LoaderProcess:
    """stream_batches of a FeatureStore, run in a child process.

    One pass at a time. stream_batches here takes the same arguments as
    FeatureStore.stream_batches and yields the same tuples, each array a fresh
    copy out of the shared slot it arrived in (the slot is handed back at
    once). A pass abandoned half way stops the child's pass too; a failure in
    the child raises LoaderFailed with its traceback.
    """

    def __init__(self, store, slots=8):
        import multiprocessing
        ctx = multiprocessing.get_context("spawn")
        self._conn, child = ctx.Pipe(duplex=True)
        self._proc = ctx.Process(target=_loader_main, args=(child, store.init_args, slots),
                                 name="noa-loader", daemon=True)
        self._proc.start()
        child.close()
        self._shm = {}
        self._busy = False
        kind, summary = self._recv()
        if kind != "ready" or summary != _store_summary(store):
            self.close()
            raise LoaderFailed("the loader process opened another file table than the trainer: "
                               f"{len(summary or [])} files against {len(store.files)}")

    def _recv(self):
        while True:
            try:
                kind, payload = self._conn.recv()
            except (EOFError, OSError) as exc:
                raise LoaderFailed(f"the loader process died (exit code {self._proc.exitcode})") from exc
            if kind == "out":
                sys.stdout.write(payload)
                sys.stdout.flush()
                continue
            if kind == "error":
                raise LoaderFailed(f"the loader process failed:\n{payload}")
            return kind, payload

    def _slot(self, name):
        from multiprocessing import shared_memory
        if name not in self._shm:
            self._shm[name] = shared_memory.SharedMemory(name=name)
        return self._shm[name].buf

    def stream_batches(self, batch_size, rng, split="train", chunk=8192, buffer_chunks=64,
                       workers=0):
        if self._busy:
            raise RuntimeError("LoaderProcess serves one pass at a time")
        self._busy = True
        bitgen = rng.bit_generator
        ended = False
        try:
            # Sent on the first next(), as the in-process generator draws from
            # the rng only then.
            self._conn.send(("pass", ({"batch_size": batch_size, "split": split, "chunk": chunk,
                                       "buffer_chunks": buffer_chunks, "workers": workers},
                                      type(bitgen).__name__, bitgen.state)))
            while True:
                try:
                    kind, payload = self._recv()
                except LoaderFailed:
                    ended = True            # an error ends the child's pass
                    raise
                if kind == "batch":
                    name, layout = payload
                    buf = self._slot(name)
                    arrays = tuple(np.array(np.ndarray(shape, dtype=np.dtype(dt), buffer=buf,
                                                       offset=offset))
                                   for dt, shape, offset in layout)
                    self._conn.send(("free", name))
                    yield arrays
                elif kind == "end":
                    bitgen.state = payload
                    ended = True
                    return
        finally:
            if not ended:
                self._abandon(bitgen)
            self._busy = False

    def _abandon(self, bitgen):
        # The child answers a stop with the one "end" every pass gets; the
        # batches it sent meanwhile are handed back unread.
        try:
            self._conn.send(("stop", None))
            while True:
                kind, payload = self._recv()
                if kind == "batch":
                    self._conn.send(("free", payload[0]))
                elif kind == "end":
                    bitgen.state = payload
                    return
        except Exception:  # noqa: BLE001 - the child is gone; nothing left to stop
            pass

    def close(self):
        try:
            self._conn.send(("quit", None))
        except Exception:  # noqa: BLE001 - already gone
            pass
        self._proc.join(timeout=10)
        if self._proc.is_alive():
            self._proc.terminate()
        for shm in self._shm.values():
            try:
                shm.close()
            except BufferError:     # a view still held by the caller
                pass
        self._shm.clear()
        self._conn.close()


class _PipeOut(io.TextIOBase):
    """The child's stdout: every finished line goes to the parent, in order."""

    def __init__(self, conn):
        self._conn = conn
        self._parts = []

    def write(self, text):
        self._parts.append(text)
        if "\n" in text:
            self.flush()
        return len(text)

    def flush(self):
        if self._parts:
            text, self._parts = "".join(self._parts), []
            self._conn.send(("out", text))


class _StopPass(Exception):
    pass


class _Quit(Exception):
    pass


def _loader_main(conn, init_args, slots):
    """Entry point of the loader process."""
    import traceback
    from multiprocessing import shared_memory
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            store = FeatureStore(**init_args)
        conn.send(("ready", _store_summary(store)))
    except BaseException:  # noqa: BLE001 - reported to the parent
        conn.send(("error", traceback.format_exc()))
        return
    sys.stdout = _PipeOut(conn)
    owned = {}      # name -> SharedMemory written here
    free = []       # names the parent has handed back

    def handle(kind, payload):
        if kind == "free":
            free.append(payload)
        elif kind == "stop":
            raise _StopPass()
        elif kind == "quit":
            raise _Quit()

    def acquire(nbytes):
        while True:
            for name in free:
                if owned[name].size >= nbytes:
                    free.remove(name)
                    return name
            if len(owned) < slots:
                shm = shared_memory.SharedMemory(create=True, size=nbytes)
                owned[shm.name] = shm
                return shm.name
            if free:
                old = owned.pop(free.pop())
                old.close()
                old.unlink()
                continue
            handle(*conn.recv())

    def serve(params, bitgen_name, state):
        rng = np.random.Generator(getattr(np.random, bitgen_name)())
        rng.bit_generator.state = state
        gen = store.stream_batches(params["batch_size"], rng, split=params["split"],
                                   chunk=params["chunk"], buffer_chunks=params["buffer_chunks"],
                                   workers=params["workers"])
        try:
            for batch in gen:
                layout, nbytes = [], 0
                for a in batch:
                    layout.append((a.dtype.str, a.shape, nbytes))
                    nbytes += -(-a.nbytes // 64) * 64
                name = acquire(max(nbytes, 64))
                buf = owned[name].buf
                for a, (dt, shape, offset) in zip(batch, layout):
                    np.ndarray(shape, dtype=a.dtype, buffer=buf, offset=offset)[...] = a
                del buf
                conn.send(("batch", (name, layout)))
                while conn.poll():
                    handle(*conn.recv())
        except _StopPass:
            pass
        except _Quit:
            gen.close()
            raise
        except Exception:  # noqa: BLE001 - reported to the parent, which raises
            gen.close()
            sys.stdout.flush()
            conn.send(("error", traceback.format_exc()))
            return
        gen.close()
        sys.stdout.flush()
        conn.send(("end", rng.bit_generator.state))

    try:
        while True:
            kind, payload = conn.recv()
            if kind == "pass":
                serve(*payload)
            elif kind == "free":
                free.append(payload)
            elif kind == "quit":
                break
            # A "stop" between passes is one that crossed the pass's own
            # "end" in the pipe: that pass is over, and it gets no answer.
    except (_Quit, EOFError, OSError):
        pass
    finally:
        for shm in owned.values():
            shm.close()
            shm.unlink()


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
