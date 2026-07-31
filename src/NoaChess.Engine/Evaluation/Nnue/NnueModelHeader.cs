namespace NoaChess.Engine.Evaluation.Nnue;

// Header of the .noannue model file. Binary layout (little-endian):
//
//   offset size  field
//   0      8     magic "NOANNUE1" (ASCII)
//   8      4     format version        (u32)
//   12     4     feature schema id     (u32)  must match NnueFeatureIndex
//   16     4     architecture id       (u32)  frozen layer topology
//   20     4     ft inputs             (u32)  22528 for schema 2 (HalfKAv2_hm)
//   24     4     ft outputs            (u32)  accumulator width
//   28     4     l1 outputs            (u32)  hidden layer width
//   32     2     QA activation scale   (u16)
//   34     2     QB weight scale       (u16)
//   36     2     output scale (cp)     (u16)
//   38     2     padding               (u16)
//   40     8     payload length        (u64)
//   48     32    payload SHA-256
//   80     ...   payload
//
// Payload (little-endian, in this order):
//   ftWeights  int16[ftInputs * ftOutputs]   row per feature index
//   ftBias     int16[ftOutputs]
//   l1Weights  ARCH 1: int16[l1Outputs * 2*ftOutputs]  row per OUTPUT
//              ARCH 2: int8 [l1Outputs * 2*ftOutputs]  row per OUTPUT
//   l1Bias     int32[l1Outputs]
//   outWeights int16[l1Outputs]
//   outBias    int32
//
// ---- ARCHITECTURE IDS ----
//
// ARCH 1 (legacy, v3.x): L1 weights int16, activations clamped to [0, QA=255]
// and held as int16. The dot product runs on VPMADDWD. Every shipped net up to
// gen7 uses this and it stays fully supported — a format change must never
// strand a net that is currently playing.
//
// ARCH 2 (v4.0.0): L1 weights int8, activations clamped to [0, QA=127] and
// packed to unsigned bytes. The dot product runs on VPMADDUBSW + VPMADDWD,
// which halves weight memory traffic and doubles per-element throughput on
// AVX2 without VNNI — the target CPU is Zen+, so VPDPBUSD is not available and
// the int16 intermediate cannot be skipped.
//
// WHY QA MUST DROP TO 127 IN ARCH 2 — this is a correctness constraint, not a
// tuning choice. VPMADDUBSW computes a0*w0 + a1*w1 into an int16 lane, and
// int16 SATURATES. With unsigned-byte activations and signed-byte weights:
//     QA=255: |255*127 + 255*127| = 64,770  > 32,767  -> saturates, WRONG
//     QA=127: |127*127 + 127*127| = 32,258  < 32,767  -> exact, always
// So arch 2 is bit-exact against its own scalar reference by construction, for
// every possible input. The cost is one bit of activation resolution; the FT
// weights themselves lose nothing, because export already clipped L1 weights
// to +/-127 even while storing them as int16.
//
// Any mismatch (magic, version, schema, architecture, dimensions, length or
// SHA-256) rejects the model: a silently wrong net is worse than no net.
public sealed class NnueModelHeader
{
    public const string Magic = "NOANNUE1";
    public const uint SupportedFormatVersion = 1;

    // Legacy int16 L1 (every net through gen7). Still loadable, still played.
    public const uint ArchitectureInt16L1 = 1;
    // v4.0.0 int8 L1. Requires QA <= 127 (see the saturation proof above).
    public const uint ArchitectureInt8L1 = 2;

    // Activations must fit an unsigned byte AND keep VPMADDUBSW exact.
    public const int MaxQaForInt8L1 = 127;

    public const int HeaderSize = 80;

    public uint FormatVersion;
    public uint FeatureSchemaId;
    public uint ArchitectureId;
    public int FtInputs;
    public int FtOutputs;
    public int L1Outputs;
    public ushort QA;
    public ushort QB;
    public ushort OutputScale;
    public ulong PayloadLength;
    public byte[] PayloadSha256 = new byte[32];
}
