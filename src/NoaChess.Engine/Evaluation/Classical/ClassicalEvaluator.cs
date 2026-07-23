using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Classical evaluator, v3.0: a tapered (middlegame/endgame) evaluation with
// material, piece-square tables, mobility, king safety, pawn structure and a
// handful of positional terms (bishop pair, rooks on open files).
//
// "Tapered" means every term is a (middlegame, endgame) pair; the two are
// blended by the game phase, computed from the non-pawn material still on the
// board (see Score / EvaluationParams). This is the single most important step
// up from a flat material+PST evaluation: the same knight, king or passed pawn
// is worth different amounts depending on how far into the game we are.
//
// Performance: the piece loop makes ONE pass per side. The attack bitboard of
// each piece is computed once and feeds BOTH the mobility term and the enemy
// king-safety term, instead of scanning the pieces several times.
public sealed class ClassicalEvaluator : IPositionEvaluator
{
    private readonly PawnStructureEvaluator _pawnStructure = new();

    // Per-color king zone (king square + adjacent squares), filled each call.
    private readonly int[] _kingSquare = new int[2];
    private readonly ulong[] _kingZone = new ulong[2];

    // Attack units accumulated against each color's king this call.
    private readonly int[] _kingDanger = new int[2];

    public int Evaluate(Board board)
    {
        ulong whitePawns = board.Pieces(Color.White, PieceType.Pawn);
        ulong blackPawns = board.Pieces(Color.Black, PieceType.Pawn);

        // Squares defended by each side's pawns: a piece that moves onto one of
        // the enemy's pawn-attacked squares is simply lost, so those squares do
        // not count as real mobility.
        ulong whitePawnAttacks = ((whitePawns & ~Bitboard.FileA) << 7)
                               | ((whitePawns & ~Bitboard.FileH) << 9);
        ulong blackPawnAttacks = ((blackPawns & ~Bitboard.FileA) >> 9)
                               | ((blackPawns & ~Bitboard.FileH) >> 7);
        ulong[] pawnAttacks = [whitePawnAttacks, blackPawnAttacks];

        for (int c = 0; c < 2; c++)
        {
            _kingSquare[c] = board.KingSquare((Color)c);
            _kingZone[c] = Attacks.King(_kingSquare[c]) | Bitboard.SquareBB(_kingSquare[c]);
            _kingDanger[c] = 0;
        }

        Score score = default; // White's point of view.
        int phase = 0;

        for (int c = 0; c < 2; c++)
        {
            Color color = (Color)c;
            int sign = color == Color.White ? 1 : -1;
            Color enemy = Board.OppositeColor(color);
            ulong ownPieces = board.Occupancy(color);
            ulong occ = board.AllOccupancy;

            // Mobility area: anywhere not blocked by our own pieces and not
            // covered by an enemy pawn.
            ulong mobilityArea = ~ownPieces & ~pawnAttacks[(int)enemy];

            Score side = default;

            for (int p = 0; p < 6; p++)
            {
                var type = (PieceType)p;
                ulong pieces = board.Pieces(color, type);
                phase += Bitboard.PopCount(pieces) * EvaluationParams.PhaseWeight[p];

                while (pieces != 0)
                {
                    int sq = Bitboard.PopLsb(ref pieces);

                    // Material + piece-square value.
                    side += new Score(EvaluationParams.MaterialMg[p], EvaluationParams.MaterialEg[p]);
                    side += PieceSquareTables.Value(type, color, sq);

                    if (type is PieceType.Knight or PieceType.Bishop
                             or PieceType.Rook or PieceType.Queen)
                    {
                        ulong attacks = type switch
                        {
                            PieceType.Knight => Attacks.Knight(sq),
                            PieceType.Bishop => Attacks.Bishop(sq, occ),
                            PieceType.Rook => Attacks.Rook(sq, occ),
                            _ => Attacks.Queen(sq, occ),
                        };

                        // Mobility, centered on a typical count for the piece.
                        int moves = Bitboard.PopCount(attacks & mobilityArea);
                        side += EvaluationParams.MobilityStep[p]
                              * (moves - EvaluationParams.MobilityBaseline[p]);

                        // Pressure on the enemy king's zone.
                        int zoneHits = Bitboard.PopCount(attacks & _kingZone[(int)enemy]);
                        if (zoneHits > 0)
                            _kingDanger[(int)enemy] += zoneHits * EvaluationParams.KingAttackWeight[p];
                    }
                }
            }

            // Bishop pair: two bishops cover both colors and are worth a bonus.
            if (Bitboard.PopCount(board.Pieces(color, PieceType.Bishop)) >= 2)
                side += EvaluationParams.BishopPair;

            // Rooks on open / semi-open files.
            side += RookFileBonus(board, color, whitePawns, blackPawns);

            score += side * sign;
        }

        // Pawn structure (cached, tapered, already White-relative).
        score += _pawnStructure.Evaluate(board);

        // King safety: turn the accumulated attack units (plus a pawn-shield
        // check) into a middlegame penalty on the endangered king.
        score += KingSafety(board, Color.White, whitePawns) * 1;
        score += KingSafety(board, Color.Black, blackPawns) * -1;

        if (phase > EvaluationParams.PhaseMax)
            phase = EvaluationParams.PhaseMax; // Early promotions can exceed it.

        int tapered = score.Taper(phase);

        // Mop-up: nudge a won bare-king endgame towards the mate (see below).
        tapered += MopUp(board, Color.White) - MopUp(board, Color.Black);

        // Negamax wants the score relative to the side to move.
        return board.SideToMove == Color.White ? tapered : -tapered;
    }

