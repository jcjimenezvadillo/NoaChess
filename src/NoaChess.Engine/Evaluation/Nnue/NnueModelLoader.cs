using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NoaChess.Engine.Evaluation.Nnue;

// Parses and validates a .noannue model file (see NnueModelHeader for the
// layout). Validation is strict: magic, version, schema, architecture,
// dimensions, payload length and SHA-256 must all match - a corrupt or
// incompatible model is rejected with a descriptive error instead of being
// allowed to play nonsense chess.
public static class NnueModelLoader
{
    // Loads a model or explains why it cannot be loaded.
    public static bool TryLoad(string path, out NnueNetwork? network, out string error)
    {
        network = null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            error = $"cannot read '{path}': {ex.Message}";
            return false;
        }

        return TryParse(bytes, out network, out error);
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, out NnueNetwork? network, out string error)
    {
        network = null;

        if (bytes.Length < NnueModelHeader.HeaderSize)
        {
            error = "file too small to contain a header";
            return false;
        }

        // ---- Header ----
        if (!bytes[..8].SequenceEqual(Encoding.ASCII.GetBytes(NnueModelHeader.Magic)))
        {
            error = "bad magic (not a NOANNUE model)";
            return false;
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        uint schema = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint arch = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        int ftInputs = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        int ftOutputs = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        int l1Outputs = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);
        ushort qa = BinaryPrimitives.ReadUInt16LittleEndian(bytes[32..]);
        ushort qb = BinaryPrimitives.ReadUInt16LittleEndian(bytes[34..]);
        ushort outputScale = BinaryPrimitives.ReadUInt16LittleEndian(bytes[36..]);
        // Offset 38 was padding before v4.2.0, so legacy files read 0 here and
        // are treated as unbucketed - old models keep loading unchanged.
        ushort headerBuckets = BinaryPrimitives.ReadUInt16LittleEndian(bytes[38..]);

        if (version != NnueModelHeader.SupportedFormatVersion
            && version != NnueModelHeader.FormatVersionTwo)
        {
            error = $"unsupported format version {version}";
            return false;
        }

        // Bytes 0..39 mean the same thing in both versions; only the tail moves.
        // Version 2 inserts l2 outputs, psqt buckets and a flag word before the
        // payload length, so everything from offset 40 on shifts by 8 bytes.
        bool v2 = version == NnueModelHeader.FormatVersionTwo;
        int headerSize = v2 ? NnueModelHeader.HeaderSizeV2 : NnueModelHeader.HeaderSize;
        if (bytes.Length < headerSize)
        {
            error = "file too small to contain a header";
            return false;
        }

        int l2Outputs = v2 ? BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]) : 0;
        ushort psqtBuckets = v2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[44..]) : (ushort)0;
        ushort flags = v2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[46..]) : (ushort)0;
        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(v2 ? 48 : 40)..]);
        ReadOnlySpan<byte> expectedSha = v2 ? bytes[56..88] : bytes[48..80];

        // Nothing reads a psqt head yet. Rejecting a file that declares one is
        // the honest response: reading it as if it had none would evaluate a
        // different network from the one that was trained.
        if (psqtBuckets != 0)
        {
            error = $"this build does not implement the psqt head (header declares {psqtBuckets} buckets)";
            return false;
        }
        if (schema != NnueFeatureIndex.FeatureSchemaId)
        {
            error = $"feature schema {schema} does not match engine schema {NnueFeatureIndex.FeatureSchemaId}";
            return false;
        }
        bool int8 = arch is NnueModelHeader.ArchitectureInt8L1
                         or NnueModelHeader.ArchitectureInt8L1Buckets
                         or NnueModelHeader.ArchitectureThreats
                         or NnueModelHeader.ArchitectureDualActivation;
        if (arch != NnueModelHeader.ArchitectureInt16L1 && !int8)
        {
            error = $"unsupported architecture id {arch}";
            return false;
        }
        bool archFive = arch == NnueModelHeader.ArchitectureDualActivation;
        if (archFive && !v2)
        {
            error = "architecture 5 requires format version 2 (it has fields version 1 cannot hold)";
            return false;
        }
        if (!archFive && (l2Outputs != 0 || flags != 0))
        {
            error = $"architecture {arch} cannot carry a second hidden layer or arch flags";
            return false;
        }
        if (archFive)
        {
            if ((flags & NnueModelHeader.ArchFlagPairwiseFt) == 0)
            {
                error = "architecture 5 reads the feature transformer pairwise; the flag is not set";
                return false;
            }
            if (l2Outputs <= 0 || l2Outputs > 4096)
            {
                error = $"implausible second hidden layer width {l2Outputs}";
                return false;
            }
            // The pairwise read multiplies acc[j] by acc[j + ftOutputs/2], so an
            // odd width has no second half to pair with.
            if (ftOutputs % 2 != 0)
            {
                error = $"architecture 5 needs an even ft width to pair (got {ftOutputs})";
                return false;
            }
        }
        // The int8 architectures pack activations into unsigned bytes and
        // multiply them by signed bytes through VPMADDUBSW, whose int16 lane
        // saturates above 32,767. QA <= 127 makes |a0*w0 + a1*w1| <= 2*127*127
        // = 32,258, i.e. exact for every possible input. A model claiming int8
        // with a larger QA would evaluate silently wrong positions.
        if (int8 && qa > NnueModelHeader.MaxQaForInt8L1)
        {
            error = $"architecture {arch} requires QA <= {NnueModelHeader.MaxQaForInt8L1} "
                  + $"to keep the int8 dot product free of int16 saturation (got QA={qa})";
            return false;
        }

        // Arch 3 and arch 4 may declare buckets; anything else must be
        // unbucketed, or the payload size and the head indexing disagree.
        bool bucketed = arch is NnueModelHeader.ArchitectureInt8L1Buckets
                             or NnueModelHeader.ArchitectureThreats
                             or NnueModelHeader.ArchitectureDualActivation;
        int buckets = bucketed ? (headerBuckets == 0 ? 1 : headerBuckets) : 1;
        if (!bucketed && headerBuckets > 1)
        {
            error = $"architecture {arch} does not support output buckets (header declares {headerBuckets})";
            return false;
        }
        if (buckets < 1 || buckets > NnueModelHeader.MaxOutputBuckets)
        {
            error = $"implausible output bucket count {buckets}";
            return false;
        }
        if (ftInputs != NnueFeatureIndex.InputSize)
        {
            error = $"ft input size {ftInputs} does not match schema input size {NnueFeatureIndex.InputSize}";
            return false;
        }
        if (ftOutputs <= 0 || ftOutputs > 4096 || l1Outputs <= 0 || l1Outputs > 4096)
        {
            error = "implausible layer dimensions";
            return false;
        }
        if (qa == 0 || qb == 0 || outputScale == 0)
        {
            error = "quantization scales must be non-zero";
            return false;
        }

        // ---- Payload ----
        // Everything after the feature transformer is replicated per bucket.
        int l1WeightBytes = int8 ? 1 : 2;
        long transformerBytes =
            (long)ftInputs * ftOutputs * 2                            // ftWeights int16
            + ftOutputs * 2;                                          // ftBias int16

        long expectedPayload = archFive
            ? transformerBytes
              + buckets * NnueModelHeader.ArchFiveHeadBytes(ftOutputs, l1Outputs, l2Outputs)
            : transformerBytes
              + (long)buckets * l1Outputs * 2 * ftOutputs * l1WeightBytes // l1Weights
              + (long)buckets * l1Outputs * 4                           // l1Bias int32
              + (long)buckets * l1Outputs * 2                           // outWeights int16
              + (long)buckets * 4;                                      // outBias int32

        // Arch 4 appends the threat transformer. Its row count is NOT read from
        // the file: it is a constant of the feature schema, so a file whose size
        // implies a different one is rejected here rather than being read into a
        // net that would then evaluate nonsense.
        //
        // Arch 5 carries the same block when its threat flag is set, so the two
        // improvements compose: a net can have the threat features AND the
        // rebuilt head without a third architecture id.
        bool hasThreats = arch == NnueModelHeader.ArchitectureThreats
                       || (archFive && (flags & NnueModelHeader.ArchFlagThreats) != 0);
        if (hasThreats)
            expectedPayload += NnueModelHeader.ThreatWeightBytes(
                ThreatFeatureIndex.InputSize, ftOutputs);

        if ((long)payloadLength != expectedPayload)
        {
            error = $"payload length {payloadLength} does not match dimensions (expected {expectedPayload})";
            return false;
        }
        if (bytes.Length != headerSize + expectedPayload)
        {
            error = "file length does not match header";
            return false;
        }

        ReadOnlySpan<byte> payload = bytes[headerSize..];

        Span<byte> actualSha = stackalloc byte[32];
        SHA256.HashData(payload, actualSha);
        if (!actualSha.SequenceEqual(expectedSha))
        {
            error = "payload SHA-256 mismatch (corrupt model)";
            return false;
        }

        // ---- Deserialize arrays ----
        int offset = 0;
        short[] ReadInt16Array(ReadOnlySpan<byte> src, int count)
        {
            var result = new short[count];
            for (int i = 0; i < count; i++, offset += 2)
                result[i] = BinaryPrimitives.ReadInt16LittleEndian(src[offset..]);
            return result;
        }
        int[] ReadInt32Array(ReadOnlySpan<byte> src, int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++, offset += 4)
                result[i] = BinaryPrimitives.ReadInt32LittleEndian(src[offset..]);
            return result;
        }

        sbyte[] ReadInt8Array(ReadOnlySpan<byte> src, int count)
        {
            var result = new sbyte[count];
            for (int i = 0; i < count; i++, offset++)
                result[i] = (sbyte)src[offset];
            return result;
        }

        var ftWeights = ReadInt16Array(payload, ftInputs * ftOutputs);
        var ftBias = ReadInt16Array(payload, ftOutputs);

        // ARCH 5 reads a different head: the pairwise transformer halves the L1
        // input, there is a second hidden layer, and the output row spans both
        // layers' activations. Everything stays bucket-major and the blocks are
        // in the order the exporter writes them.
        int l1Inputs = archFive ? ftOutputs : 2 * ftOutputs;
        int outInputs = archFive ? 2 * l1Outputs + 2 * l2Outputs : l1Outputs;

        int l1WeightCount = buckets * l1Outputs * l1Inputs;
        short[]? l1Weights = null;
        sbyte[]? l1WeightsI8 = null;
        if (int8)
            l1WeightsI8 = ReadInt8Array(payload, l1WeightCount);
        else
            l1Weights = ReadInt16Array(payload, l1WeightCount);

        var l1Bias = ReadInt32Array(payload, buckets * l1Outputs);

        sbyte[]? l2Weights = null;
        int[]? l2Bias = null;
        if (archFive)
        {
            l2Weights = ReadInt8Array(payload, buckets * l2Outputs * 2 * l1Outputs);
            l2Bias = ReadInt32Array(payload, buckets * l2Outputs);
        }

        var outWeights = ReadInt16Array(payload, buckets * outInputs);
        var outBias = ReadInt32Array(payload, buckets);

        // The threat transformer, arch 4 only. Read last because it is appended
        // last, so the offsets of everything before it are the ones arch 1-3
        // already used and no existing net moves by a byte.
        short[]? threatWeights = null;
        if (hasThreats)
            threatWeights = ReadInt16Array(payload, ThreatFeatureIndex.InputSize * ftOutputs);

        // Built once at load: see NnueNetwork.SquaredActivation for why the
        // activation must not divide at evaluation time.
        var squared = new byte[qa + 1];
        for (int c = 0; c <= qa; c++)
            squared[c] = (byte)(c * c / qa);

        network = new NnueNetwork
        {
            QbShift = System.Numerics.BitOperations.IsPow2(qb)
                ? System.Numerics.BitOperations.TrailingZeroCount(qb)
                : -1,
            SquaredActivation = squared,
            ArchitectureId = arch,
            FtInputs = ftInputs,
            FtOutputs = ftOutputs,
            L1Outputs = l1Outputs,
            L2Outputs = l2Outputs,
            OutputBuckets = buckets,
            QA = qa,
            QB = qb,
            OutputScale = outputScale,
            FtWeights = ftWeights,
            FtBias = ftBias,
            L1Weights = l1Weights,
            L1WeightsI8 = l1WeightsI8,
            L1Bias = l1Bias,
            L2Weights = l2Weights,
            L2Bias = l2Bias,
            OutWeights = outWeights,
            OutBias = outBias,
            ThreatWeights = threatWeights,
            Sha256 = Convert.ToHexString(actualSha).ToLowerInvariant()
        };
        error = "";
        return true;
    }
}
