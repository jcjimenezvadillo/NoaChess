namespace NoaChess.Engine.Evaluation.Nnue;

// Header of the .noannue model file. Binary layout (little-endian):
//
//   offset size  field
//   0      8     magic "NOANNUE1" (ASCII)
//   8      4     format version        (u32)
//   12     4     feature schema id     (u32)  must match NnueFeatureIndex
//   16     4     architecture id       (u32)  frozen layer topology
//   20     4     ft inputs             (u32)  40960 for schema 1
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
//   l1Weights  int16[l1Outputs * 2*ftOutputs] row per OUTPUT (cache-friendly dot)
//   l1Bias     int32[l1Outputs]
//   outWeights int16[l1Outputs]
//   outBias    int32
//
// Any mismatch (magic, version, schema, architecture, dimensions, length or
// SHA-256) rejects the model: a silently wrong net is worse than no net.
public sealed class NnueModelHeader
{
    public const string Magic = "NOANNUE1";
    public const uint SupportedFormatVersion = 1;
    public const uint SupportedArchitectureId = 1;
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
