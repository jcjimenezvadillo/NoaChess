using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Winnable / scale factors (4I): the final tapered blend is adjusted for
// positions that are structurally harder or easier to win than the raw score
// claims. Two mechanisms, applied to the TOTAL White-relative score right
// before the phase interpolation (reference winnable() plus the drawish
// factor of the reference material entry):
//
//  1. Complexity (initiative): passers, total pawns, king outflanking, pawns
//     on both flanks, king infiltration and pure-pawn endings make a position
//     easier to convert; kings crossing past each other with all the pawns on
//     one flank (almostUnwinnable) make it drawish. Computed in raw reference
//     internal units and converted x0.48 once (the mg/eg caps are NoaChess
//     centipawns). The adjustment can only reduce the midgame component, can
//     push the endgame component either way, and never flips the sign of
//     either.
//
//  2. Scale factor: the endgame half of the blend is multiplied by sf/64.
//     A specific factor comes from the material configuration (no pawns and
//     at most a bishop of extra material rarely wins: KK, KBK, KNK, KRKB,
//     KRKN, KmmKm); otherwise general heuristics: opposite-colored bishops,
//     single-rook endgames with the pawns on one flank, queen versus no
//     queen, and a cap by the strong side's pawn count, all further reduced
//     when every pawn sits on one flank. Scale factors are dimensionless
//     ratios and are NOT x0.48-rescaled.
public static class Winnable
{
    public const int ScaleNormal = 64;

    // Files a-d / e-h (the reference QueenSide / KingSide masks).
    private const ulong QueenSideFiles = Bitboard.FileA | (Bitboard.FileA << 1)
                                       | (Bitboard.FileA << 2) | (Bitboard.FileA << 3);
    private const ulong KingSideFiles = (Bitboard.FileA << 4) | (Bitboard.FileA << 5)
                                      | (Bitboard.FileA << 6) | (Bitboard.FileA << 7);

    // Bits set where file+rank is odd (b1, d1, ..., a2, c2, ...).
    private const ulong LightSquares = 0x55AA55AA55AA55AAUL;

    // Replaces Score.Taper for the total evaluation: complexity adjustment on
    // both components, then interpolation with the endgame part scaled sf/64.
    public static int Apply(Board board, Score score, int phase,
                            ulong whitePassers, ulong blackPassers)
        => Apply(board, score, phase, whitePassers, blackPassers, out _);

    // Overload that also reports the position's complexity (the initiative
    // magnitude, centipawns, never negative). The search's null-move-pruning
    // entry margin consumes it: complex positions are harder to null-prune.
    public static int Apply(Board board, Score score, int phase,
                            ulong whitePassers, ulong blackPassers,
                            out int complexityCp)
    {
        int wk = board.KingSquare(Color.White);
        int bk = board.KingSquare(Color.Black);
        ulong pawns = board.Pieces(Color.White, PieceType.Pawn)
                    | board.Pieces(Color.Black, PieceType.Pawn);

        // Outflanking: file distance between the kings plus the SIGNED rank
        // difference; negative when the kings have crossed past each other
        // (typical of a race that burned the win). Color-symmetric.
        int outflanking = Math.Abs(Squares.FileOf(wk) - Squares.FileOf(bk))
                        + Squares.RankOf(wk) - Squares.RankOf(bk);
        bool pawnsOnBothFlanks = (pawns & QueenSideFiles) != 0
                              && (pawns & KingSideFiles) != 0;
        bool almostUnwinnable = outflanking < 0 && !pawnsOnBothFlanks;
        bool infiltration = Squares.RankOf(wk) > 3 || Squares.RankOf(bk) < 4;

        int npmWhite = NonPawnMaterial(board, Color.White);
        int npmBlack = NonPawnMaterial(board, Color.Black);

        // Initiative, in raw reference internal units.
        int complexity = 9 * Bitboard.PopCount(whitePassers | blackPassers)
                       + 12 * Bitboard.PopCount(pawns)
                       + 9 * outflanking
                       + 21 * (pawnsOnBothFlanks ? 1 : 0)
                       + 24 * (infiltration ? 1 : 0)
                       + 51 * (npmWhite + npmBlack == 0 ? 1 : 0)
                       - 43 * (almostUnwinnable ? 1 : 0)
                       - 110;
        complexityCp = Math.Max(0, complexity * 48 / 100);

        int mg = score.Mg, eg = score.Eg;

        // The attacking side is the sign of each component; the bonus is
        // capped so neither component changes sign. x0.48 to centipawns
        // (the +50 midgame offset is a reference-unit constant too).
        int u = Math.Sign(mg) * Math.Clamp((complexity + 50) * 48 / 100, -Math.Abs(mg), 0);
        int v = Math.Sign(eg) * Math.Max(complexity * 48 / 100, -Math.Abs(eg));
        mg += u;
        eg += v;

        Color strong = eg > 0 ? Color.White : Color.Black;
        int sf = ScaleFactor(board, strong,
                             strong == Color.White ? whitePassers : blackPassers,
                             npmWhite, npmBlack, pawnsOnBothFlanks);

        return (mg * phase
              + eg * (EvaluationParams.PhaseMax - phase) * sf / ScaleNormal)
             / EvaluationParams.PhaseMax;
    }

