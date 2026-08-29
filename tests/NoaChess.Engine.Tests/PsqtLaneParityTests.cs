using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;
using Xunit;

namespace NoaChess.Engine.Tests;

// The psqt lane has the same silent failure mode as every accumulator path:
// a missed update produces a plausible number, no exception, and a corrupted
// evaluation. The oracle is the same one the threat parity test uses - a full
// Refresh from the board - and the walk is the same shape: make, unmake, null
// moves and king moves in a chain, CHECKED AT THE LEAVES ONLY, because
// materialising every level would guarantee parents are computed before
// children and hide exactly the chain bugs this hunts.
public class PsqtLaneParityTests
{
    private static readonly string[] Positions =
    [
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    ];

    private static NnueNetwork SyntheticPsqtNet(int ftOut = 32, int psqtBuckets = 2)
    {
        var rng = new Random(20260825);

        var net = new NnueNetwork
        {
            ArchitectureId = NnueModelHeader.ArchitectureInt16L1,
            FtInputs = NnueFeatureIndex.InputSize,
            FtOutputs = ftOut,
            L1Outputs = 8,
            QA = 255,
            QB = 64,
            OutputScale = 400,
            FtWeights = new short[NnueFeatureIndex.InputSize * ftOut],
            FtBias = new short[ftOut],
            L1Weights = new short[8 * 2 * ftOut],
            L1Bias = new int[8],
            OutWeights = new short[8],
            OutBias = [0],
            PsqtWeights = psqtBuckets > 0 ? new int[NnueFeatureIndex.InputSize * psqtBuckets] : null,
            PsqtBuckets = psqtBuckets,
            QbShift = 6,
            SquaredActivation = new byte[256],
            Sha256 = "parity",
        };

        // Non-trivial psqt weights: distinct per feature and per bucket, large
        // enough that any missed update moves the sum by an obvious amount.
        if (net.PsqtWeights is not null)
            for (int i = 0; i < net.PsqtWeights.Length; i++)
                net.PsqtWeights[i] = rng.Next(-5000, 5001);
        for (int i = 0; i < net.FtWeights.Length; i++)
            net.FtWeights[i] = (short)rng.Next(-40, 41);
        return net;
    }

    [Fact]
    public void IncrementalLaneMatchesRefreshAtTheLeaves()
    {
        NnueNetwork net = SyntheticPsqtNet();
        var stack = new NnueAccumulatorStack(net);
        var reference = new NnueAccumulator(net.FtOutputs);
        var failures = new List<string>();
        int leaves = 0;

        void Check(Board board, string trail)
        {
            // Materialise both perspectives the way an evaluation would, THEN
            // compare the lane against a from-scratch refresh.
            stack.GetPerspective(board, Color.White);
            stack.GetPerspective(board, Color.Black);
            reference.Refresh(net, board, Color.White);
            reference.Refresh(net, board, Color.Black);

            for (int b = 0; b < net.PsqtBuckets; b++)
            {
                int stm = (int)board.SideToMove * NnueAccumulator.MaxPsqtBuckets + b;
                int opp = (1 - (int)board.SideToMove) * NnueAccumulator.MaxPsqtBuckets + b;
                int truth = (reference.Psqt[stm] - reference.Psqt[opp]) / 2;
                int incremental = stack.PsqtDiff(board.SideToMove, b);
                if (truth != incremental && failures.Count < 5)
                    failures.Add($"bucket {b}: incremental {incremental} vs refresh {truth}\n    line: {trail}");
            }
            leaves++;
        }

        void Walk(Board board, int depth, string trail)
        {
            if (depth == 0)
            {
                Check(board, trail);
                return;
            }

            if (depth == 2)
            {
                stack.PushNull();
                board.MakeNullMove();
                Walk(board, 0, trail + " null");
                board.UnmakeNullMove();
                stack.Pop();
            }

            foreach (Move move in MoveGenerator.GenerateLegalMoves(board).ToArray())
            {
                stack.PushMove(board, move);
                board.MakeMove(move);
                stack.CompleteThreatDelta(board);

                Walk(board, depth - 1, $"{trail} {move.From}->{move.To}");

                board.UnmakeMove();
                stack.Pop();
            }
        }

        foreach (string fen in Positions)
        {
            var board = new Board(fen);
            stack.Reset(board);
            Walk(board, 2, fen);
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} divergences over {leaves} leaves:\n" + string.Join("\n", failures));
        Assert.True(leaves > 1000, $"walk only reached {leaves} leaves; the test is not exercising enough");
    }

    [Fact]
    public void LaneStaysZeroWithoutPsqtHead()
    {
        NnueNetwork net = SyntheticPsqtNet(psqtBuckets: 0);
        var board = new Board(Positions[1]);
        var acc = new NnueAccumulator(net.FtOutputs);
        acc.Refresh(net, board, Color.White);
        acc.Refresh(net, board, Color.Black);
        foreach (int v in acc.Psqt)
            Assert.Equal(0, v);
    }
}
