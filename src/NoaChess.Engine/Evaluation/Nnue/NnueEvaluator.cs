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

    // ---- MEASURED AND CUT (2026-07-31): NNUE-to-classical scale alignment ----
    //
    // Rescaling this evaluator's output to "align" it with the classical
    // centipawn scale was tried and LOST decisively: 1250 permille measured
    // 144-261-261 [0.412] over 666 games, -61.7 +/- 20.7 Elo, LOS 0.0%, H0
    // accepted (10+0.1, proven-mate stop in both arms). Negative from the first
    // sample and monotone. The knob is gone; do not reintroduce it.
    //
    // The scale measurement itself was right — the net IS compressed, see the
    // numbers below — but the conclusion was wrong, for two reasons:
    //   1. The pruning margins are ALREADY calibrated to the compressed net in
    //      practice: gen3 through gen7 were every one of them SPRT-validated
    //      with it, so the shipped combination is the empirically tuned one.
    //      "Fixing" the scale broke a calibration that worked.
    //   2. Inflating the eval makes pruning MORE aggressive. RFP fires on
    //      staticEval - margin >= beta, so a 25% larger eval trips it far more
    //      often (same for razoring and futility), producing unsound cutoffs.
    //      A compressed eval against fixed margins is equivalent to LARGER
    //      margins — safer pruning — and the engine prefers that.
    //
    // Measured 2026-07-30 over 6000 real positions from the human opening book,
    // regressing gen7 static eval on the classical evaluator: global slope
    // 0.783, mean|nnue| 95.7 vs mean|classical| 114.0 (ratio 0.84, matching the
    // training pipeline's own validate slope of 0.840).
    //
    // Method note for whoever measures a future net: do NOT calibrate on
    // artificial material positions (removing a piece from the start position).
    // That was tried first and gave a confident but WRONG "1.29x inflated"
    // reading in the opposite direction — such positions are far outside the
    // net's training distribution, so the two evaluators simply disagree there
    // rather than differing by scale.
    //
    // The rationale that motivated it, for the record: every pruning constant
    // (QsFutilityMargin=147, ProbCut margins, razoring, the quiescence victim
    // values [100,320,330,500,900], aspiration deltas) is expressed on the
    // CLASSICAL centipawn scale and several are compared directly against this
    // evaluator's output. That mismatch is REAL — it is simply not worth
    // correcting here, because the search was tuned around it.

    private readonly NnueNetwork _network;
    private readonly NnueAccumulatorStack _accumulators;
    private readonly bool _useSimd;

    public NnueEvaluator(NnueNetwork network)
    {
        _network = network;
        _accumulators = new NnueAccumulatorStack(network);
        // The SIMD kernels process whole vector lanes; fall back to scalar for
        // widths that do not divide evenly (never the case for the shipped
        // architectures, but never trust a file). The int8 path packs 32 bytes
        // at a time and carries its own portable fallback, so it only needs the
        // accumulator-clipping width to line up.
        _useSimd = NnueInference.SimdAvailable
                   && (network.UsesInt8L1
                       || network.FtOutputs % System.Numerics.Vector<short>.Count == 0);
    }

    public string ModelSha256 => _network.Sha256;
    public bool UsesSimd => _useSimd;

    // Exposed for the `nnueprofile` command, which needs the loaded weights to
    // time the primitives in isolation. Read-only after load.
    public NnueNetwork Network => _network;

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

    // Shares the read-only network (weights never change after load) but gives
    // the clone its OWN accumulator stack, so a helper search thread updates
    // its accumulators independently of every other thread.
    public IPositionEvaluator Clone() => new NnueEvaluator(_network);
}
