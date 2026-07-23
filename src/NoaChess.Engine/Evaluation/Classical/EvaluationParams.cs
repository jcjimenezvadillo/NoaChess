namespace NoaChess.Engine.Evaluation.Classical;

// All tunable evaluation constants in one place. Centralizing them (instead
// of scattering magic numbers through the evaluators) prepares the ground for
// automated parameter tuning (Optuna / SPRT, per the roadmap): a tuner only
// needs to touch this file.
//
// Every positional term now carries a middlegame (Mg) and an endgame (Eg)
// value, blended at the end by the game phase (see Score / ClassicalEvaluator).
public static class EvaluationParams
{
    // ---- Material (centipawns), indexed by PieceType. The king has no
    //      material value: it is never captured. Values are the PeSTO set,
    //      calibrated together with the piece-square tables below. ----
    public static readonly int[] MaterialMg = [82, 337, 365, 477, 1025, 0];
    public static readonly int[] MaterialEg = [94, 281, 297, 512, 936, 0];

    // Game-phase weight of each piece type. Summed over all pieces on the board
    // it yields 24 at the start (pure middlegame) and 0 with only kings/pawns
    // (pure endgame). Pawns and kings do not shift the phase.
    public static readonly int[] PhaseWeight = [0, 1, 1, 2, 4, 0];
    public const int PhaseMax = 24;

    // ---- Mobility ----
    // Each piece scores by how many squares it can move to, excluding squares
    // occupied by friendly pieces and squares attacked by enemy pawns (going
    // there just loses the piece). Centered around a typical count so the term
    // averages near zero and does not silently inflate the material values.
    public static readonly int[] MobilityBaseline = [0, 4, 6, 7, 14, 0]; // by PieceType
    public static readonly Score[] MobilityStep =
    [
        default,          // pawn (no mobility term)
        new(4, 4),        // knight
        new(3, 3),        // bishop
        new(2, 4),        // rook (open lines matter more in the endgame)
        new(1, 2),        // queen
        default,          // king
    ];

    // ---- King safety ----
    // Attack weight per enemy piece type for each king-zone square it attacks.
    // The accumulated "attack units" index a quadratic danger curve.
    public static readonly int[] KingAttackWeight = [0, 2, 2, 3, 5, 0]; // by PieceType
    // Extra units for each of the three files around the king with no friendly
    // pawn ahead of it (a missing pawn shield / open file towards the king).
    public const int OpenFileNearKingUnits = 3;
    // Danger curve: penalty = min(units^2 / Divisor, Cap), applied to the
    // middlegame score only (a king in the open is fine once the queens go).
    public const int KingDangerDivisor = 6;
    public const int KingDangerCap = 500;

    // ---- Minor positional terms ----
    public static readonly Score BishopPair = new(22, 40);
    public static readonly Score RookOpenFile = new(30, 12);     // no pawns at all
    public static readonly Score RookSemiOpenFile = new(15, 6);  // no friendly pawns

    // ---- Pawn structure ----
    public static readonly Score DoubledPawn = new(-10, -20);
    public static readonly Score IsolatedPawn = new(-14, -8);
    // Passed pawn bonus by RELATIVE rank (0 = own back rank, 7 unused). Endgame
    // heavy: a passer wins games once the pieces come off.
    public static readonly Score[] PassedPawn =
    [
        new(0, 0), new(5, 10), new(10, 20), new(15, 35),
        new(25, 55), new(40, 80), new(60, 120), new(0, 0),
    ];
}
