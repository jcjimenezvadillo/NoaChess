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
//   38     2     output buckets        (u16)  arch 3+; 0 or 1 means "unbucketed"
//   40     8     payload length        (u64)
//   48     32    payload SHA-256
//   80     ...   payload
//
// Payload (little-endian, in this order):
//   ftWeights  int16[ftInputs * ftOutputs]   row per feature index
//   ftBias     int16[ftOutputs]
//   l1Weights  ARCH 1: int16[l1Outputs * 2*ftOutputs]  row per OUTPUT
//              ARCH 2: int8 [l1Outputs * 2*ftOutputs]  row per OUTPUT
//              ARCH 3: int8 [buckets * l1Outputs * 2*ftOutputs]  bucket-major
//   l1Bias     int32[buckets * l1Outputs]      (buckets = 1 for arch 1/2)
//   outWeights int16[buckets * l1Outputs]
//   outBias    int32[buckets]
//
// ---- ARCHITECTURE IDS ----
//
// ARCH 1 (legacy, v3.x): L1 weights int16, activations clamped to [0, QA=255]
// and held as int16. The dot product runs on VPMADDWD. Every shipped net up to
// gen7 uses this and it stays fully supported - a format change must never
// strand a net that is currently playing.
//
// ARCH 2 (v4.0.0): L1 weights int8, activations clamped to [0, QA=127] and
// packed to unsigned bytes. The dot product runs on VPMADDUBSW + VPMADDWD,
// which halves weight memory traffic and doubles per-element throughput on
// AVX2 without VNNI - the target CPU is Zen+, so VPDPBUSD is not available and
// the int16 intermediate cannot be skipped.
//
// WHY QA MUST DROP TO 127 IN ARCH 2 - this is a correctness constraint, not a
// tuning choice. VPMADDUBSW computes a0*w0 + a1*w1 into an int16 lane, and
// int16 SATURATES. With unsigned-byte activations and signed-byte weights:
//     QA=255: |255*127 + 255*127| = 64,770  > 32,767  -> saturates, WRONG
//     QA=127: |127*127 + 127*127| = 32,258  < 32,767  -> exact, always
// So arch 2 is bit-exact against its own scalar reference by construction, for
// every possible input. The cost is one bit of activation resolution; the FT
// weights themselves lose nothing, because export already clipped L1 weights
// to +/-127 even while storing them as int16.
//
// ARCH 3 (v4.2.0): int8 L1 as in arch 2, plus OUTPUT BUCKETS. The head is
// replicated per bucket and the bucket is chosen from the piece count, so the
// network gets a specialised readout for each phase of the game instead of one
// linear map that has to serve a 32-piece opening and a 4-piece ending alike.
//
// WHY THIS IS ALMOST FREE AT RUNTIME. Only ONE bucket is evaluated per call -
// the others are never touched - so the arithmetic per evaluation is exactly
// the arch-2 cost. What grows is the WEIGHT TABLE, by the bucket count, and
// only for the head: at ft=128/l1=32 the L1 matrix goes from 16 KB to 128 KB
// against a 5.5 MB feature transformer. That is the best capacity-per-cost
// trade available in this architecture, which is why it ships before any width
// increase.
//
// BUCKET SELECTION must be identical in the engine and the trainer:
//     bucket = clamp((pieceCount - 1) * buckets / 32, 0, buckets - 1)
// With 8 buckets this is the familiar (pieceCount - 1) / 4, and it generalises
// to any bucket count. Piece count includes both kings, so it runs 2..32.
//
// Any mismatch (magic, version, schema, architecture, dimensions, length or
// SHA-256) rejects the model: a silently wrong net is worse than no net.
public sealed class NnueModelHeader
{
    public const string Magic = "NOANNUE1";
    public const uint SupportedFormatVersion = 1;

    // ---- FORMAT VERSION 2 (v5.2.0, architecture 5) ----
    //
    // Version 1's header is 80 fixed bytes with no spare room before the
    // payload length at offset 40, so architecture 5 - which has a second
    // hidden layer and a flag word - could not be described without moving a
    // field. Moving one would strand every net currently playing, so version 2
    // is a strict SUPERSET: bytes 0..39 keep their meaning and their offsets
    // exactly, and the three new fields occupy 40..47, pushing the payload
    // length and the SHA down by 8 bytes.
    //
    //   offset size  field                       (version 2 only)
    //   40     4     l2 outputs           (u32)  second hidden layer width
    //   44     2     psqt buckets         (u16)  0 = no psqt head
    //   46     2     flags                (u16)  see ArchFlag* below
    //   48     8     payload length       (u64)
    //   56     32    payload SHA-256
    //   88     ...   payload
    //
    // A version 1 file is still read by the version 1 path, byte for byte.
    public const uint FormatVersionTwo = 2;
    public const int HeaderSizeV2 = 88;

    // Feature transformer outputs are read PAIRWISE: the two halves of the
    // accumulator are multiplied element by element. Always set for arch 5.
    public const ushort ArchFlagPairwiseFt = 1 << 0;
    // The file carries a threat transformer block, as arch 4 does.
    public const ushort ArchFlagThreats = 1 << 1;
    // The file carries the COARSE threat lane: 144 x ftOut int16 rows on the
    // qa grid, appended LAST. Computed at evaluation time from bitboards -
    // per evaluation, not per node, which is the whole point of the lane.
    // Forbidden together with threats, psqt or arch 5: no combined payload
    // shape exists and the trainer refuses the combination too.
    public const ushort ArchFlagCoarse = 1 << 2;
    public const int CoarseRows = 144;

