# Cross-language verification of an exported .noannue.
#
# WHY. The engine and the trainer are two independent implementations of the
# same integer arithmetic, connected only by a byte layout and a bucket formula.
# Nothing in either one fails loudly when they disagree: the net simply plays
# slightly wrong chess forever. This script reads the exported FILE (not the
# PyTorch model) and reproduces the engine's quantized forward pass in numpy, so
# the two can be compared on identical positions.
#
# Usage:
#   python verify_export.py --model ../../models/nnue/net.noannue \
#       --fen "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"
#
# Compare the printed value against:
#   NoaChess.DataGen --nnueprobe <model> "<fen>"
# They must be EQUAL. Any difference is a contract break, not a rounding detail.

import argparse
import struct

import numpy as np

import threats

from dataset import _make_index, PS_NB

HEADER_SIZE = 80
ARCH_INT16_L1 = 1
ARCH_INT8_L1 = 2
ARCH_INT8_L1_BUCKETS = 3
# Arch 4: arch 3 plus a threat feature transformer appended to the payload. Its
# row count is not in the header - it is a constant of the feature schema - so
# this reader asserts the payload length against it exactly as the engine does.
ARCH_THREATS = 4

_PIECE_CHARS = {"p": 0, "n": 1, "b": 2, "r": 3, "q": 4, "k": 5}


def parse_fen(fen):
    """Minimal FEN reader: returns (pieces, kings, side_to_move).

    pieces is a list of (square, piece_type 0..5, colour 0 white / 1 black) with
    a1 = 0 and a8 = 56, matching NnueFeatureIndex.
    """
    placement, stm = fen.split()[0], fen.split()[1]
    pieces, kings = [], [None, None]
    for rank_index, rank in enumerate(placement.split("/")):
        file_index = 0
        for char in rank:
            if char.isdigit():
                file_index += int(char)
                continue
            colour = 0 if char.isupper() else 1
            ptype = _PIECE_CHARS[char.lower()]
            square = (7 - rank_index) * 8 + file_index
            pieces.append((square, ptype, colour))
            if ptype == 5:
                kings[colour] = square
            file_index += 1
    return pieces, kings, 0 if stm == "w" else 1


def load_model(path):
    with open(path, "rb") as f:
        raw = f.read()
    (magic, version, schema, arch, ft_in, ft_out, l1_out,
     qa, qb, out_scale, buckets, payload_len, _sha) = struct.unpack(
        "<8s I I I i i i H H H H Q 32s", raw[:HEADER_SIZE])
    if magic != b"NOANNUE1":
        raise SystemExit(f"{path}: not a NOANNUE1 file")
    buckets = max(1, buckets) if arch in (ARCH_INT8_L1_BUCKETS, ARCH_THREATS) else 1

    body = raw[HEADER_SIZE:]
    offset = 0

    def take(count, dtype):
        nonlocal offset
        size = count * np.dtype(dtype).itemsize
        out = np.frombuffer(body[offset:offset + size], dtype=dtype)
        offset += size
        return out

    ft_w = take(ft_in * ft_out, np.int16).reshape(ft_in, ft_out)
    ft_b = take(ft_out, np.int16)
    l1_dtype = np.int16 if arch == ARCH_INT16_L1 else np.int8
    l1_w = take(buckets * l1_out * 2 * ft_out, l1_dtype).reshape(buckets, l1_out, 2 * ft_out)
    l1_b = take(buckets * l1_out, np.int32).reshape(buckets, l1_out)
    out_w = take(buckets * l1_out, np.int16).reshape(buckets, l1_out)
    out_b = take(buckets, np.int32)

    # Read LAST because it is appended last, so every offset above is the one
    # arch 1-3 files already use.
    th_w = None
    if arch == ARCH_THREATS:
        th_w = take(threats.THREAT_INPUT_SIZE * ft_out, np.int16).reshape(
            threats.THREAT_INPUT_SIZE, ft_out)

    if offset != payload_len:
        raise SystemExit(f"{path}: payload length mismatch ({offset} read, {payload_len} declared)")

    return dict(arch=arch, ft_in=ft_in, ft_out=ft_out, l1_out=l1_out, buckets=buckets,
                qa=qa, qb=qb, out_scale=out_scale,
                ft_w=ft_w, ft_b=ft_b, l1_w=l1_w, l1_b=l1_b, out_w=out_w, out_b=out_b,
                th_w=th_w)


