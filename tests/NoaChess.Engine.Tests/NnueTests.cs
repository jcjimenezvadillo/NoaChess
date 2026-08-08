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
            ArchitectureId = NnueModelHeader.ArchitectureInt16L1,
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
            OutBias = [rng.Next(-1000, 1000)],
            Sha256 = "test"
        };

        for (int i = 0; i < net.FtWeights.Length; i++) net.FtWeights[i] = RandW(60);
        for (int i = 0; i < net.FtBias.Length; i++) net.FtBias[i] = RandW(100);
        for (int i = 0; i < net.L1Weights!.Length; i++) net.L1Weights[i] = RandW(100);
        for (int i = 0; i < net.L1Bias.Length; i++) net.L1Bias[i] = rng.Next(-5000, 5000);
        for (int i = 0; i < net.OutWeights.Length; i++) net.OutWeights[i] = RandW(100);
        return net;
    }

    // Same weights as the int16 network, re-expressed for the v4.0.0 int8 L1
    // path: QA drops to 127 (the VPMADDUBSW saturation bound) and the L1 matrix
    // is stored as sbyte. ftOut must be a multiple of 32 for the AVX2 packing
    // path to be exercised rather than the fallback.
    private static NnueNetwork CreateTestNetworkInt8(int seed = 1234, int ftOut = 32, int l1Out = 8)
    {
        var rng = new Random(seed);
        short RandW(int range) => (short)rng.Next(-range, range + 1);

        var net = new NnueNetwork
        {
            ArchitectureId = NnueModelHeader.ArchitectureInt8L1,
            FtInputs = NnueFeatureIndex.InputSize,
            FtOutputs = ftOut,
            L1Outputs = l1Out,
            QA = 127,
            QB = 64,
            OutputScale = 400,
            FtWeights = new short[NnueFeatureIndex.InputSize * ftOut],
            FtBias = new short[ftOut],
            L1WeightsI8 = new sbyte[l1Out * 2 * ftOut],
            L1Bias = new int[l1Out],
            OutWeights = new short[l1Out],
            OutBias = [rng.Next(-1000, 1000)],
            Sha256 = "test-i8"
        };

        for (int i = 0; i < net.FtWeights.Length; i++) net.FtWeights[i] = RandW(60);
        for (int i = 0; i < net.FtBias.Length; i++) net.FtBias[i] = RandW(100);
        // Full int8 range including the extremes, so the saturation bound is
        // exercised at its worst case rather than near zero.
        for (int i = 0; i < net.L1WeightsI8!.Length; i++) net.L1WeightsI8[i] = (sbyte)rng.Next(-127, 128);
        for (int i = 0; i < net.L1Bias.Length; i++) net.L1Bias[i] = rng.Next(-5000, 5000);
        for (int i = 0; i < net.OutWeights.Length; i++) net.OutWeights[i] = RandW(100);
        return net;
    }

    // ---------- Feature indexing ----------

    [Fact]
    public void FeatureIndex_GoldenValues()
    {
        // White pawn e2, white king e1, White perspective:
        // vflip 0, orient(e1=4)=0 (king already on files e-h), oriented sq = 12;
        // plane = own pawn = 0; kingBucket(e1=4) = 31 -> 31*704 = 21824.
        // -> 12 + 0 + 21824 = 21836.
        Assert.Equal(21836, NnueFeatureIndex.Index(Color.White, 4, Color.White, PieceType.Pawn, 12));

        // Same white pawn e2 from Black's perspective (black king e8=60):
        // vflip 56 -> pawn e2 becomes e7=52; enemy pawn -> plane 1*64 = 64;
        // kingBucket(60 ^ 56 = 4) = 21824.
        // -> 52 + 64 + 21824 = 21940.
        Assert.Equal(21940, NnueFeatureIndex.Index(Color.Black, 60, Color.White, PieceType.Pawn, 12));

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

        // Kiwipete has 32 pieces; in HalfKA every piece (kings included) is a
        // feature -> 32 active features.
        Assert.Equal(32, countA);

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

    // ---------- Lazy accumulator (v4.5.0) ----------
    //
    // The two tests above evaluate at EVERY ply, so they only ever exercise a
    // pending chain of length 1. The lazy stack's real risk is the opposite
    // case: plies pushed without anyone asking for an evaluation, so several
    // recorded updates have to be replayed at once onto an ancestor's values.
    // Everything below deliberately skips evaluations to build those chains.

    // Random walk that descends, backtracks and evaluates at random points. The
    // interesting states it produces on its own: chains spanning king moves of
    // the other side, chains interrupted by a refresh, siblings pushed onto a
    // parent that was never evaluated, and re-evaluation after popping into the
    // middle of an uncomputed chain.
    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(2024)]
    public void LazyAccumulator_MatchesFullRefresh_WithSparseEvaluations(int seed)
    {
        var net = CreateTestNetwork();
        var lazy = new NnueEvaluator(net);
        var reference = new NnueEvaluator(net);
        var rng = new Random(seed);

        var board = new Board();
        lazy.Reset(board);

        // Null moves are pushed as a distinct kind of level, so the stack has to
        // be unwound with the matching unmake; remember what each ply was.
        var wasNull = new bool[64];
        int depth = 0;

        for (int step = 0; step < 600; step++)
        {
            bool canDescend = depth < 14;
            bool descend = depth == 0 || (canDescend && rng.Next(100) < 65);

            if (descend)
            {
                var moves = MoveGenerator.GenerateLegalMoves(board);
                if (moves.Count == 0)
                {
                    // Checkmate or stalemate: nothing to push, back up instead.
                    if (depth == 0)
                        break;
                    descend = false;
                }
                else if (rng.Next(100) < 8 && !board.IsInCheck())
                {
                    wasNull[depth] = true;
                    lazy.PushNull();
                    board.MakeNullMove();
                    depth++;
                }
                else
                {
                    wasNull[depth] = false;
                    Move move = moves[rng.Next(moves.Count)];
                    lazy.PushMove(board, move);
                    board.MakeMove(move);
                    depth++;
                }
            }

            if (!descend)
            {
                depth--;
                if (wasNull[depth])
                    board.UnmakeNullMove();
                else
                    board.UnmakeMove();
                lazy.Pop();
            }

            // Only sometimes - that is what leaves pending chains longer than 1.
            if (rng.Next(100) < 30)
            {
                int lazyScore = lazy.Evaluate(board);
                reference.Reset(board);
                Assert.Equal(reference.Evaluate(board), lazyScore);
            }
        }
    }

    // A single long chain: push every ply of a forced-looking line without
    // evaluating anything, then evaluate once at the bottom. The FEN is the
    // usual perft position, which reaches castling, captures and king moves
    // within a few plies from any of its branches.
    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(97)]
    public void LazyAccumulator_MatchesFullRefresh_AfterUnevaluatedChain(int seed)
    {
        var net = CreateTestNetwork();
        var lazy = new NnueEvaluator(net);
        var reference = new NnueEvaluator(net);
        var rng = new Random(seed);

        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        lazy.Reset(board);

        int pushed = 0;
        for (int ply = 0; ply < 10; ply++)
        {
            var moves = MoveGenerator.GenerateLegalMoves(board);
            if (moves.Count == 0)
                break;
            Move move = moves[rng.Next(moves.Count)];
            lazy.PushMove(board, move);
            board.MakeMove(move);
            pushed++;
        }

        Assert.True(pushed > 1, "the test needs a chain, not a single push");

        // First evaluation of the whole line: one copy plus 'pushed' replays.
        reference.Reset(board);
        Assert.Equal(reference.Evaluate(board), lazy.Evaluate(board));

        // Unwind, evaluating at every level on the way back up. Each of these
        // walks back into a chain whose intermediate levels are still
        // uncomputed, which is the case a naive implementation gets wrong.
        while (pushed-- > 0)
        {
            board.UnmakeMove();
            lazy.Pop();
            reference.Reset(board);
            Assert.Equal(reference.Evaluate(board), lazy.Evaluate(board));
        }
    }

    // Siblings pushed onto a parent that was never evaluated: the child's values
    // array still holds the PREVIOUS sibling's numbers, so a lazy level that
    // forgets to copy before replaying would silently accumulate twice.
    [Fact]
    public void LazyAccumulator_SiblingsDoNotInheritEachOther()
    {
        var net = CreateTestNetwork();
        var lazy = new NnueEvaluator(net);
        var reference = new NnueEvaluator(net);

        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        lazy.Reset(board);

        // Descend two plies WITHOUT evaluating, so the parent of the siblings
        // below is itself uncomputed and every sibling replays a chain of 3.
        var opening = MoveGenerator.GenerateLegalMoves(board);
        foreach (Move first in new[] { opening[0], opening[opening.Count / 2] })
        {
            lazy.PushMove(board, first);
            board.MakeMove(first);

            var replies = MoveGenerator.GenerateLegalMoves(board);
            Move reply = replies[0];
            lazy.PushMove(board, reply);
            board.MakeMove(reply);

            foreach (Move sibling in MoveGenerator.GenerateLegalMoves(board))
            {
                lazy.PushMove(board, sibling);
                board.MakeMove(sibling);

                reference.Reset(board);
                Assert.Equal(reference.Evaluate(board), lazy.Evaluate(board));

                board.UnmakeMove();
                lazy.Pop();
            }

            board.UnmakeMove();
            lazy.Pop();
            board.UnmakeMove();
            lazy.Pop();
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
        int l1Bytes = net.UsesInt8L1 ? 1 : 2;
        int buckets = net.OutputBuckets;
        long payloadLen =
            (long)net.FtInputs * net.FtOutputs * 2 + net.FtOutputs * 2
            + (long)buckets * net.L1Outputs * 2 * net.FtOutputs * l1Bytes
            + (long)buckets * net.L1Outputs * 4
            + (long)buckets * net.L1Outputs * 2 + (long)buckets * 4;

        var payload = new byte[payloadLen];
        int o = 0;
        void W16(short v) { BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(o), v); o += 2; }
        void W32(int v) { BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(o), v); o += 4; }

        foreach (short v in net.FtWeights) W16(v);
        foreach (short v in net.FtBias) W16(v);
        if (net.UsesInt8L1)
            foreach (sbyte v in net.L1WeightsI8!) payload[o++] = (byte)v;
        else
            foreach (short v in net.L1Weights!) W16(v);
        foreach (int v in net.L1Bias) W32(v);
        foreach (short v in net.OutWeights) W16(v);
        foreach (int v in net.OutBias) W32(v);

        var file = new byte[NnueModelHeader.HeaderSize + payloadLen];
        Encoding.ASCII.GetBytes(NnueModelHeader.Magic).CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), NnueModelHeader.SupportedFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), NnueFeatureIndex.FeatureSchemaId);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), net.ArchitectureId);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(20), net.FtInputs);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), net.FtOutputs);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28), net.L1Outputs);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), (ushort)net.QA);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), (ushort)net.QB);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(36), (ushort)net.OutputScale);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(38),
            (ushort)(net.ArchitectureId == NnueModelHeader.ArchitectureInt8L1Buckets
                ? net.OutputBuckets : 0));
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

    // ---------- v4.0.0: accumulator cache (finny table) ----------

    // THE parity gate for the cache. The random-game test above cannot catch a
    // cache bug on its own, because after v4.0.0 both the incremental evaluator
    // and its "full recomputation" reference go through the cache - a wrong
    // cache would be wrong identically on both sides and the test would pass.
    // This one compares the cache against NnueAccumulator.Refresh, the direct
    // rebuild-from-bias path that does not consult it.
    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(99)]
    public void AccumulatorCache_MatchesDirectRefresh_RandomGames(int seed)
    {
        var net = CreateTestNetwork();
        var cache = new NnueAccumulatorCache(net);
        var cached = new NnueAccumulator(net.FtOutputs);
        var direct = new NnueAccumulator(net.FtOutputs);
        var rng = new Random(seed);

        var board = new Board();
        for (int plyCount = 0; plyCount < 150; plyCount++)
        {
            foreach (Color perspective in new[] { Color.White, Color.Black })
            {
                cache.Refresh(cached, board, perspective);
                direct.Refresh(net, board, perspective);
                Assert.Equal(direct.Values[(int)perspective], cached.Values[(int)perspective]);
                Assert.True(cached.Valid[(int)perspective]);
            }

            if (GameState.GetResult(board) != GameResult.Ongoing)
                break;
            var moves = MoveGenerator.GenerateLegalMoves(board);
            board.MakeMove(moves[rng.Next(moves.Count)]);
        }
    }

    // A cache entry is keyed by king square, and two squares sharing a bucket
    // are horizontal mirrors whose Orient() differs - so every feature index
    // differs too. Walking a king back and forth across the a-d / e-h boundary
    // reuses entries in both directions and would expose a bucket-keyed cache.
    [Fact]
    public void AccumulatorCache_HandlesKingCrossingTheMirrorBoundary()
    {
        var net = CreateTestNetwork();
        var cache = new NnueAccumulatorCache(net);
        var cached = new NnueAccumulator(net.FtOutputs);
        var direct = new NnueAccumulator(net.FtOutputs);

        // King walks e1 -> d1 -> c1 -> d1 -> e1: crosses the file-d boundary
        // twice, so entries are written and then re-read after the mirror flips.
        foreach (string fen in new[]
                 {
                     "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
                     "4k3/8/8/8/8/8/4P3/3K4 w - - 0 1",
                     "4k3/8/8/8/8/8/4P3/2K5 w - - 0 1",
                     "4k3/8/8/8/8/8/4P3/3K4 w - - 0 1",
                     "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
                 })
        {
            var board = new Board(fen);
            foreach (Color perspective in new[] { Color.White, Color.Black })
            {
                cache.Refresh(cached, board, perspective);
                direct.Refresh(net, board, perspective);
                Assert.Equal(direct.Values[(int)perspective], cached.Values[(int)perspective]);
            }
        }
    }

    // ---------- v4.0.0: int8 L1 architecture ----------

    [Fact]
    public void Int8_ScalarMatchesSimd()
    {
        var net = CreateTestNetworkInt8();
        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        var acc = new NnueAccumulator(net.FtOutputs);
        acc.Refresh(net, board, Color.White);
        acc.Refresh(net, board, Color.Black);

        Assert.Equal(
            NnueInference.EvaluateScalar(net, acc.Values[0], acc.Values[1]),
            NnueInference.EvaluateSimd(net, acc.Values[0], acc.Values[1]));
    }

    // The AVX2 packing path reorders bytes (PackUnsignedSaturate works per
    // 128-bit lane and is corrected by a Permute4x64). If that permute were
    // wrong, every activation would meet the wrong weight - which shows up
    // only over many different accumulator states, not on one position.
    [Theory]
    [InlineData(5)]
    [InlineData(23)]
    [InlineData(1001)]
    public void Int8_ScalarMatchesSimd_OverRandomGames(int seed)
    {
        var net = CreateTestNetworkInt8();
        var evaluator = new NnueEvaluator(net);
        var rng = new Random(seed);

        var board = new Board();
        var acc = new NnueAccumulator(net.FtOutputs);

        for (int plyCount = 0; plyCount < 120; plyCount++)
        {
            acc.Refresh(net, board, Color.White);
            acc.Refresh(net, board, Color.Black);
            int stm = (int)board.SideToMove;
            int opp = 1 - stm;

            Assert.Equal(
                NnueInference.EvaluateScalar(net, acc.Values[stm], acc.Values[opp]),
                NnueInference.EvaluateSimd(net, acc.Values[stm], acc.Values[opp]));

            if (GameState.GetResult(board) != GameResult.Ongoing)
                break;
            var moves = MoveGenerator.GenerateLegalMoves(board);
            board.MakeMove(moves[rng.Next(moves.Count)]);
        }
    }

    // The whole int8 design rests on VPMADDUBSW never saturating its int16
    // lane. Drive the extreme case directly: maximum activation against maximum
    // weight of both signs. If saturation ever occurred, SIMD and scalar would
    // disagree, because only the SIMD path saturates.
    [Fact]
    public void Int8_DoesNotSaturate_AtExtremeActivationsAndWeights()
    {
        var net = CreateTestNetworkInt8(ftOut: 64, l1Out: 8);

        // Every L1 weight at an extreme, alternating sign so partial sums do
        // not cancel into a comfortable range.
        for (int i = 0; i < net.L1WeightsI8!.Length; i++)
            net.L1WeightsI8[i] = (sbyte)(i % 2 == 0 ? 127 : -127);

        // Accumulators far above QA, so every activation clamps to exactly QA.
        var stm = new short[net.FtOutputs];
        var opp = new short[net.FtOutputs];
        Array.Fill(stm, (short)30_000);
        Array.Fill(opp, (short)30_000);

        Assert.Equal(
            NnueInference.EvaluateScalar(net, stm, opp),
            NnueInference.EvaluateSimd(net, stm, opp));

        // And the bound itself: 2*QA*127 must fit int16 for the kernel to be
        // exact by construction rather than by luck.
        Assert.True(2 * net.QA * 127 <= short.MaxValue,
            $"int8 kernel bound violated: 2*{net.QA}*127 = {2 * net.QA * 127} > {short.MaxValue}");
    }

    [Fact]
    public void Loader_RoundTripsAnInt8Model()
    {
        var original = CreateTestNetworkInt8();
        byte[] bytes = Serialize(original);

        Assert.True(NnueModelLoader.TryParse(bytes, out NnueNetwork? loaded, out string error), error);
        Assert.Equal(NnueModelHeader.ArchitectureInt8L1, loaded!.ArchitectureId);
        Assert.True(loaded.UsesInt8L1);
        Assert.Null(loaded.L1Weights);
        Assert.Equal(original.L1WeightsI8, loaded.L1WeightsI8);

        var board = new Board();
        var a = new NnueEvaluator(original); a.Reset(board);
        var b = new NnueEvaluator(loaded); b.Reset(board);
        Assert.Equal(a.Evaluate(board), b.Evaluate(board));
    }

    // A model claiming int8 with QA above the saturation bound would evaluate
    // silently wrong positions. It must be refused at load, not tolerated.
    [Fact]
    public void Loader_RejectsInt8ModelWithUnsafeQa()
    {
        var net = CreateTestNetworkInt8();
        byte[] bytes = Serialize(net);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 255); // QA = 255

        Assert.False(NnueModelLoader.TryParse(bytes, out _, out string error));
        Assert.Contains("saturation", error);
    }

    // Legacy nets must keep loading unchanged: a format change that stranded
    // the net currently playing would be a regression, not an upgrade.
    [Fact]
    public void Loader_StillAcceptsLegacyInt16Architecture()
    {
        var original = CreateTestNetwork();
        Assert.True(NnueModelLoader.TryParse(Serialize(original), out NnueNetwork? loaded, out string error), error);
        Assert.Equal(NnueModelHeader.ArchitectureInt16L1, loaded!.ArchitectureId);
        Assert.False(loaded.UsesInt8L1);
        Assert.Null(loaded.L1WeightsI8);
        Assert.Equal(original.L1Weights, loaded.L1Weights);
        Assert.Equal(1, loaded.OutputBuckets);
        Assert.False(loaded.UsesOutputBuckets);
    }

    // ---------- v4.2.0: output buckets (architecture 3) ----------

    private static NnueNetwork CreateTestNetworkBuckets(
        int seed = 1234, int ftOut = 32, int l1Out = 8, int buckets = 8)
    {
        var rng = new Random(seed);
        short RandW(int range) => (short)rng.Next(-range, range + 1);

        var net = new NnueNetwork
        {
            ArchitectureId = NnueModelHeader.ArchitectureInt8L1Buckets,
            FtInputs = NnueFeatureIndex.InputSize,
            FtOutputs = ftOut,
            L1Outputs = l1Out,
            OutputBuckets = buckets,
            QA = 127,
            QB = 64,
            OutputScale = 400,
            FtWeights = new short[NnueFeatureIndex.InputSize * ftOut],
            FtBias = new short[ftOut],
            L1WeightsI8 = new sbyte[buckets * l1Out * 2 * ftOut],
            L1Bias = new int[buckets * l1Out],
            OutWeights = new short[buckets * l1Out],
            OutBias = new int[buckets],
            Sha256 = "test-buckets"
        };

        for (int i = 0; i < net.FtWeights.Length; i++) net.FtWeights[i] = RandW(60);
        for (int i = 0; i < net.FtBias.Length; i++) net.FtBias[i] = RandW(100);
        for (int i = 0; i < net.L1WeightsI8!.Length; i++) net.L1WeightsI8[i] = (sbyte)rng.Next(-127, 128);
        for (int i = 0; i < net.L1Bias.Length; i++) net.L1Bias[i] = rng.Next(-5000, 5000);
        for (int i = 0; i < net.OutWeights.Length; i++) net.OutWeights[i] = RandW(100);
        for (int i = 0; i < net.OutBias.Length; i++) net.OutBias[i] = rng.Next(-1000, 1000);
        return net;
    }

    // Bucket selection is duplicated in the C# engine and the Python trainer.
    // If the two formulas ever disagree, every evaluation reads a head the net
    // was not trained for - a silent, catastrophic mismatch. These values pin
    // the C# side; tools/training/nnue/model.py must produce the same.
    [Fact]
    public void BucketForPieceCount_GoldenValues()
    {
        // 8 buckets: (count - 1) * 8 / 32 == (count - 1) / 4.
        Assert.Equal(0, NnueModelHeader.BucketForPieceCount(2, 8));   // bare kings
        Assert.Equal(0, NnueModelHeader.BucketForPieceCount(4, 8));
        Assert.Equal(1, NnueModelHeader.BucketForPieceCount(5, 8));
        Assert.Equal(1, NnueModelHeader.BucketForPieceCount(8, 8));
        Assert.Equal(2, NnueModelHeader.BucketForPieceCount(9, 8));
        Assert.Equal(7, NnueModelHeader.BucketForPieceCount(32, 8));  // full board

        // An unbucketed net always selects head 0, whatever the piece count.
        Assert.Equal(0, NnueModelHeader.BucketForPieceCount(32, 1));
        Assert.Equal(0, NnueModelHeader.BucketForPieceCount(2, 1));

        // Never out of range, including inputs the engine should never produce.
        for (int count = 0; count <= 40; count++)
            for (int buckets = 1; buckets <= 16; buckets++)
                Assert.InRange(NnueModelHeader.BucketForPieceCount(count, buckets), 0, buckets - 1);
    }

    [Fact]
    public void Buckets_ScalarMatchesSimd_OverRandomGames()
    {
        var net = CreateTestNetworkBuckets();
        var rng = new Random(31);
        var board = new Board();
        var acc = new NnueAccumulator(net.FtOutputs);

        for (int plyCount = 0; plyCount < 140; plyCount++)
        {
            acc.Refresh(net, board, Color.White);
            acc.Refresh(net, board, Color.Black);
            int stm = (int)board.SideToMove;
            int bucket = NnueModelHeader.BucketForPieceCount(
                System.Numerics.BitOperations.PopCount(board.AllOccupancy), net.OutputBuckets);

            Assert.Equal(
                NnueInference.EvaluateScalar(net, acc.Values[stm], acc.Values[1 - stm], bucket),
                NnueInference.EvaluateSimd(net, acc.Values[stm], acc.Values[1 - stm], bucket));

            if (GameState.GetResult(board) != GameResult.Ongoing)
                break;
            var moves = MoveGenerator.GenerateLegalMoves(board);
            board.MakeMove(moves[rng.Next(moves.Count)]);
        }
    }

    // Different buckets must actually produce different evaluations, otherwise
    // the head is being read from the same slice and the whole feature is a
    // no-op that would still pass every parity test above.
    [Fact]
    public void Buckets_DifferentBucketsGiveDifferentEvaluations()
    {
        var net = CreateTestNetworkBuckets();
        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        var acc = new NnueAccumulator(net.FtOutputs);
        acc.Refresh(net, board, Color.White);
        acc.Refresh(net, board, Color.Black);

        var scores = new HashSet<int>();
        for (int bucket = 0; bucket < net.OutputBuckets; bucket++)
            scores.Add(NnueInference.EvaluateSimd(net, acc.Values[0], acc.Values[1], bucket));

        Assert.True(scores.Count > 1,
            "every bucket produced the same score - the head is not actually bucketed");
    }

    // A one-bucket arch-3 net must evaluate exactly like the arch-2 net built
    // from the same weights: the bucket machinery has to vanish when unused.
    [Fact]
    public void Buckets_SingleBucketMatchesUnbucketedArchitecture()
    {
        var bucketed = CreateTestNetworkBuckets(seed: 77, buckets: 1);
        var plain = new NnueNetwork
        {
            ArchitectureId = NnueModelHeader.ArchitectureInt8L1,
            FtInputs = bucketed.FtInputs,
            FtOutputs = bucketed.FtOutputs,
            L1Outputs = bucketed.L1Outputs,
            QA = bucketed.QA,
            QB = bucketed.QB,
            OutputScale = bucketed.OutputScale,
            FtWeights = bucketed.FtWeights,
            FtBias = bucketed.FtBias,
            L1WeightsI8 = bucketed.L1WeightsI8,
            L1Bias = bucketed.L1Bias,
            OutWeights = bucketed.OutWeights,
            OutBias = bucketed.OutBias,
            Sha256 = "plain"
        };

        var board = new Board("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
        var a = new NnueEvaluator(bucketed); a.Reset(board);
        var b = new NnueEvaluator(plain); b.Reset(board);
        Assert.Equal(b.Evaluate(board), a.Evaluate(board));
    }

    [Fact]
    public void Loader_RoundTripsABucketedModel()
    {
        var original = CreateTestNetworkBuckets();
        Assert.True(NnueModelLoader.TryParse(Serialize(original), out NnueNetwork? loaded, out string error), error);

        Assert.Equal(NnueModelHeader.ArchitectureInt8L1Buckets, loaded!.ArchitectureId);
        Assert.Equal(original.OutputBuckets, loaded.OutputBuckets);
        Assert.True(loaded.UsesOutputBuckets);
        Assert.True(loaded.UsesInt8L1);
        Assert.Equal(original.L1WeightsI8, loaded.L1WeightsI8);
        Assert.Equal(original.L1Bias, loaded.L1Bias);
        Assert.Equal(original.OutWeights, loaded.OutWeights);
        Assert.Equal(original.OutBias, loaded.OutBias);

        var board = new Board();
        var a = new NnueEvaluator(original); a.Reset(board);
        var b = new NnueEvaluator(loaded); b.Reset(board);
        Assert.Equal(a.Evaluate(board), b.Evaluate(board));
    }

    // Declaring buckets on an architecture that has no room for them means the
    // payload size and the head indexing disagree; refuse rather than misread.
    [Fact]
    public void Loader_RejectsBucketsOnUnbucketedArchitecture()
    {
        byte[] bytes = Serialize(CreateTestNetworkInt8());
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(38), 8);

        Assert.False(NnueModelLoader.TryParse(bytes, out _, out string error));
        Assert.Contains("bucket", error);
    }
}
