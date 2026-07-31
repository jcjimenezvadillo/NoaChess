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

from model import NoaNnue, INPUT_SIZE, FT_OUT, L1_OUT, QA, QB, OUTPUT_SCALE

MAGIC = b"NOANNUE1"
FORMAT_VERSION = 1
FEATURE_SCHEMA_ID = 2

ARCH_INT16_L1 = 1
ARCH_INT8_L1 = 2

# Activation scale per architecture. Arch 2 is capped by the saturation bound
# proved above; the C# loader enforces the same number.
QA_FOR_ARCH = {ARCH_INT16_L1: QA, ARCH_INT8_L1: 127}


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
    parser.add_argument("--arch", type=int, default=ARCH_INT8_L1,
                        choices=[ARCH_INT16_L1, ARCH_INT8_L1],
                        help="1 = legacy int16 L1 (QA=255), 2 = int8 L1 (QA=127, default)")
    args = parser.parse_args()

    arch = args.arch
    qa = QA_FOR_ARCH[arch]

    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    ckpt_args = checkpoint.get("args", {})
    ft_out = ckpt_args.get("ft_out", FT_OUT)
    l1_out = ckpt_args.get("l1_out", L1_OUT)
    model = NoaNnue(ft_out, l1_out)
    model.load_state_dict(checkpoint["model"])
    model.clip_weights()

    print(f"exporting arch {arch} "
          f"({'int8' if arch == ARCH_INT8_L1 else 'int16'} L1), "
          f"ft_out={ft_out} l1_out={l1_out} QA={qa} QB={QB}")

    # EmbeddingBag rows are feature-major already; drop the padding row.
    ft_w = quantize(model.ft.weight[:INPUT_SIZE], qa, np.int16, 32767, "ftWeights")
    ft_b = quantize(model.ft_bias, qa, np.int16, 32767, "ftBias")
    # nn.Linear stores weight as [out, in] — exactly the row-per-output layout
    # the C# dot product expects.
    l1_dtype = np.int8 if arch == ARCH_INT8_L1 else np.int16
    l1_w = quantize(model.l1.weight, QB, l1_dtype, 127, "l1Weights")
    l1_b = quantize(model.l1.bias, qa * QB, np.int32, 2**31 - 1, "l1Bias")
    out_w = quantize(model.out.weight.flatten(), QB, np.int16, 127, "outWeights")
    out_b = int(np.round(model.out.bias.item() * qa * QB))

    # Guard the saturation bound with the ACTUAL exported values, not just the
    # nominal limits — a future change to clip_weights must not be able to make
    # the kernel wrong without this failing first.
    if arch == ARCH_INT8_L1:
        worst = 2 * qa * int(np.abs(l1_w.astype(np.int32)).max())
        if worst > 32767:
            raise SystemExit(
                f"arch 2 saturation check FAILED: 2*QA*max|l1_w| = {worst:,} > 32,767. "
                f"The int8 kernel would saturate. Lower QA or tighten l1 weight clipping.")
        print(f"  saturation check OK: worst int16 lane = {worst:,} / 32,767 "
              f"({100.0 * worst / 32767:.1f}% of headroom used)")

    payload = b"".join([
        ft_w.tobytes(), ft_b.tobytes(),
        l1_w.tobytes(), l1_b.tobytes(),
        out_w.tobytes(), struct.pack("<i", out_b),
    ])
    sha = hashlib.sha256(payload).digest()

    header = struct.pack(
        "<8s I I I i i i H H H H Q 32s",
        MAGIC, FORMAT_VERSION, FEATURE_SCHEMA_ID, arch,
        INPUT_SIZE, ft_out, l1_out,
        qa, QB, int(OUTPUT_SCALE), 0,
        len(payload), sha)
    assert len(header) == 80

    with open(args.out, "wb") as f:
        f.write(header)
        f.write(payload)

    print(f"exported {args.out}")
    print(f"  payload: {len(payload):,} bytes  sha256: {sha.hex()}")


if __name__ == "__main__":
    main()
