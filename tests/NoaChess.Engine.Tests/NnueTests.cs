using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// The mandatory NNUE correctness suite from the technical roadmap:
// - Same position -> same features; golden feature indices.
// - Accumulator incremental update == full recomputation (random games
//   including castling, en passant and promotions).
// - Make/unmake restores features.
// - Corrupt/incompatible models rejected by the loader.
// - Scalar inference == SIMD inference.
public class NnueTests
{
    // ---------- Test network ----------

    // Small deterministic network (seeded RNG): correctness tests do not need
    // trained weights, only stable nonzero ones.
    private static NnueNetwork CreateTestNetwork(int seed = 1234, int ftOut = 32, int l1Out = 8)
    {
        var rng = new Random(seed);
        short RandW(int range) => (short)rng.Next(-range, range + 1);

        var net = new NnueNetwork
        {
            FtInputs = NnueFeatureIndex.InputSize,
            FtOutputs = ftOut,
            L1Outputs = l1Out,
            QA = 255,
            QB = 64,
            OutputScale = 400,
            FtWeights = new short[NnueFeatureIndex.InputSize * ftOut],
            FtBias = new short[ftOut],
            L1Weights = new short[l1Out * 2 * ftOut],
            L1Bias = new int[l1Out],
            OutWeights = new short[l1Out],
            OutBias = rng.Next(-1000, 1000),
            Sha256 = "test"
        };

        for (int i = 0; i < net.FtWeights.Length; i++) net.FtWeights[i] = RandW(60);
        for (int i = 0; i < net.FtBias.Length; i++) net.FtBias[i] = RandW(100);
        for (int i = 0; i < net.L1Weights.Length; i++) net.L1Weights[i] = RandW(100);
        for (int i = 0; i < net.L1Bias.Length; i++) net.L1Bias[i] = rng.Next(-5000, 5000);
        for (int i = 0; i < net.OutWeights.Length; i++) net.OutWeights[i] = RandW(100);
        return net;
    }

    // ---------- Feature indexing ----------

    [Fact]
    public void FeatureIndex_GoldenValues()
    {
        // White pawn e2, white king e1, white perspective:
        // king 4, pieceIndex = pawn(0)*2 + own(0) = 0, square 12
        // -> 4*640 + 0*64 + 12 = 2572.
        Assert.Equal(2572, NnueFeatureIndex.Index(Color.White, 4, Color.White, PieceType.Pawn, 12));

        // Same pawn from Black's perspective (black king e8): both squares
        // are rank-flipped (e8 -> e1 = 4, e2 -> e7 = 52) and the pawn is an
        // enemy piece -> pieceIndex = 0*2 + 1 = 1:
        // -> 4*640 + 1*64 + 52 = 2676.
        Assert.Equal(2676, NnueFeatureIndex.Index(Color.Black, 60, Color.White, PieceType.Pawn, 12));

        // Symmetry: a mirrored position must produce the same index for the
        // mirrored perspective. Black queen d8 seen by Black on king e8 ==
        // white queen d1 seen by White on king e1.
        int white = NnueFeatureIndex.Index(Color.White, 4, Color.White, PieceType.Queen, 3);
        int black = NnueFeatureIndex.Index(Color.Black, 60, Color.Black, PieceType.Queen, 59);
        Assert.Equal(white, black);
    }

    [Fact]
    public void ActiveFeatures_AreDeterministicAndComplete()
    {
        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        Span<int> a = stackalloc int[NnueFeatureIndex.MaxActiveFeatures];
        Span<int> b = stackalloc int[NnueFeatureIndex.MaxActiveFeatures];
        int countA = NnueFeatureIndex.ActiveFeatures(board, Color.White, a);
        int countB = NnueFeatureIndex.ActiveFeatures(board, Color.White, b);

        // Same position -> same features (deterministic order too).
        Assert.Equal(countA, countB);
        Assert.True(a[..countA].SequenceEqual(b[..countB]));

        // Kiwipete has 32 pieces -> 30 non-king features.
        Assert.Equal(30, countA);

        // All indices inside the schema's space.
        foreach (int f in a[..countA])
            Assert.InRange(f, 0, NnueFeatureIndex.InputSize - 1);
    }

    [Fact]
    public void MakeUnmake_RestoresFeatures()
    {
        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        Span<int> before = stackalloc int[NnueFeatureIndex.MaxActiveFeatures];
        Span<int> after = stackalloc int[NnueFeatureIndex.MaxActiveFeatures];
        int beforeCount = NnueFeatureIndex.ActiveFeatures(board, Color.White, before);

        foreach (Move move in MoveGenerator.GenerateLegalMoves(board))
        {
            board.MakeMove(move);
            board.UnmakeMove();
            int afterCount = NnueFeatureIndex.ActiveFeatures(board, Color.White, after);
            Assert.Equal(beforeCount, afterCount);
            Assert.True(before[..beforeCount].SequenceEqual(after[..afterCount]));
        }
    }

    // ---------- Incremental accumulators ----------

