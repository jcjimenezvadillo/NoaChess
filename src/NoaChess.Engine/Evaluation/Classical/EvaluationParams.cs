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
    // Mobility keeps the HAND values (SPRT-validated in v2.2.0) and is
    // excluded from texel tuning: every tuning run, on old AND fresh data,
    // converges to negative endgame mobility for the minors — a spurious
    // correlation (the winning side simplifies and restricts enemy mobility,
    // so low own-mobility correlates with winning), and playing by it makes
    // the engine cage its own pieces in endgames.
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
    // Non-readonly on purpose: the texel tuner (tools/NoaChess.Tuner) adjusts
    // these in-process and the engine never mutates them itself.
    // Values below are texel-tuned (v2.4.0) on fresh v2.3.0-strength self-play
    // data (noa-2.4.0-tune.noadata), jointly with the PSTs.
    public static Score BishopPair = new(44, 68);
    public static Score RookOpenFile = new(58, -16);    // no pawns at all
    public static Score RookSemiOpenFile = new(25, 18); // no friendly pawns
    // A rook on the 7th rank cuts the enemy king off and attacks its pawns.
    public static Score RookOnSeventh = new(19, 17);

    // ---- Knight outposts ----
    // A knight on a hole in the enemy camp (relative ranks 4-6, protected by a
    // friendly pawn, no enemy pawn can ever kick it) is a permanent asset.
    public static Score KnightOutpost = new(51, 18);

    // ---- Space ----
    // Per safe central square (files c-f, relative ranks 2-4, not occupied by
    // a friendly pawn, not attacked by enemy pawns). Middlegame term; the
    // tuned endgame half is slightly negative (space is a liability once the
    // pieces that would use it are gone).
    public static Score SpacePerSquare = new(2, -8);

    // ---- Tempo ----
    // The side to move has the initiative; applied directly to the negamax
    // score so it is always positive for the evaluee. Not tunable: a constant
    // offset has no gradient in the texel loss and would just shift K.
    public const int Tempo = 14;

    // ---- Pawn structure ----
    public static Score DoubledPawn = new(-14, -14);
    public static Score IsolatedPawn = new(-10, -12);
    // Phalanx: two friendly pawns side-by-side (same rank, adjacent file)
    // protect each other's advance. Bonus grows with rank because a phalanx
    // on the 5th rank is a real threat; on the 2nd it is just potential.
    // Rank-indexed (0 = own back rank, 7 unused).
    public static Score[] Phalanx =
    [
        new(0, 0), new(3, 0), new(4, 5), new(13, 12),
        new(16, 26), new(44, 34), new(64, 54), new(0, 0),
    ];
    // Backward pawn: cannot advance safely (stop square attacked by an enemy
    // pawn) and has no friendly pawn on adjacent files behind it that could
    // advance to give support. Not as severe as isolated because the pawn
    // can still be defended by pieces.
    public static Score BackwardPawn = new(-12, -6);
    // Passed pawn bonus by RELATIVE rank (0 = own back rank, 7 unused). Endgame
    // heavy: a passer wins games once the pieces come off.
    public static Score[] PassedPawn =
    [
        new(0, 0), new(-7, 30), new(-6, 30), new(-5, 59),
        new(21, 81), new(52, 84), new(36, 92), new(0, 0),
    ];
    // Connected passers on adjacent files defend each other's promotion path;
    // applied per passer that has a friendly passer on an adjacent file.
    public static Score ConnectedPassers = new(-8, 10);
    // Rook behind its own passed pawn (Tarrasch): pushes the pawn and never
    // blocks it. Applied per (rook, passer) pair on the same file.
    public static Score RookBehindPasser = new(12, 14);
    // A passer whose stop square is occupied by an enemy piece is going
    // nowhere for now: give back a third of the rank bonus.
    public const int BlockedPasserDivisor = 3;
}