def evaluate(model, fen):
    """Reproduces NnueInference exactly, in integers, from the side to move."""
    pieces, kings, stm = parse_fen(fen)

    accumulators = []
    for perspective in (0, 1):
        acc = model["ft_b"].astype(np.int32).copy()
        for square, ptype, colour in pieces:
            index = _make_index(perspective, kings[perspective], ptype, colour, square)
            acc += model["ft_w"][index].astype(np.int32)

        # Threats sum into the SAME accumulator, before the clamp, which is what
        # NnueAccumulator.Refresh does and what the trainer's forward pass does.
        # Reproducing them separately and adding afterwards would verify a
        # different function than the one the engine runs.
        if model["th_w"] is not None:
            occupancy = 0
            codes = []
            for square, ptype, colour in sorted(pieces):
                occupancy |= 1 << square
                codes.append(colour * 6 + ptype)
            nibbles = bytearray(16)
            for i, code in enumerate(codes):
                nibbles[i >> 1] |= code << (4 * (i & 1))
            white, black = threats.active_threats(occupancy, nibbles)
            for index in (white if perspective == 0 else black):
                acc += model["th_w"][index].astype(np.int32)

        accumulators.append(acc)

    stm_acc, opp_acc = accumulators[stm], accumulators[1 - stm]
    qa, qb = model["qa"], model["qb"]

    # Bucket selection: mirror of NnueModelHeader.BucketForPieceCount.
    buckets = model["buckets"]
    bucket = 0 if buckets <= 1 else min(max((len(pieces) - 1) * buckets // 32, 0), buckets - 1)

    activation = np.concatenate([np.clip(stm_acc, 0, qa), np.clip(opp_acc, 0, qa)])
    hidden = model["l1_b"][bucket].astype(np.int64) + \
        model["l1_w"][bucket].astype(np.int64) @ activation.astype(np.int64)

    # Floor division is safe HERE only because of the clamp that follows: a
    # negative quotient differs between floor and truncation, but both land
    # below zero and the clamp maps them to the same 0.
    a2 = np.clip(hidden // qb, 0, qa)
    output = int(model["out_b"][bucket]) + int(model["out_w"][bucket].astype(np.int64) @ a2)

    # The final division has NO clamp after it, so the rounding direction is
    # visible in the answer. C# integer division TRUNCATES toward zero; Python's
    # // FLOORS. They agree on every positive evaluation and differ by exactly
    # one centipawn on every negative one, which is why this script reported a
    # contract break on the first net whose test position evaluated below zero.
    # The engine is what plays, so the checker follows the engine.
    value = output * model["out_scale"]
    divisor = qa * qb
    truncated = abs(value) // divisor
    return int(-truncated if value < 0 else truncated), bucket, len(pieces)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--fen", action="append", required=True,
                        help="repeatable; one evaluation printed per FEN")
    args = parser.parse_args()

    model = load_model(args.model)
    print(f"arch={model['arch']} ft={model['ft_out']} l1={model['l1_out']} "
          f"buckets={model['buckets']} qa={model['qa']} qb={model['qb']}")
    for fen in args.fen:
        score, bucket, pieces = evaluate(model, fen)
        print(f"  {score:6d}  (bucket {bucket}, {pieces} pieces)  {fen}")
    print()
    print("Compare against: NoaChess.DataGen --nnueprobe <model> \"<fen>\"")
    print("Values must be EQUAL; a difference is a contract break.")


if __name__ == "__main__":
    main()
