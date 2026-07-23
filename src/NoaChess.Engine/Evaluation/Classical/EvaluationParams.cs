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

    // ---- Mobility (reference MobilityBonus, rescaled x0.48, re-centered) ----
    // Non-linear lookup indexed by [pieceType - knight][number of attacked
    // squares inside the mobility area]. The old linear model underpriced the
    // caged end of the curve: going from 2 to 3 knight squares matters far
    // more than going from 7 to 8. Values are the reference tables times
    // 100/208 (the reference-to-NoaChess unit scale), then re-centered by
    // subtracting the entry at the typical mobility count (knight 4, bishop 6,
    // rook 7, queen 14 — the SPRT-validated baselines of the old linear term).
    // The raw tables carry a big positive offset at typical mobility (rook +59
    // eg, queen +63 eg) that the reference absorbs in its own piece values;
    // injected as-is it silently inflates NoaChess's texel-tuned material
    // balance. Centering keeps the non-linear SHAPE (the signal) with ~zero avg.
    // NOT texel-tunable: every tuning run converges to negative endgame
    // mobility via spurious correlation (the winning side simplifies and
    // restricts enemy mobility).
    public static readonly Score[][] MobilityBonus =
    [
        [ // Knight (9 entries). ref: (-62,-79)(-53,-57)(-12,-31)(-3,-17)(3,7)(12,13)(21,16)(28,21)(37,26)
            new(-31, -41), new(-26, -30), new(-7, -18), new(-2, -11), new(0, 0),
            new(5, 3), new(9, 5), new(12, 7), new(17, 10),
        ],
        [ // Bishop (14). ref: (-47,-59)(-20,-25)(14,-8)(29,12)(39,21)(53,40)(53,56)(60,58)(62,65)(69,72)(78,78)(83,87)(91,88)(96,98)
            new(-48, -55), new(-35, -39), new(-18, -31), new(-11, -21), new(-6, -17),
            new(0, -8), new(0, 0), new(4, 1), new(5, 4), new(8, 8),
            new(13, 11), new(15, 15), new(19, 15), new(21, 20),
        ],
        [ // Rook (15). ref: (-60,-82)(-24,-15)(0,17)(3,43)(4,72)(14,100)(20,102)(30,122)(41,133)(41,139)(41,153)(45,160)(57,165)(58,170)(67,175)
            new(-43, -98), new(-26, -66), new(-14, -51), new(-13, -38), new(-12, -24),
            new(-7, -11), new(-4, -10), new(0, 0), new(6, 5), new(6, 8),
            new(6, 15), new(8, 18), new(13, 20), new(14, 23), new(18, 25),
        ],
        [ // Queen (28). ref: (-29,-49)(-16,-29)(-8,-8)(-8,17)(18,39)(25,54)(23,59)(37,73)(41,76)(54,95)(65,95)(68,101)(69,124)(70,128)(70,132)(70,133)(71,136)(72,140)(74,147)(76,149)(90,153)(104,169)(105,171)(106,171)(112,178)(114,185)(114,187)(119,221)
            new(-48, -87), new(-42, -77), new(-38, -67), new(-38, -55), new(-25, -44),
            new(-22, -37), new(-23, -35), new(-16, -28), new(-14, -26), new(-8, -17),
            new(-3, -17), new(-1, -14), new(-1, -3), new(0, -1), new(0, 0),
            new(0, 1), new(0, 2), new(1, 4), new(2, 8), new(3, 9),
            new(9, 11), new(16, 18), new(16, 19), new(17, 19), new(20, 23),
            new(21, 26), new(21, 27), new(23, 43),
        ],
    ];

    // ---- King safety (reference king-safety system + pawn shelter) ----
    // The danger accumulator works in RAW internal units on purpose: the
    // quadratic transform (danger^2/4096) and every constant below come from
    // the reference jointly-tuned system, so the whole king-safety Score is
    // computed in internal units first and converted to NoaChess centipawns
    // (x0.48) once at the end. No re-centering needed: each side has exactly
    // one king, so any constant offset cancels in the White-minus-Black
    // subtraction. Safe/unsafe check terms are deliberately NOT ported (the
    // v2.4.6 failure was a safe-check mask bug); a possible future sub-block.

    // Attack weight per attacking piece type (raw units).
    public static readonly int[] KingAttackWeights = [0, 76, 46, 45, 14, 0]; // by PieceType

    // Shelter strength by [file distance from edge][relative rank of our pawn].
    // Rank 0 = no pawn on that file (or pawn behind the king). Raw values.
    public static readonly int[][] ShelterStrength =
    [
        [-2, 85, 95, 53, 39, 23, 25, 0],
        [-55, 64, 32, -55, -30, -11, -61, 0],
        [-11, 75, 19, -6, 26, 9, -47, 0],
        [-41, -11, -27, -58, -42, -66, -163, 0],
    ];

    // Enemy pawn storm by [file distance from edge][relative rank of their
    // pawn]. Rank 0 = no enemy pawn on that file. Raw values.
    public static readonly int[][] UnblockedStorm =
    [
        [94, -280, -170, 90, 59, 47, 53, 0],
        [43, -17, 128, 39, 26, -17, 15, 0],
        [-9, 62, 170, 34, -5, -20, -11, 0],
        [-27, -19, 106, 10, 2, -13, -24, 0],
    ];

    // Reduced storm penalty when our pawn blocks theirs (their pawn directly
    // in front of ours), indexed by THEIR pawn's relative rank. Raw values.
    public static readonly Score[] BlockedStorm =
    [
        default, default, new(64, 75), new(-3, 14), new(-12, 19), new(-7, 4), new(-10, 5), default,
    ];

    // King standing on a file: [our file is semi-open][their file is semi-open]
    // (semi-open for a color = that color has no pawn on the file). Subtracted
    // from the shelter score. Raw values.
    public static readonly Score[,] KingOnFile = new Score[2, 2]
    {
        { new(-18, 11), new(-6, -3) },
        { new(0, 0), new(5, -4) },
    };

    // Penalty when the king sits on a flank with no pawns of either color, and
    // per-attack penalty on the king's flank. Raw values.
    public static readonly Score PawnlessFlank = new(19, 97);
    public static readonly Score FlankAttacks = new(8, 0);

    // Bonus in the piece loop for a rook/bishop aimed at the enemy king ring
    // (even with pieces in the way). These live in normal evaluation space, so
    // they are stored x0.48. ref: RookOnKingRing (16,0), BishopOnKingRing (24,0).
    public static Score RookOnKingRing = new(8, 0);
    public static Score BishopOnKingRing = new(12, 0);

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

    // ---- 4E piece terms — scaled ×0.48 from reference ----

    // TrappedRook: rook on the home rank with ≤3 mobility squares is a
    // positional error. Applied ×1 when castling rights remain, ×2 when not.
    // ref S(55,13) ×0.48.
    public static Score TrappedRook = new(26, 6);

    // RookOnClosedFile: penalty (stored positive, subtracted) for a rook on a
    // file whose own pawn is blocked. ref S(10,5) ×0.48.
    public static Score RookOnClosedFile = new(5, 2);

    // LongDiagonalBishop: bishop can see ≥2 center squares (d4/d5/e4/e5) through
    // pawns — it dominates the long diagonal. ref S(45,0) ×0.48.
    public static Score LongDiagonalBishop = new(22, 0);

    // KingProtector: penalty per Chebyshev distance of a minor piece from its own
    // king, indexed [knight, bishop]. DISABLED (zeroed): the term is jointly
    // calibrated in the reference with its own PSTs and king safety; on top of
    // PeSTO PSTs it double-counts king-distance and its Eg component cancels the
    // outpost bonuses (KnightOutpost +18 Eg − 4×dist Eg ≈ 0 at dist 4), which
    // collapsed play at long TC (~-111 Elo in the 2.6.5 gauntlet).
    // ref {S(9,9), S(7,9)} ×0.48 would be {(4,4),(3,4)} — do not re-enable
    // without an SPRT that proves it.
    public static Score[] KingProtector =
    [
        new(0, 0), new(0, 0),
    ];

    // MinorBehindPawn: bonus when a bishop or knight has a pawn (either color)
    // directly in front of it — it is shielded / blockades. ref S(18,3) ×0.48.
    public static Score MinorBehindPawn = new(9, 1);

    // BishopPawns: penalty per own pawn on the bishop's color, indexed by the
    // bishop file's distance to the board edge (0 = a/h .. 3 = d/e), and scaled
    // up when the bishop is not pawn-protected and when the center is blocked.
    // ref BishopPawns[4] = {S(3,8),S(3,9),S(2,7),S(3,7)} ×0.48 (penalty).
    public static Score[] BishopPawns =
    [
        new(1, 4), new(1, 4), new(1, 3), new(1, 3),
    ];

    // BishopXRayPawns: penalty per enemy pawn on the bishop's diagonal (x-ray,
    // ignoring blockers) — they restrict the bishop's scope. ref S(4,5) ×0.48.
    public static Score BishopXRayPawns = new(2, 2);

    // BishopOutpost: bishop permanently settled on a pawn-protected outpost
    // square (ranks 4-6 relative, no enemy pawn can evict it). Scaled by the
    // same tuned-to-reference ratio as KnightOutpost (51/54 Mg, 18/34 Eg)
    // instead of the generic ×0.48: the outpost family is one of the few
    // terms where NoaChess's texel tuning landed near the RAW reference value
    // (PeSTO PSTs under-reward advanced minors, so the explicit term carries
    // more of the weight than in the reference). ref S(31,25).
    public static Score BishopOutpost = new(29, 13);

    // ReachableOutpost: minor piece that can reach an outpost square next move.
    // ref S(33,19) ×0.48.
    public static Score ReachableOutpost = new(16, 9);

    // UncontestedOutpost: extra endgame value for a knight already on an outpost,
    // proportional to the number of our pawns on the knight's wing (a knight on
    // a wing with no enemy targets is worth little; with our pawns to support it
    // becomes a durable endgame asset). ref S(0,10) ×0.48, per wing pawn.
    public static Score UncontestedOutpost = new(0, 5);

    // WeakQueen: penalty when the queen is the single blocker between an enemy
    // rook/bishop and another target (a relative pin or latent discovered
    // attack). ref S(57,19) ×0.48.
    public static Score WeakQueen = new(-27, -9);

    // ---- Knight outposts ----
    // A knight on a hole in the enemy camp (relative ranks 4-6, protected by a
    // friendly pawn, no enemy pawn can ever kick it) is a permanent asset.
    // Texel-tuned value (v2.4.0, jointly with the PeSTO PSTs) — NOT the
    // generic ×0.48 of ref S(54,34): halving it to (26,16) measurably lost
    // Elo in the 2.6.5 SPRT runs. Keep the tuned value.
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

    // ---- Threats (reference threat values, RESCALED) ----
    // The reference works in internal units where PawnValueEg = 208 equals the
    // 100 cp it reports over UCI; NoaChess evaluates directly in ~centipawns
    // (PeSTO). Every reference constant ported here is therefore multiplied by
    // 100/208 = 0.48 — porting the raw numbers made every threat term twice as
    // strong as intended and cost Elo (SPRT trended negative). This applies to
    // EVERY value ported from the reference evaluation.
    //
    // A "weak" enemy piece is one we attack that is not strongly protected
    // (not pawn-defended and not defended more times than we attack it).
    // Indexed by the ATTACKED piece type (pawn..king; king slot stays zero,
    // matching the reference where the king entry of the array is zero-init).
    public static Score[] ThreatByMinor = // ref: (6,37)(64,50)(82,57)(103,130)(81,163)
    [
        new(3, 18), new(31, 24), new(39, 27), new(50, 63), new(39, 78), default,
    ];
    public static Score[] ThreatByRook = // ref: (3,44)(36,71)(44,59)(0,39)(60,39)
    [
        new(1, 21), new(17, 34), new(21, 28), new(0, 19), new(29, 19), default,
    ];
    // Our king attacks a weak enemy piece (endgame-heavy: the king becomes a
    // fighting piece once the danger is gone). ref: (24,87).
    public static Score ThreatByKing = new(12, 42);
    // A weak enemy piece nobody defends at all (or a non-pawn we attack
    // twice). ref: (72,40).
    public static Score Hanging = new(35, 19);
    // Extra bonus when the weak piece's only protector is the enemy queen.
    // ref: (14,0).
    public static Score WeakQueenProtection = new(7, 0);
    // Enemy piece moves restricted by our control of escape squares.
    // ref: (6,7).
    public static Score RestrictedPiece = new(3, 3);
    // A safe friendly pawn (defended or not attacked) attacking a non-pawn.
    // ref: (167,99).
    public static Score ThreatBySafePawn = new(80, 48);
    // A pawn that can push next move to a safe square and then attack a
    // non-pawn enemy piece. ref: (48,39).
    public static Score ThreatByPawnPush = new(23, 19);
    // Knight can jump to a safe square that attacks the enemy queen.
    // ref: (16,11).
    public static Score KnightOnQueen = new(8, 5);
    // Bishop or rook already attacks a safe square from which it hits the
    // enemy queen, with double attack on that square. ref: (62,21).
    public static Score SliderOnQueen = new(30, 10);

    // ---- Pawn structure (reference pawns.cpp scoring chain, ×0.48) ----
    // The per-pawn score is a chain of MUTUALLY EXCLUSIVE branches, not a sum
    // of independent terms: a supported/phalanx pawn scores the Connected
    // formula, an isolated pawn pays Isolated (or the trebled-pawn Doubled
    // special case), a backward pawn pays Backward; on top, any unsupported
    // pawn pays Doubled/WeakLever, and advanced blocked pawns get BlockedPawnRank.
    //
    // Doubled: own pawn DIRECTLY behind on the same file (not the per-file
    // count of the old model) and no support. ref S(11,51) ×0.48.
    public static Score Doubled = new(5, 25);
    // DoubledEarly: extra penalty for a doubled pawn while no enemy pawn is
    // fixed yet (no own pawn rams or restrains them) — early doubling is
    // harder to justify because the structure is still fluid. ref S(17,7) ×0.48.
    public static Score DoubledEarly = new(8, 3);
    // Isolated: no friendly pawn on adjacent files. ref S(1,20) ×0.48.
    public static Score Isolated = new(0, 10);
    // Backward: all neighbours strictly ahead and the pawn cannot advance
    // (stop square blocked or covered by a lever-push). ref S(6,19) ×0.48.
    public static Score Backward = new(3, 9);
    // WeakLever: an unsupported pawn attacked by two enemy pawns loses the
    // exchange on either recapture. ref S(2,57) ×0.48.
    public static Score WeakLever = new(1, 27);
    // WeakUnopposed: an isolated or backward pawn with a free file in front is
    // an easy target for rooks (added on top of Isolated/Backward; the
    // backward case only off the rook files). ref S(15,18) ×0.48.
    public static Score WeakUnopposed = new(7, 9);
    // Connected: supported and/or phalanx pawns, by relative rank. RAW
    // reference units — the formula v = Connected[r]*(2 + phalanx - opposed)
    // + 22*support is computed in reference units and converted ×0.48 at the
    // end (eg = v*(r-2)/4 before conversion).
    public static readonly int[] Connected = [0, 5, 7, 11, 23, 48, 87, 0];
    // Blocked pawn on relative rank 5-6 (indexed rank-4): a rammed pawn deep
    // in the enemy camp cramps the defense — small bonus that turns positive
    // in the endgame on rank 6. ref {S(-19,-8), S(-7,3)} ×0.48. Added as-is.
    public static Score[] BlockedPawnRank =
    [
        new(-9, -4), new(-3, 1),
    ];
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
    // Penalty per file distance from the edge: flank passers are worth more
    // than central ones (the defending king covers fewer squares). Reference
    // S(13,8) x0.48. Subtracted per passer.
    public static Score PassedFile = new(6, 4);
}
