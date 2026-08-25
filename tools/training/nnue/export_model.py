# Exports a trained checkpoint to the .noannue binary the C# engine loads.
# The quantization contract and byte layout mirror NnueNetwork.cs /
# NnueModelLoader.cs exactly.
#
#   ARCH 1 (legacy, v3.x)          ARCH 2 (v4.0.0, int8 L1)
#   QA = 255                       QA = 127   <- REQUIRED, see below
#   ftWeights  round(w*QA) int16   ftWeights  round(w*QA) int16
#   ftBias     round(b*QA) int16   ftBias     round(b*QA) int16
#   l1Weights  round(w*QB) int16   l1Weights  round(w*QB) INT8
#   l1Bias     round(b*QA*QB) i32  l1Bias     round(b*QA*QB) i32
#   outWeights round(w*QB) int16   outWeights round(w*QB) int16
#   outBias    round(b*QA*QB) i32  outBias    round(b*QA*QB) i32
#
# WHY ARCH 2 FORCES QA=127 (a correctness constraint, not tuning). The engine's
# int8 kernel is VPMADDUBSW, which computes a0*w0 + a1*w1 into an int16 lane,
# and int16 SATURATES:
#     QA=255 -> |255*127 + 255*127| = 64,770  > 32,767  -> saturates, WRONG
#     QA=127 -> |127*127 + 127*127| = 32,258  < 32,767  -> exact, always
# The C# loader refuses any arch-2 model with QA > 127, so an accidental export
# fails loudly at load time rather than evaluating silently wrong positions.
#
# Note that l1 weights were ALREADY clipped to +/-127 in the legacy path while
# being stored as int16, so moving them to int8 storage costs nothing at all.
# The only real change is one bit of activation resolution.
#
# Usage:
#   python export_model.py --checkpoint checkpoints/gen7.pt --out ../../models/nnue/net.noannue
#   python export_model.py --checkpoint checkpoints/gen7.pt --out net-i8.noannue --arch 2

import argparse
import hashlib
import struct

import numpy as np
import torch

from model import (NoaNnue, INPUT_SIZE, MAX_ACTIVE, FT_OUT, L1_OUT, L2_OUT,
                   OUT_BUCKETS, QA, QB, OUTPUT_SCALE)
from threats import THREAT_INPUT_SIZE, MAX_ACTIVE_THREATS as THREAT_MAX_ACTIVE

MAGIC = b"NOANNUE1"
FORMAT_VERSION = 1
FEATURE_SCHEMA_ID = 2

# Version 1's header is 80 fixed bytes with no spare room before the payload
# length, so architecture 5 - which needs a second layer width and a flag word -
# gets version 2: bytes 0..39 keep their meaning and their offsets exactly, and
# the new fields go at 40..47, pushing the length and the SHA down by eight.
# A version 1 file is still written and read byte for byte as before.
FORMAT_VERSION_2 = 2
HEADER_BYTES_V1 = 80
HEADER_BYTES_V2 = 88

ARCH_FLAG_PAIRWISE_FT = 1 << 0
ARCH_FLAG_THREATS = 1 << 1

ARCH_INT16_L1 = 1
ARCH_INT8_L1 = 2
ARCH_INT8_L1_BUCKETS = 3
# Arch 4: everything arch 3 has plus a second feature transformer for threats,
# appended to the payload. The header does not describe it - its row count is a
# constant of the feature schema, and the engine asserts the file size against
# that constant rather than reading a number it could disagree with.
ARCH_THREATS = 4
# Arch 5: pairwise transformer read, squared activations alongside clipped ones,
# a second hidden layer the output reads past, and a linear bypass. It carries
# threats through a flag rather than a sixth architecture id, so the two
# improvements compose instead of forking the format.
ARCH_DUAL = 5

# Activation scale per architecture. The int8 architectures are capped by the
# saturation bound proved above; the C# loader enforces the same number.
QA_FOR_ARCH = {ARCH_INT16_L1: QA, ARCH_INT8_L1: 127, ARCH_INT8_L1_BUCKETS: 127,
               ARCH_THREATS: 127, ARCH_DUAL: 127}