    // Endgame scale factor (0..64) for the strong side. Public with explicit
    // inputs so the tests can pin every branch.
    public static int ScaleFactor(Board board, Color strong, ulong strongPassers,
                                  int npmWhite, int npmBlack, bool pawnsOnBothFlanks)
    {
        Color weak = Board.OppositeColor(strong);
        int npmStrong = strong == Color.White ? npmWhite : npmBlack;
        int npmWeak = strong == Color.White ? npmBlack : npmWhite;
        int bishopMg = EvaluationParams.MaterialMg[(int)PieceType.Bishop];
        int rookMg = EvaluationParams.MaterialMg[(int)PieceType.Rook];

        // Material-configuration factor: no pawns and at most a bishop of
        // extra material rarely wins. At most a minor in total is a dead draw
        // (KK, KBK, KNK); against a bare minor it is nearly one (KRKB, KRKN);
        // otherwise merely very drawish (KmmKm and friends).
        if (board.Pieces(strong, PieceType.Pawn) == 0 && npmStrong - npmWeak <= bishopMg)
            return npmStrong < rookMg ? 0
                 : npmWeak <= bishopMg ? 4 : 14;

        int sf = ScaleNormal;
        ulong strongPawns = board.Pieces(strong, PieceType.Pawn);
        int strongPawnCount = Bitboard.PopCount(strongPawns);

        ulong whiteBishops = board.Pieces(Color.White, PieceType.Bishop);
        ulong blackBishops = board.Pieces(Color.Black, PieceType.Bishop);
        bool oppositeBishops = Bitboard.PopCount(whiteBishops) == 1
                            && Bitboard.PopCount(blackBishops) == 1
                            && ((whiteBishops & LightSquares) != 0)
                            != ((blackBishops & LightSquares) != 0);

        if (oppositeBishops)
        {
            // Pure opposite-colored bishops: winnability is driven by the
            // strong side's passed pawns; with more material on the board, by
            // how many units the strong side still has.
            sf = npmWhite == bishopMg && npmBlack == bishopMg
                ? 18 + 4 * Bitboard.PopCount(strongPassers)
                : 22 + 3 * Bitboard.PopCount(board.Occupancy(strong));
        }
        else if (npmWhite == rookMg && npmBlack == rookMg
              && strongPawnCount - Bitboard.PopCount(board.Pieces(weak, PieceType.Pawn)) <= 1
              && ((strongPawns & KingSideFiles) != 0) != ((strongPawns & QueenSideFiles) != 0)
              && (Attacks.King(board.KingSquare(weak)) & board.Pieces(weak, PieceType.Pawn)) != 0)
        {
            // Single-rook endgame, no meaningful pawn advantage, all the
            // strong side's pawns on one flank and the weak king defending
            // its own pawns: the textbook drawn rook ending.
            sf = 36;
        }
        else if (Bitboard.PopCount(board.Pieces(Color.White, PieceType.Queen)
                                 | board.Pieces(Color.Black, PieceType.Queen)) == 1)
        {
            // Queen versus no queen: scaled by the minors of the queenless
            // side (they gang up on the queen).
            Color queenless = board.Pieces(Color.White, PieceType.Queen) != 0
                ? Color.Black : Color.White;
            sf = 37 + 3 * Bitboard.PopCount(board.Pieces(queenless, PieceType.Knight)
                                          | board.Pieces(queenless, PieceType.Bishop));
        }
        else
        {
            // Everything else: cap by the strong side's pawn count, reduced
            // when its pawns sit on a single flank.
            sf = Math.Min(sf, 36 + 7 * strongPawnCount) - 4 * (pawnsOnBothFlanks ? 0 : 1);
        }

        // All-pawns-on-one-flank reduction, applied to every branch above.
        sf -= 4 * (pawnsOnBothFlanks ? 0 : 1);
        return sf;
    }

    // Non-pawn material of one color in NoaChess middlegame centipawns.
    public static int NonPawnMaterial(Board board, Color color)
    {
        int total = 0;
        for (int p = (int)PieceType.Knight; p <= (int)PieceType.Queen; p++)
            total += Bitboard.PopCount(board.Pieces(color, (PieceType)p))
                   * EvaluationParams.MaterialMg[p];
        return total;
    }
}