    // Plays random legal games (seeded), keeping an incremental evaluator in
    // sync; at every ply the incremental evaluation must equal a fresh
    // evaluator that recomputes from scratch. Covers captures, castling,
    // en passant, promotions and king moves organically.
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void IncrementalAccumulator_MatchesFullRefresh_RandomGames(int seed)
    {
        var net = CreateTestNetwork();
        var incremental = new NnueEvaluator(net);
        var reference = new NnueEvaluator(net);
        var rng = new Random(seed);

        var board = new Board();
        incremental.Reset(board);

        for (int plyCount = 0; plyCount < 120; plyCount++)
        {
            if (GameState.GetResult(board) != GameResult.Ongoing)
                break;

            var moves = MoveGenerator.GenerateLegalMoves(board);
            Move move = moves[rng.Next(moves.Count)];

            incremental.PushMove(board, move);
            board.MakeMove(move);

            int incrementalScore = incremental.Evaluate(board);
            reference.Reset(board); // Full recomputation.
            int referenceScore = reference.Evaluate(board);

            Assert.Equal(referenceScore, incrementalScore);
        }
    }

    [Fact]
    public void IncrementalAccumulator_SurvivesUnmakeSequences()
    {
        // Push/pop symmetry: walk one ply down every legal move and back;
        // after each pop the evaluation must equal the root evaluation.
        var net = CreateTestNetwork();
        var evaluator = new NnueEvaluator(net);
        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        evaluator.Reset(board);
        int rootScore = evaluator.Evaluate(board);

        foreach (Move move in MoveGenerator.GenerateLegalMoves(board))
        {
            evaluator.PushMove(board, move);
            board.MakeMove(move);
            _ = evaluator.Evaluate(board);
            board.UnmakeMove();
            evaluator.Pop();

            Assert.Equal(rootScore, evaluator.Evaluate(board));
        }
    }

    // ---------- Inference backends ----------

    [Fact]
    public void ScalarAndSimd_ProduceIdenticalScores()
    {
        var net = CreateTestNetwork(ftOut: 64, l1Out: 16);
        var rng = new Random(99);

        for (int trial = 0; trial < 50; trial++)
        {
            var stm = new short[net.FtOutputs];
            var opp = new short[net.FtOutputs];
            for (int i = 0; i < net.FtOutputs; i++)
            {
                // Include out-of-clip-range values to exercise the clamps.
                stm[i] = (short)rng.Next(-500, 800);
                opp[i] = (short)rng.Next(-500, 800);
            }

            Assert.Equal(
                NnueInference.EvaluateScalar(net, stm, opp),
                NnueInference.EvaluateSimd(net, stm, opp));
        }
    }

    // ---------- Model loader ----------

    // Serializes a network into the NOANNUE binary format (the C# mirror of
    // export_model.py, used only by tests).
    private static byte[] Serialize(NnueNetwork net)
    {
        long payloadLen =
            (long)net.FtInputs * net.FtOutputs * 2 + net.FtOutputs * 2
            + (long)net.L1Outputs * 2 * net.FtOutputs * 2 + net.L1Outputs * 4
            + net.L1Outputs * 2 + 4;

        var payload = new byte[payloadLen];
        int o = 0;
        void W16(short v) { BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(o), v); o += 2; }
        void W32(int v) { BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(o), v); o += 4; }

        foreach (short v in net.FtWeights) W16(v);
        foreach (short v in net.FtBias) W16(v);
        foreach (short v in net.L1Weights) W16(v);
        foreach (int v in net.L1Bias) W32(v);
        foreach (short v in net.OutWeights) W16(v);
        W32(net.OutBias);

        var file = new byte[NnueModelHeader.HeaderSize + payloadLen];
        Encoding.ASCII.GetBytes(NnueModelHeader.Magic).CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), NnueModelHeader.SupportedFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), NnueFeatureIndex.FeatureSchemaId);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), NnueModelHeader.SupportedArchitectureId);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(20), net.FtInputs);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), net.FtOutputs);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28), net.L1Outputs);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), (ushort)net.QA);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), (ushort)net.QB);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(36), (ushort)net.OutputScale);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(40), (ulong)payloadLen);
        SHA256.HashData(payload).CopyTo(file, 48);
        payload.CopyTo(file, NnueModelHeader.HeaderSize);
        return file;
    }

    [Fact]
    public void Loader_RoundTripsAValidModel()
    {
        var original = CreateTestNetwork();
        byte[] bytes = Serialize(original);

        Assert.True(NnueModelLoader.TryParse(bytes, out NnueNetwork? loaded, out string error), error);

        // The loaded network must evaluate exactly like the original.
        var board = new Board();
        var a = new NnueEvaluator(original); a.Reset(board);
        var b = new NnueEvaluator(loaded!); b.Reset(board);
        Assert.Equal(a.Evaluate(board), b.Evaluate(board));
    }

    [Theory]
    [InlineData(0, (byte)'X')]   // Corrupt magic.
    [InlineData(12, 99)]         // Wrong feature schema id.
    [InlineData(16, 99)]         // Wrong architecture id.
    [InlineData(100, 77)]        // Flipped payload byte -> SHA mismatch.
    public void Loader_RejectsCorruptModels(int offset, byte newValue)
    {
        byte[] bytes = Serialize(CreateTestNetwork());
        bytes[offset] = newValue;

        Assert.False(NnueModelLoader.TryParse(bytes, out _, out string error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Loader_RejectsTruncatedFile()
    {
        byte[] bytes = Serialize(CreateTestNetwork());
        Assert.False(NnueModelLoader.TryParse(bytes.AsSpan(0, bytes.Length - 100), out _, out string error));
        Assert.Contains("length", error);
    }
}
