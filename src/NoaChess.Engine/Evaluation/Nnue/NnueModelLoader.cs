using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NoaChess.Engine.Evaluation.Nnue;

// Parses and validates a .noannue model file (see NnueModelHeader for the
// layout). Validation is strict: magic, version, schema, architecture,
// dimensions, payload length and SHA-256 must all match — a corrupt or
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
        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]);
        ReadOnlySpan<byte> expectedSha = bytes[48..80];

        if (version != NnueModelHeader.SupportedFormatVersion)
        {
            error = $"unsupported format version {version}";
            return false;
        }
        if (schema != NnueFeatureIndex.FeatureSchemaId)
        {
            error = $"feature schema {schema} does not match engine schema {NnueFeatureIndex.FeatureSchemaId}";
            return false;
        }
        if (arch != NnueModelHeader.SupportedArchitectureId)
        {
            error = $"unsupported architecture id {arch}";
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
        long expectedPayload =
            (long)ftInputs * ftOutputs * 2   // ftWeights int16
            + ftOutputs * 2                  // ftBias int16
            + (long)l1Outputs * 2 * ftOutputs * 2 // l1Weights int16
            + l1Outputs * 4                  // l1Bias int32
            + l1Outputs * 2                  // outWeights int16
            + 4;                             // outBias int32

        if ((long)payloadLength != expectedPayload)
        {
            error = $"payload length {payloadLength} does not match dimensions (expected {expectedPayload})";
            return false;
        }
        if (bytes.Length != NnueModelHeader.HeaderSize + expectedPayload)
        {
            error = "file length does not match header";
            return false;
        }

        ReadOnlySpan<byte> payload = bytes[NnueModelHeader.HeaderSize..];

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

        var ftWeights = ReadInt16Array(payload, ftInputs * ftOutputs);
        var ftBias = ReadInt16Array(payload, ftOutputs);
        var l1Weights = ReadInt16Array(payload, l1Outputs * 2 * ftOutputs);
        var l1Bias = ReadInt32Array(payload, l1Outputs);
        var outWeights = ReadInt16Array(payload, l1Outputs);
        int outBias = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);

        network = new NnueNetwork
        {
            FtInputs = ftInputs,
            FtOutputs = ftOutputs,
            L1Outputs = l1Outputs,
            QA = qa,
            QB = qb,
            OutputScale = outputScale,
            FtWeights = ftWeights,
            FtBias = ftBias,
            L1Weights = l1Weights,
            L1Bias = l1Bias,
            OutWeights = outWeights,
            OutBias = outBias,
            Sha256 = Convert.ToHexString(actualSha).ToLowerInvariant()
        };
        error = "";
        return true;
    }
}
