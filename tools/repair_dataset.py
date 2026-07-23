# Repairs a .noadata file left with a placeholder header (recordCount=0)
# because NoaChess.DataGen was stopped before finishing (Ctrl+C, closed
# console, ran out of time, etc).
#
# The dataset is written game-by-game (each finished game's positions are
# flushed atomically), so everything up to the interruption point is valid
# data - only the header's record count (patched at the very end of a full
# run) is wrong. This recomputes it from the actual file size, drops any
# trailing partial record, and rewrites just that header field.
#
# Usage: python repair_dataset.py data/run4.noadata

import sys
import struct

HEADER_SIZE = 64
RECORD_SIZE = 40
MAGIC = b"NOADATA1"

path = sys.argv[1]

with open(path, "r+b") as f:
    header = f.read(HEADER_SIZE)
    if header[:8] != MAGIC:
        raise SystemExit(f"{path}: not a NOADATA1 file")

    f.seek(0, 2)
    file_size = f.tell()
    usable_bytes = file_size - HEADER_SIZE
    record_count = usable_bytes // RECORD_SIZE
    trailing_partial = usable_bytes % RECORD_SIZE

    stated_count = int.from_bytes(header[24:32], "little")
    print(f"file size: {file_size:,} bytes")
    print(f"stated record count in header: {stated_count:,}")
    print(f"actual complete records found: {record_count:,}")
    if trailing_partial:
        print(f"dropping {trailing_partial} trailing bytes (incomplete last record)")

    new_size = HEADER_SIZE + record_count * RECORD_SIZE
    f.truncate(new_size)

    f.seek(24)
    f.write(struct.pack("<Q", record_count))

print(f"done: {path} repaired, {record_count:,} records usable for training")
