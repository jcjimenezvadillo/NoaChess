using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// NNUE position evaluator: feature-transformer accumulators updated
// incrementally by the search, then a small integer network on top.
// Returns centipawns from the side to move's point of view, same contract
// as ClassicalEvaluator.
public sealed class NnueEvaluator : IIncrementalEvaluator
{
    // Evaluations must stay clearly below the search's mate-score band.
    private const int EvalClamp = 30_000;

    private readonly NnueNetwork _network;
    private readonly NnueAccumulatorStack _accumulators;
    private readonly bool _useSimd;

    public NnueEvaluator(NnueNetwork network)
    {
        _network = network;
        _accumulators = new NnueAccumulatorStack(network);
        // The SIMD kernel processes whole vector lanes; fall back to scalar
        // for widths that do not divide evenly (never the case for the
        // shipped architectures, but never trust a file).
        _useSimd = NnueInference.SimdAvailable
                   && network.FtOutputs % System.Numerics.Vector<short>.Count == 0;
    }

    public string ModelSha256 => _network.Sha256;
    public bool UsesSimd => _useSimd;

    public int Evaluate(Board board)
    {
        Color stm = board.SideToMove;
        short[] stmAcc = _accumulators.GetPerspective(board, stm);
        short[] oppAcc = _accumulators.GetPerspective(board, Board.OppositeColor(stm));

        int score = _useSimd
            ? NnueInference.EvaluateSimd(_network, stmAcc, oppAcc)
            : NnueInference.EvaluateScalar(_network, stmAcc, oppAcc);

        return Math.Clamp(score, -EvalClamp, EvalClamp);
    }

    public void Reset(Board board) => _accumulators.Reset(board);
    public void PushMove(Board board, Move move) => _accumulators.PushMove(board, move);
    public void PushNull() => _accumulators.PushNull();
    public void Pop() => _accumulators.Pop();
}
