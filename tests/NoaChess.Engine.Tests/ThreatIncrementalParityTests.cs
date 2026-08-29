using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// The incremental threat accumulator against a full refresh, AT EVERY NODE of a
// real search.
//
// WHY THIS TEST AND NOT THE OTHER ONE. ThreatDeltaTests proves the delta is the
// right set of features for one move from one position. That is necessary and
// nowhere near sufficient: the accumulator applies those deltas in a CHAIN,
// down and back up a search tree, across king moves that force a rebuild, null
// moves that change nothing, and pseudo-legal moves that get unmade the instant
// they are found illegal. A delta that is individually correct can still leave
// the chain wrong if a level is materialised at the wrong moment or a Pop
// forgets something.
//
// And the failure mode is the reason this is worth the runtime: a wrong
// accumulator does not throw and does not lose a game. It evaluates a position
// that does not exist, quietly, while every other test stays green. This is the
// same check that caught the lazy HalfKA accumulator being subtly wrong.
public class ThreatIncrementalParityTests
{
    private static readonly string[] Positions =
    [
        "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQK2R w KQkq - 0 6",
        "r3k2r/pppq1ppp/2n1bn2/3pp3/3PP3/2N1BN2/PPPQ1PPP/R3K2R w KQkq - 0 9",
        "3r1rk1/1pq2ppp/p1nbpn2/8/2BP4/2N1PN2/PPQ2PPP/3R1RK1 w - - 0 15",
        "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
    ];

    // Shape-accurate and deterministic. The values do not matter: what is under
    // test is whether two ways of reaching the same accumulator agree, so any
    // fixed weights answer it, and fixed ones make a failure reproducible.
    private static NnueNetwork SyntheticThreatNet(int ftOut = 32, int l1Out = 8)
    {
        var rng = new Random(20260815);
        short W(int range) => (short)rng.Next(-range, range + 1);

        var net = new NnueNetwork
        {
            ArchitectureId = NnueModelHeader.ArchitectureThreats,
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
            OutBias = [0],
            ThreatWeights = new short[ThreatFeatureIndex.InputSize * ftOut],
            Sha256 = "parity"
        };

        for (int i = 0; i < net.FtWeights.Length; i++) net.FtWeights[i] = W(30);
        for (int i = 0; i < net.FtBias.Length; i++) net.FtBias[i] = W(50);
        for (int i = 0; i < net.L1WeightsI8!.Length; i++) net.L1WeightsI8[i] = (sbyte)rng.Next(-60, 61);
        for (int i = 0; i < net.L1Bias.Length; i++) net.L1Bias[i] = rng.Next(-2000, 2000);
        for (int i = 0; i < net.OutWeights.Length; i++) net.OutWeights[i] = W(80);
        for (int i = 0; i < net.ThreatWeights!.Length; i++) net.ThreatWeights[i] = W(30);
        return net;
    }

    [Fact]
    public void IncrementalMatchesRefreshAtEveryNodeOfASearch()
    {
        NnueNetwork net = SyntheticThreatNet();
        var stack = new NnueAccumulatorStack(net);
        var reference = new NnueAccumulator(net.FtOutputs);

        int nodes = 0, kingMoves = 0, nulls = 0;
        var failures = new List<string>();

        void Check(Board board, string trail)
        {
            foreach (Color perspective in new[] { Color.White, Color.Black })
            {
                short[] incremental = stack.GetPerspective(board, perspective);
                reference.Refresh(net, board, perspective);
                short[] truth = reference.Values[(int)perspective];

                for (int i = 0; i < truth.Length; i++)
                {
                    if (incremental[i] == truth[i])
                        continue;
                    if (failures.Count < 5)
                        failures.Add($"{perspective} en [{i}]: incremental {incremental[i]} "
                                   + $"contra refresco {truth[i]}\n    linea: {trail}");
                    break;
                }
            }
            nodes++;
        }

        // A small fixed-depth walk rather than the engine's search: it has to
        // cover make, unmake, null moves and king moves in a chain, and doing it
        // here keeps the test independent of whatever the search decides to
        // prune on the day.
        // CHECKED AT THE LEAVES ONLY, and that is the whole point rather than an
        // optimisation.
        //
        // The first version of this test evaluated at every node, and a
        // deliberate sabotage - removing the king-move refresh from
        // CompleteThreatDelta - still PASSED. Asking for an evaluation
        // materialises the level, so checking everywhere quietly guaranteed
        // that every parent was materialised before its child was pushed. The
        // real search does not do that: laziness is the reason the class
        // exists, so a level really can be pushed from an ancestor nobody has
        // evaluated. Only checking at the leaves reproduces that.
        void Walk(Board board, int depth, string trail)
        {
            if (depth == 0)
            {
                Check(board, trail);
                return;
            }

            // A null move exercises the level that records no change at all.
            if (depth == 2)
            {
                stack.PushNull();
                board.MakeNullMove();
                nulls++;
                Walk(board, 0, trail + " null");
                board.UnmakeNullMove();
                stack.Pop();
            }

            foreach (Move move in MoveGenerator.GenerateLegalMoves(board).ToArray())
            {
                if (board.PieceTypeAt(move.From) == PieceType.King)
                    kingMoves++;

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

        Assert.True(nodes > 1500, $"solo {nodes} nodos visitados, muestra insuficiente");
        Assert.True(kingMoves > 0, "ningun movimiento de rey: el camino de refresco entero no se probo");
        Assert.True(nulls > 0, "ninguna jugada nula probada");
        Assert.True(failures.Count == 0,
            $"el acumulador incremental difiere del refresco en {failures.Count} sitios "
          + $"de {nodes} nodos ({kingMoves} jugadas de rey, {nulls} nulas):\n"
          + string.Join("\n", failures));
    }
}