    // Rook bonus for standing on a file with no friendly pawns (semi-open) or
    // no pawns at all (open): open files are a rook's natural highway.
    private static Score RookFileBonus(Board board, Color color, ulong whitePawns, ulong blackPawns)
    {
        ulong rooks = board.Pieces(color, PieceType.Rook);
        ulong ownPawns = color == Color.White ? whitePawns : blackPawns;
        ulong allPawns = whitePawns | blackPawns;
        Score bonus = default;

        while (rooks != 0)
        {
            int sq = Bitboard.PopLsb(ref rooks);
            ulong file = Bitboard.FileA << Squares.FileOf(sq);
            if ((allPawns & file) == 0)
                bonus += EvaluationParams.RookOpenFile;
            else if ((ownPawns & file) == 0)
                bonus += EvaluationParams.RookSemiOpenFile;
        }
        return bonus;
    }

    // King safety as a middlegame-only term (it tapers to nothing in the
    // endgame, where an active, centralized king is GOOD). Combines the attack
    // units gathered during the piece loop with a pawn-shield check on the
    // three files around the king, then maps the total through a quadratic
    // danger curve. Returned as a Score whose sign the caller applies.
    private Score KingSafety(Board board, Color color, ulong ownPawns)
    {
        int units = _kingDanger[(int)color];

        // Missing pawn shield: for each of the three files around the king with
        // no friendly pawn ahead of it, the king is that much more exposed.
        int kingSq = _kingSquare[(int)color];
        int kingFile = Squares.FileOf(kingSq);
        int kingRank = Squares.RankOf(kingSq);
        ulong ahead = 0;
        if (color == Color.White)
            for (int r = kingRank + 1; r < 8; r++) ahead |= 0xFFUL << (r * 8);
        else
            for (int r = 0; r < kingRank; r++) ahead |= 0xFFUL << (r * 8);

        for (int f = Math.Max(0, kingFile - 1); f <= Math.Min(7, kingFile + 1); f++)
        {
            ulong file = Bitboard.FileA << f;
            if ((ownPawns & file & ahead) == 0)
                units += EvaluationParams.OpenFileNearKingUnits;
        }

        int danger = Math.Min(units * units / EvaluationParams.KingDangerDivisor,
                              EvaluationParams.KingDangerCap);

        // Penalty in the middlegame only; the endgame value is zero so tapering
        // fades king safety out as pieces come off.
        return new Score(-danger, 0);
    }

    // Bonus for the clearly winning side in a bare-bones endgame: push the
    // enemy king towards the edge/corner (where mates happen) and bring the own
    // king closer. The gradient gives the search a slope to follow long before
    // the mate itself is within its horizon. Kept outside the tapered score
    // because it is a pure endgame heuristic already gated on material.
    private static int MopUp(Board board, Color us)
    {
        Color them = Board.OppositeColor(us);
        int myMaterial = NonKingMaterial(board, us);
        int theirMaterial = NonKingMaterial(board, them);

        // Only when clearly winning against (nearly) a lone king.
        if (myMaterial < theirMaterial + 400 || theirMaterial > 300)
            return 0;

        int myKing = board.KingSquare(us);
        int theirKing = board.KingSquare(them);

        int centerDistance = Math.Abs(2 * Squares.FileOf(theirKing) - 7) / 2
                           + Math.Abs(2 * Squares.RankOf(theirKing) - 7) / 2;
        int kingsDistance = Math.Abs(Squares.FileOf(myKing) - Squares.FileOf(theirKing))
                          + Math.Abs(Squares.RankOf(myKing) - Squares.RankOf(theirKing));

        return 10 * centerDistance + 4 * (14 - kingsDistance);
    }

    private static int NonKingMaterial(Board board, Color color)
    {
        int total = 0;
        for (int p = 0; p < 5; p++) // pawn..queen, no king
            total += Bitboard.PopCount(board.Pieces(color, (PieceType)p))
                   * EvaluationParams.MaterialMg[p];
        return total;
    }
}