    // Legacy int16 L1 (every net through gen7). Still loadable, still played.
    public const uint ArchitectureInt16L1 = 1;
    // v4.0.0 int8 L1. Requires QA <= 127 (see the saturation proof above).
    public const uint ArchitectureInt8L1 = 2;
    // v4.2.0 int8 L1 + output buckets selected by piece count.
    public const uint ArchitectureInt8L1Buckets = 3;

    // ARCH 4: everything arch 3 has, plus a SECOND feature transformer fed by
    // threat features, summed into the same accumulator.
    //
    // THE HEADER DOES NOT GROW, and that is a deliberate choice rather than a
    // shortcut. It is 80 fixed bytes with the payload SHA covering everything
    // after them, so adding a "threat inputs" field would change the offset of
    // every field behind it and strand every net currently playing. It is also
    // unnecessary: the feature schema id already pins what a feature MEANS, and
    // arch 4 means "HalfKAv2_hm plus the threat set", whose dimension is a
    // constant of that schema. A number that can only ever hold one value does
    // not belong in a file.
    //
    // Payload gains one block, appended after the existing ones so an arch 1-3
    // reader that stops early still sees a coherent file:
    //     threatFtWeights int16[60720 * ftOutputs]
    //
    // It is large - 31 MB at ftOutputs 256 against a 5.5 MB net today - because
    // 60,720 rows is what the feature set costs. There is no bias block: both
    // transformers sum into one accumulator, so a second bias would only be
    // added to the first and the trainer folds it there at export.
    public const uint ArchitectureThreats = 4;

    // ARCH 5 (v5.2.0): the head rebuilt to match what a modern reference
    // evaluation actually computes. Arch 1-4 run ONE linear layer with ONE
    // clipped ReLU on top of the accumulator; that is the whole non-linearity
    // of the evaluation. Arch 5 adds three things, none of which needs a wider
    // feature transformer:
    //
    // 1. PAIRWISE FEATURE TRANSFORMER READ. Instead of clipping all ftOutputs
    //    values and feeding them forward, the accumulator is split in half and
    //    multiplied element by element:
    //        act[j] = clamp(acc[j], 0, QA) * clamp(acc[j + H], 0, QA) / QA
    //    with H = ftOutputs / 2. That introduces a QUADRATIC interaction
    //    between features at the very first layer, which a stack of clipped
    //    linear maps cannot express at all, and it HALVES the L1 input width -
    //    so it makes the head cheaper while making it stronger.
    //
    // 2. DUAL ACTIVATION. Each hidden layer emits both its clipped activation
    //    and the SQUARE of it, concatenated:
    //        c = clamp(h / QB, 0, QA)      s = c * c / QA
    //    The next layer therefore sees a second-order term for every unit at
    //    the cost of one multiply each. Squaring is the cheapest useful
    //    non-linearity there is and it is what the reference uses at both of
    //    its hidden layers.
    //
    // 3. A SECOND HIDDEN LAYER WITH A SKIP CONNECTION. The output reads the
    //    activations of BOTH hidden layers, not just the last one, so the
    //    second layer only has to learn what the first one could not - the
    //    first layer's contribution is never bottlenecked through it.
    //
    // Plus a plain linear bypass taken from the same place: the last two L1
    // PRE-activations are added to the output as (h[n-2] - h[n-1]). Their units
    // are already QA*QB, identical to the output accumulator's, so the term
    // ports with no rescaling at all - two neurons that can carry an unbounded
    // linear score past every clamp in the network.
    //
    // NET COST AT ft=128 / l1=32 / l2=32, in multiply-accumulates per
    // evaluation, against arch 3:
    //     arch 3   L1 32 x 256 = 8192   out    32            = 8224
    //     arch 5   L1 32 x 128 = 4096   L2 32 x 64 = 2048
    //              out 128              pairwise 128, squares 64
    //                                                        = 6464
    // The richer network is CHEAPER, because the pairwise read pays for the
    // rest. The feature transformer, which the profiler puts at 73.8% of the
    // cost, is untouched.
    public const uint ArchitectureDualActivation = 5;

    // Size of the threat weight block, which the loader checks the file against
    // before reading a byte of it. Kept here so the trainer's expectation and
    // the engine's are one expression.
    public static long ThreatWeightBytes(int threatInputs, int ftOutputs)
        => (long)threatInputs * ftOutputs * sizeof(short);

    // Activations must fit an unsigned byte AND keep VPMADDUBSW exact.
    public const int MaxQaForInt8L1 = 127;

    // Sanity bound on the bucket count; 8 is the value the trainer defaults to.
    public const int MaxOutputBuckets = 32;

    public const int HeaderSize = 80;

    // The single definition of bucket selection. Duplicating this formula is
    // how a trainer and an engine drift apart silently, so both sides call one
    // function and the tests pin its values.
    public static int BucketForPieceCount(int pieceCount, int buckets)
    {
        if (buckets <= 1)
            return 0;
        int bucket = (pieceCount - 1) * buckets / 32;
        return Math.Clamp(bucket, 0, buckets - 1);
    }

    // Size of one bucket's head, in bytes, for architecture 5. Kept next to the
    // layout it describes so the loader's expectation and the exporter's are
    // one expression rather than two that must agree.
    public static long ArchFiveHeadBytes(int ftOutputs, int l1Outputs, int l2Outputs)
        // The L1 input is 2 * H values (H per perspective), one byte each, so
        // the "* 2" below counts PERSPECTIVES and not bytes.
        => (long)l1Outputs * (ftOutputs / 2) * 2      // l1Weights int8
         + (long)l1Outputs * 4                        // l1Bias int32
         + (long)l2Outputs * (2 * l1Outputs)          // l2Weights int8
         + (long)l2Outputs * 4                        // l2Bias int32
         + (long)(2 * l1Outputs + 2 * l2Outputs) * 2  // outWeights int16
         + 4;                                         // outBias int32

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
