namespace NoaChess.Engine.Evaluation.Classical;

// All tunable evaluation constants in one place. Centralizing them (instead
// of scattering magic numbers through the evaluators) prepares the ground for
// automated parameter tuning (Optuna / SPRT, per the roadmap): a tuner only
// needs to touch this file.
public static class EvaluationParams
{
    // Material values in centipawns (index = PieceType). The king has no
    // material value: it is never captured (mate is detected in the search).
    public static readonly int[] MaterialValue = [100, 320, 330, 500, 900, 0];

    // ---- Pawn structure (all values in centipawns, from the pawn owner's
    //      point of view; penalties are negative) ----

    // Each pawn beyond the first on the same file. Doubled pawns cannot
    // defend each other and block their own file.
    public const int DoubledPawnPenalty = -15;

    // Pawn with no friendly pawns on adjacent files: it can never be defended
    // by another pawn and the squares in front of it are weak.
    public const int IsolatedPawnPenalty = -12;

    // Passed pawn (no enemy pawns ahead on its own or adjacent files), by
    // RELATIVE rank (index 0 = own back rank, 7 unused). The bonus grows fast:
    // a passer two squares from promotion dominates an endgame.
    public static readonly int[] PassedPawnBonus = [0, 10, 15, 25, 40, 65, 100, 0];
}
