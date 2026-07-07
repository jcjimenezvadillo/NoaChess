# Exports a trained checkpoint to the .noannue binary the C# engine loads.
# The quantization contract and byte layout mirror NnueNetwork.cs /
# NnueModelLoader.cs exactly:
#
#   ftWeights  = round(w * QA)        int16
#   ftBias     = round(b * QA)        int16
#   l1Weights  = round(w * QB)        int16   (row per OUTPUT neuron)
#   l1Bias     = round(b * QA * QB)   int32
#   outWeights = round(w * QB)        int16
#   outBias    = round(b * QA * QB)   int32
#
# Usage:
#   python export_model.py --checkpoint checkpoints/run1.pt --out ../../models/nnue/noa-v2.noannue

import argparse
import hashlib
import struct

import numpy as np
import torch

from model import NoaNnue, INPUT_SIZE, FT_OUT, L1_OUT, QA, QB, OUTPUT_SCALE

MAGIC = b"NOANNUE1"
FORMAT_VERSION = 1
FEATURE_SCHEMA_ID = 1
ARCHITECTURE_ID = 1


def quantize(tensor, scale, dtype, limit):
    q = np.round(tensor.detach().numpy() * scale)
    clipped = np.clip(q, -limit, limit)
    if (q != clipped).any():
        print(f"  warning: {(q != clipped).sum()} weights clipped to +/-{limit}")
    return clipped.astype(dtype)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=False)
    model = NoaNnue()
    model.load_state_dict(checkpoint["model"])
    model.clip_weights()

    # EmbeddingBag rows are feature-major already; drop the padding row.
    ft_w = quantize(model.ft.weight[:INPUT_SIZE], QA, np.int16, 32767)
    ft_b = quantize(model.ft_bias, QA, np.int16, 32767)
    # nn.Linear stores weight as [out, in] — exactly the row-per-output
    # layout the C# dot product expects.
    l1_w = quantize(model.l1.weight, QB, np.int16, 127)
    l1_b = quantize(model.l1.bias, QA * QB, np.int32, 2**31 - 1)
    out_w = quantize(model.out.weight.flatten(), QB, np.int16, 127)
    out_b = int(np.round(model.out.bias.item() * QA * QB))

    payload = b"".join([
        ft_w.tobytes(), ft_b.tobytes(),
        l1_w.tobytes(), l1_b.tobytes(),
        out_w.tobytes(), struct.pack("<i", out_b),
    ])
    sha = hashlib.sha256(payload).digest()

    header = struct.pack(
        "<8s I I I i i i H H H H Q 32s",
        MAGIC, FORMAT_VERSION, FEATURE_SCHEMA_ID, ARCHITECTURE_ID,
        INPUT_SIZE, FT_OUT, L1_OUT,
        QA, QB, int(OUTPUT_SCALE), 0,
        len(payload), sha)
    assert len(header) == 80

    with open(args.out, "wb") as f:
        f.write(header)
        f.write(payload)

    print(f"exported {args.out}")
    print(f"  payload: {len(payload):,} bytes  sha256: {sha.hex()}")


if __name__ == "__main__":
    main()