def quantize(tensor, scale, dtype, limit, name):
    q = np.round(tensor.detach().numpy() * scale)
    clipped = np.clip(q, -limit, limit)
    n_clipped = int((q != clipped).sum())
    if n_clipped:
        pct = 100.0 * n_clipped / q.size
        print(f"  warning: {name}: {n_clipped:,} of {q.size:,} weights clipped "
              f"to +/-{limit} ({pct:.3f}%)")
    return clipped.astype(dtype)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--arch", type=int, default=None,
                        choices=[ARCH_INT16_L1, ARCH_INT8_L1, ARCH_INT8_L1_BUCKETS,
                                 ARCH_THREATS, ARCH_DUAL],
                        help="1 = legacy int16 L1 (QA=255), 2 = int8 L1 (QA=127), "
                             "3 = int8 L1 + output buckets. Default: 3 when the "
                             "checkpoint has buckets, else 2.")
    args = parser.parse_args()

    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    ckpt_args = checkpoint.get("args", {})
    ft_out = ckpt_args.get("ft_out", FT_OUT)
    l1_out = ckpt_args.get("l1_out", L1_OUT)
    # Default 1, NOT OUT_BUCKETS: a checkpoint without this key was trained
    # before buckets existed, so it genuinely has one head. Defaulting to the
    # current module constant would build a model whose shape does not match
    # the saved weights and fail to load - or worse, load a reshaped mess.
    buckets = max(1, ckpt_args.get("out_buckets", 1))
    # Same reasoning: a checkpoint predating factorization has no virtual rows,
    # and building the wrong shape would fail to load the saved weights.
    factorized = bool(ckpt_args.get("factorized", False))
    # A checkpoint trained with threats carries a second transformer, and a
    # model built without one cannot load its state dict at all - which is the
    # good failure. The bad one would be exporting a threat net as arch 3 and
    # silently dropping half its input, so the mismatch is refused below.
    trained_threats = bool(ckpt_args.get("threats", False))
    # A checkpoint trained with the rebuilt head has an l1 of half the input
    # width and an l2 that no other architecture has a place for, so exporting
    # it as anything else cannot even load the state dict - the good failure.
    trained_dual = bool(ckpt_args.get("dual", False))
    psqt_buckets = int(ckpt_args.get("psqt_buckets", 0) or 0)
    l2_out = ckpt_args.get("l2_out", L2_OUT) if trained_dual else 0

    # The architecture follows the checkpoint unless overridden: exporting a
    # bucketed net as arch 1/2 would silently drop every bucket but the first.
    arch = args.arch if args.arch is not None else (
        ARCH_DUAL if trained_dual else
        ARCH_THREATS if trained_threats else
        ARCH_INT8_L1_BUCKETS if buckets > 1 else ARCH_INT8_L1)

    if trained_dual and arch != ARCH_DUAL:
        raise SystemExit(
            f"checkpoint was trained with the arch 5 head but --arch {arch} has no shape "
            f"for it. Use --arch {ARCH_DUAL}.")
    if arch == ARCH_DUAL and not trained_dual:
        raise SystemExit(
            f"--arch {ARCH_DUAL} asks for the dual-activation head but the checkpoint "
            f"was trained without it (retrain with --dual).")

    bucket_capable = arch in (ARCH_INT8_L1_BUCKETS, ARCH_THREATS, ARCH_DUAL)
    if buckets > 1 and not bucket_capable:
        raise SystemExit(
            f"checkpoint has {buckets} output buckets but --arch {arch} cannot represent them. "
            f"Use --arch {ARCH_INT8_L1_BUCKETS}, or retrain with --out-buckets 1.")
    # Refused rather than warned about: an arch 3 export of a threat net loads
    # cleanly in the engine and evaluates with half its input missing, which is
    # the worst possible failure - silent and plausible.
    if trained_threats and arch not in (ARCH_THREATS, ARCH_DUAL):
        raise SystemExit(
            f"checkpoint was trained WITH threat features but --arch {arch} cannot carry them. "
            f"Exporting it that way would produce a net that loads and evaluates wrongly. "
            f"Use --arch {ARCH_THREATS}.")
    if arch == ARCH_THREATS and not trained_threats:  # noqa: E501
        raise SystemExit(
            f"--arch {ARCH_THREATS} asks for a threat transformer but the checkpoint has none.")
    if not bucket_capable:
        buckets = 1
    qa = QA_FOR_ARCH[arch]

    model = NoaNnue(ft_out, l1_out, buckets, factorized, qa=QA_FOR_ARCH[arch],
                    threats=trained_threats, dual=trained_dual, l2_out=l2_out,
                    psqt_buckets=psqt_buckets)
    model.load_state_dict(checkpoint["model"])
    model.clip_weights()

    kind = "int16" if arch == ARCH_INT16_L1 else "int8"
    print(f"exporting arch {arch} ({kind} L1, {buckets} output bucket(s)), "
          f"ft_out={ft_out} l1_out={l1_out} QA={qa} QB={QB}"
          + (" [factorized: virtual features folded in]" if factorized else ""))

    # EmbeddingBag rows are feature-major already. fold_features drops the
    # padding row and, when the net was trained factorized, adds each virtual
    # (piece, square) row into its 32 king-bucket copies - which is exact, so the
    # exported table evaluates every position to the trained net's value.
    ft_w = quantize(model.fold_features(), qa, np.int16, 32767, "ftWeights")
    ft_b = quantize(model.ft_bias, qa, np.int16, 32767, "ftBias")
    # nn.Linear stores weight as [out, in] - exactly the row-per-output layout
    # the C# dot product expects.
    # Both l1 (buckets*l1_out rows) and out (one row per bucket) are already
    # bucket-major in the module, which is exactly the C# payload layout.
    l1_dtype = np.int16 if arch == ARCH_INT16_L1 else np.int8
    l1_w = quantize(model.l1.weight, QB, l1_dtype, 127, "l1Weights")
    l1_b = quantize(model.l1.bias, qa * QB, np.int32, 2**31 - 1, "l1Bias")
    out_w = quantize(model.out.weight.flatten(), QB, np.int16, 127, "outWeights")
    out_b = quantize(model.out.bias, qa * QB, np.int32, 2**31 - 1, "outBias")
    l2_w = l2_b = None
    if arch == ARCH_DUAL:
        l2_w = quantize(model.l2.weight, QB, np.int8, 127, "l2Weights")
        l2_b = quantize(model.l2.bias, qa * QB, np.int32, 2**31 - 1, "l2Bias")

    # Guard the saturation bound with the ACTUAL exported values, not just the
    # nominal limits - a future change to clip_weights must not be able to make
    # the kernel wrong without this failing first.
    if arch != ARCH_INT16_L1:
        # The L1 activations of arch 5 are pairwise products, bounded by
        # 127*127 >> 7 = 126 rather than by QA, but the SECOND layer's
        # activations do reach QA, so QA remains the bound that has to hold.
        max_l1 = int(np.abs(l1_w.astype(np.int32)).max())
        if l2_w is not None:
            max_l1 = max(max_l1, int(np.abs(l2_w.astype(np.int32)).max()))
        worst = 2 * qa * max_l1
        if worst > 32767:
            raise SystemExit(
                f"arch {arch} saturation check FAILED: 2*QA*max|l1_w| = {worst:,} > 32,767. "
                f"The int8 kernel would saturate. Lower QA or tighten l1 weight clipping.")
        print(f"  saturation check OK: worst int16 lane = {worst:,} / 32,767 "
              f"({100.0 * worst / 32767:.1f}% of headroom used)")

    # ACCUMULATOR HEADROOM. The engine keeps the perspective accumulator in
    # int16: ftBias plus one row per active feature, at most MAX_ACTIVE of them.
    # Folding a factorized net adds two learned rows into every real row, so the
    # magnitudes it exports are larger than the ones the trainer clipped. Bound
    # it from the ACTUAL exported values instead of trusting clip_weights: take
    # the MAX_ACTIVE largest magnitudes per lane, which is an upper bound on any
    # reachable position.
    biggest = np.partition(np.abs(ft_w.astype(np.int32)), -MAX_ACTIVE, axis=0)[-MAX_ACTIVE:]
    acc_worst = int((np.abs(ft_b.astype(np.int32)) + biggest.sum(axis=0)).max())
    if acc_worst > 32767:
        raise SystemExit(
            f"accumulator headroom check FAILED: worst int16 accumulator lane = "
            f"{acc_worst:,} > 32,767. The engine's accumulator would overflow. "
            f"Tighten clip_weights (model.py) and retrain or re-export.")
    print(f"  accumulator headroom OK: worst lane = {acc_worst:,} / 32,767 "
          f"({100.0 * acc_worst / 32767:.1f}% used, bound over {MAX_ACTIVE} active features)")

    # How much of the transformer survives quantization. 89.8% of ds1e60's
    # weights rounded to zero, which is what motivated factorization in the
    # first place, so the number belongs in every export from now on.
    zero_pct = 100.0 * float((ft_w == 0).mean())
    dead_rows = int((np.abs(ft_w.astype(np.int32)).sum(axis=1) == 0).sum())
    print(f"  feature transformer: {zero_pct:.1f}% of weights quantize to zero, "
          f"{dead_rows:,} of {INPUT_SIZE:,} features dead, max |q| = "
          f"{int(np.abs(ft_w.astype(np.int32)).max())}")

    blocks = [ft_w.tobytes(), ft_b.tobytes(),
              l1_w.tobytes(), l1_b.tobytes()]
    if arch == ARCH_DUAL:
        blocks += [l2_w.tobytes(), l2_b.tobytes()]
    blocks += [out_w.tobytes(), out_b.tobytes()]

    if trained_threats:
        # Same quantisation scale as the HalfKA transformer, because both sum
        # into ONE int16 accumulator in the engine: a different scale would put
        # their sum off the grid that accumulator holds.
        th_w = quantize(model.fold_threats(), qa, np.int16, 32767, "threatWeights")

        # The accumulator bound has to be recomputed with BOTH transformers in
        # it, and this is the check most likely to fire on a real threat net: a
        # position activates up to MAX_ACTIVE HalfKA features AND up to 128
        # threat features, so the worst case is the sum of both tails, not
        # either alone. Bounded from the exported values rather than trusted
        # from the clipping, exactly as the HalfKA bound above is.
        th_top = np.partition(np.abs(th_w.astype(np.int32)),
                              -THREAT_MAX_ACTIVE, axis=0)[-THREAT_MAX_ACTIVE:]
        both = int((np.abs(ft_b.astype(np.int32)) + biggest.sum(axis=0)
                    + th_top.sum(axis=0)).max())
        if both > 32767:
            raise SystemExit(
                f"accumulator headroom check FAILED with threats: worst int16 lane = "
                f"{both:,} > 32,767. Both transformers sum into one accumulator, so "
                f"the bound is HalfKA's tail plus the threat tail. Tighten clipping "
                f"and re-export.")
        print(f"  accumulator headroom WITH threats OK: worst lane = {both:,} / 32,767 "
              f"({100.0 * both / 32767:.1f}% used, bound over {MAX_ACTIVE} HalfKA + "
              f"{THREAT_MAX_ACTIVE} threat features)")

        zero_t = 100.0 * float((th_w == 0).mean())
        dead_t = int((np.abs(th_w.astype(np.int32)).sum(axis=1) == 0).sum())
        print(f"  threat transformer: {zero_t:.1f}% of weights quantize to zero, "
              f"{dead_t:,} of {THREAT_INPUT_SIZE:,} features dead, max |q| = "
              f"{int(np.abs(th_w.astype(np.int32)).max())}")

        blocks.append(th_w.tobytes())

    if psqt_buckets > 0:
        # Appended LAST, like the threat block: readers that stop early still
        # see a coherent file. Stored as int32 at OUTPUT_SCALE so the engine's
        # (sum_stm - sum_opp) / 2 lands directly in centipawns; the rounding
        # step is 1/400 of a raw output unit, far below training noise, which
        # is why this head trains without fake quantization.
        ps = np.round(model.fold_psqt().numpy() * OUTPUT_SCALE)
        worst = float(np.abs(ps).max())
        if worst >= 2**31:
            raise SystemExit(f"psqt weight overflows int32: {worst}")
        ps_w = ps.astype(np.int32)
        print(f"  psqt head: {psqt_buckets} bucket(s), max |q| = {int(worst):,}, "
              f"{100.0 * float((ps_w == 0).mean()):.1f}% zero")
        print("  NOTE: verify_export does not reproduce the psqt lane yet; the C#")
        print("  parity test covers the accumulator, but extend verify_export")
        print("  before the first SPRT with a psqt net.")
        blocks.append(ps_w.tobytes())

    payload = b"".join(blocks)
    sha = hashlib.sha256(payload).digest()

    if arch == ARCH_DUAL:
        flags = ARCH_FLAG_PAIRWISE_FT | (ARCH_FLAG_THREATS if trained_threats else 0)
        header = struct.pack(
            "<8s I I I i i i H H H H i H H Q 32s",
            MAGIC, FORMAT_VERSION_2, FEATURE_SCHEMA_ID, arch,
            INPUT_SIZE, ft_out, l1_out,
            qa, QB, int(OUTPUT_SCALE), buckets,
            l2_out, 0, flags,
            len(payload), sha)
        assert len(header) == HEADER_BYTES_V2
    elif psqt_buckets > 0:
        # A psqt head needs the version 2 header for its bucket count at
        # offset 44. l2 stays 0 and no flags are set: this is arch 1/2/3 plus
        # a lane, not arch 5.
        header = struct.pack(
            "<8s I I I i i i H H H H i H H Q 32s",
            MAGIC, FORMAT_VERSION_2, FEATURE_SCHEMA_ID, arch,
            INPUT_SIZE, ft_out, l1_out,
            qa, QB, int(OUTPUT_SCALE), buckets if bucket_capable else 0,
            0, psqt_buckets, 0,
            len(payload), sha)
        assert len(header) == HEADER_BYTES_V2
    else:
        # Offset 38 was padding before v4.2.0; arch 1/2 keep writing 0 there,
        # which is what makes every legacy file load unchanged.
        header = struct.pack(
            "<8s I I I i i i H H H H Q 32s",
            MAGIC, FORMAT_VERSION, FEATURE_SCHEMA_ID, arch,
            INPUT_SIZE, ft_out, l1_out,
            qa, QB, int(OUTPUT_SCALE), buckets if bucket_capable else 0,
            len(payload), sha)
        assert len(header) == HEADER_BYTES_V1

    with open(args.out, "wb") as f:
        f.write(header)
        f.write(payload)

    print(f"exported {args.out}")
    print(f"  payload: {len(payload):,} bytes  sha256: {sha.hex()}")


if __name__ == "__main__":
    main()
