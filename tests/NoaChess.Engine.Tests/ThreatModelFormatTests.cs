using System.Buffers.Binary;
using System.Security.Cryptography;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// Round-trips a synthetic arch 4 model through the loader.
//
// The threat transformer is 60,720 rows the file has to carry and the header
// deliberately does not describe: arch 4 means "HalfKAv2_hm plus the threat
// set", and that set's size is a constant of the schema rather than a field. So
// the loader asserts the size instead of reading it, and this checks that the
// assertion actually holds - both that a correct file loads with its weights
// intact, and that a file one row short is REJECTED rather than read into a net
// that would evaluate nonsense.
//
// Built here rather than taken from a real model on purpose: a trainer that
// exports arch 4 does not exist yet, and waiting for it would leave the format
// untested at exactly the moment its shape is still cheap to change.
public class ThreatModelFormatTests
{
    private const int FtOutputs = 8;      // small on purpose; the format does not care
    private const int L1Outputs = 4;
    private const int Buckets = 1;

    private static byte[] BuildModel(uint arch, int threatRows)
    {
        int ftInputs = NnueFeatureIndex.InputSize;
        bool int8 = arch != NnueModelHeader.ArchitectureInt16L1;

        var payload = new List<byte>();
        void I16(short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, v); payload.AddRange(b.ToArray()); }
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); payload.AddRange(b.ToArray()); }

        // Distinct, reproducible values so a block read at the wrong offset
        // shows up as a wrong number rather than as a plausible one.
        for (int i = 0; i < ftInputs * FtOutputs; i++) I16((short)(i % 7 - 3));
        for (int i = 0; i < FtOutputs; i++) I16((short)(i + 1));

        int l1Count = Buckets * L1Outputs * 2 * FtOutputs;
        for (int i = 0; i < l1Count; i++)
        {
            if (int8) payload.Add(unchecked((byte)(sbyte)(i % 5 - 2)));
            else I16((short)(i % 5 - 2));
        }
        for (int i = 0; i < Buckets * L1Outputs; i++) I32(i + 100);
        for (int i = 0; i < Buckets * L1Outputs; i++) I16((short)(i + 11));
        for (int i = 0; i < Buckets; i++) I32(i + 1000);

        // The threat block. threatRows is a parameter so the test can also build
        // a deliberately wrong one.
        if (arch == NnueModelHeader.ArchitectureThreats)
            for (int i = 0; i < threatRows * FtOutputs; i++)
                I16((short)(i % 11 - 5));

        byte[] body = payload.ToArray();
        var file = new byte[NnueModelHeader.HeaderSize + body.Length];
        Span<byte> h = file;

        "NOANNUE1"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], NnueModelHeader.SupportedFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)NnueFeatureIndex.FeatureSchemaId);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], arch);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)ftInputs);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], FtOutputs);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], L1Outputs);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 127);   // QA, int8-legal
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 64);    // QB
        BinaryPrimitives.WriteUInt16LittleEndian(h[36..], 400);   // output scale
        BinaryPrimitives.WriteUInt16LittleEndian(h[38..], Buckets);
        BinaryPrimitives.WriteUInt64LittleEndian(h[40..], (ulong)body.Length);
        SHA256.HashData(body).CopyTo(h[48..]);
        body.CopyTo(h[NnueModelHeader.HeaderSize..]);

        return file;
    }

    [Fact]
    public void Arch4LoadsWithItsThreatWeights()
    {
        byte[] file = BuildModel(NnueModelHeader.ArchitectureThreats, ThreatFeatureIndex.InputSize);

        bool ok = NnueModelLoader.TryParse(file, out NnueNetwork? net, out string error);

        Assert.True(ok, error);
        Assert.NotNull(net);
        Assert.True(net!.UsesThreats);
        Assert.NotNull(net.ThreatWeights);
        Assert.Equal(ThreatFeatureIndex.InputSize * FtOutputs, net.ThreatWeights!.Length);

        // The block is read LAST, so a wrong offset anywhere before it lands
        // here as wrong values. First and last rows are checked because an
        // off-by-one in the length shows at the ends and nowhere else.
        Assert.Equal((short)(0 % 11 - 5), net.ThreatWeights[0]);
        int last = ThreatFeatureIndex.InputSize * FtOutputs - 1;
        Assert.Equal((short)(last % 11 - 5), net.ThreatWeights[last]);

        // Everything that came before must still be where it always was, or the
        // appended block moved something.
        Assert.Equal((short)1, net.FtBias[0]);
        Assert.Equal(1000, net.OutBias[0]);
    }

    [Fact]
    public void AnArch4FileOneRowShortIsRejected()
    {
        byte[] file = BuildModel(NnueModelHeader.ArchitectureThreats,
                                 ThreatFeatureIndex.InputSize - 1);

        bool ok = NnueModelLoader.TryParse(file, out NnueNetwork? net, out string error);

        Assert.False(ok);
        Assert.Null(net);
        Assert.Contains("payload length", error);
    }

    [Fact]
    public void OlderArchitecturesStillLoadAndCarryNoThreats()
    {
        // The whole point of appending the block was that nets already playing
        // do not move by a byte.
        foreach (uint arch in new[] { NnueModelHeader.ArchitectureInt16L1,
                                      NnueModelHeader.ArchitectureInt8L1 })
        {
            byte[] file = BuildModel(arch, 0);
            bool ok = NnueModelLoader.TryParse(file, out NnueNetwork? net, out string error);
            Assert.True(ok, $"arch {arch}: {error}");
            Assert.False(net!.UsesThreats);
            Assert.Null(net.ThreatWeights);
        }
    }
}
