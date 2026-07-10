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

    // Per-color pawn attack maps, filled each call (reused: no allocation).
    private readonly ulong[] _pawnAttacks = new ulong[2];

    // Squares attacked by two pawns of the same color, filled each call.
    private readonly ulong[] _pawnDoubleAttacks = new ulong[2];

    // Mobility area per color, kept for terms outside the piece loop (threats
    // against the enemy queen use it as their "safe square" filter).
    private readonly ulong[] _mobilityArea = new ulong[2];

    // attackedBy[color, pieceType]: squares attacked by that piece type; the
    // extra AllPieces slot holds the union over every type. attackedBy2[color]
    // is the set of squares attacked by two or more units of the same color
    // (pawn double attacks included). Both are rebuilt on every Evaluate call
    // and feed the threat, king-safety and mobility terms.
    public const int AllPieces = 6;
    private readonly ulong[,] _attackedBy = new ulong[2, 7];
    private readonly ulong[] _attackedBy2 = new ulong[2];

    // Read-only accessors for tests and for evaluation terms in other classes.
    public ulong AttackedBy(Color color, PieceType type) => _attackedBy[(int)color, (int)type];
    public ulong AttackedByAll(Color color) => _attackedBy[(int)color, AllPieces];
    public ulong AttackedBy2(Color color) => _attackedBy2[(int)color];

    // Space area per color: files c-f, relative ranks 2-4.
    private static readonly ulong[] SpaceMask = new ulong[2];

    static ClassicalEvaluator()
    {
        ulong files = PawnStructureEvaluator.FileMask[2] | PawnStructureEvaluator.FileMask[3]
                    | PawnStructureEvaluator.FileMask[4] | PawnStructureEvaluator.FileMask[5];
        ulong whiteRanks = (0xFFUL << 8) | (0xFFUL << 16) | (0xFFUL << 24);  // ranks 2-4
        ulong blackRanks = (0xFFUL << 48) | (0xFFUL << 40) | (0xFFUL << 32); // ranks 7-5
        SpaceMask[(int)Color.White] = files & whiteRanks;
        SpaceMask[(int)Color.Black] = files & blackRanks;
    }

    public int Evaluate(Board board)
    {
        ulong whitePawns = board.Pieces(Color.White, PieceType.Pawn);
        ulong blackPawns = board.Pieces(Color.Black, PieceType.Pawn);

        // Squares defended by each side's pawns: a piece that moves onto one of
        // the enemy's pawn-attacked squares is simply lost, so those squares do
        // not count as real mobility.
        ulong[] pawnAttacks = _pawnAttacks;
        ulong whitePawnsWest = (whitePawns & ~Bitboard.FileA) << 7;
        ulong whitePawnsEast = (whitePawns & ~Bitboard.FileH) << 9;
        ulong blackPawnsWest = (blackPawns & ~Bitboard.FileA) >> 9;
        ulong blackPawnsEast = (blackPawns & ~Bitboard.FileH) >> 7;
        pawnAttacks[(int)Color.White] = whitePawnsWest | whitePawnsEast;
        pawnAttacks[(int)Color.Black] = blackPawnsWest | blackPawnsEast;

        // A square attacked from both directions is attacked by two pawns
        // (SF pawn_double_attacks_bb).
        _pawnDoubleAttacks[(int)Color.White] = whitePawnsWest & whitePawnsEast;
        _pawnDoubleAttacks[(int)Color.Black] = blackPawnsWest & blackPawnsEast;

        for (int c = 0; c < 2; c++)
        {
            _kingSquare[c] = board.KingSquare((Color)c);
            _kingZone[c] = Attacks.King(_kingSquare[c]) | Bitboard.SquareBB(_kingSquare[c]);
            _kingDanger[c] = 0;

            // attackedBy seed (SF Evaluation::initialize): king and pawn attack
            // sets are known up front; the piece loop below adds the rest.
            ulong kingAttacks = Attacks.King(_kingSquare[c]);
            _attackedBy[c, (int)PieceType.Pawn] = pawnAttacks[c];
            _attackedBy[c, (int)PieceType.Knight] = 0;
            _attackedBy[c, (int)PieceType.Bishop] = 0;
            _attackedBy[c, (int)PieceType.Rook] = 0;
            _attackedBy[c, (int)PieceType.Queen] = 0;
            _attackedBy[c, (int)PieceType.King] = kingAttacks;
            _attackedBy[c, AllPieces] = kingAttacks | pawnAttacks[c];
            _attackedBy2[c] = _pawnDoubleAttacks[c] | (kingAttacks & pawnAttacks[c]);
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
            _mobilityArea[c] = mobilityArea;

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

                        // attackedBy bookkeeping (SF Evaluation::pieces): any
                        // square this piece hits that was already covered by an
                        // earlier friendly unit becomes a double attack.
                        _attackedBy2[c] |= _attackedBy[c, AllPieces] & attacks;
                        _attackedBy[c, p] |= attacks;
                        _attackedBy[c, AllPieces] |= attacks;

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

            // Rooks on open / semi-open files and on the 7th rank.
            side += RookFileBonus(board, color, whitePawns, blackPawns);
            side += RookOnSeventh(board, color);

            score += side * sign;
        }

        // Pawn structure (cached, tapered, already White-relative). Also hands
        // back the cached passer bitboards for the piece-dependent terms.
        score += _pawnStructure.Evaluate(board, out ulong whitePassers, out ulong blackPassers);

        // Second pass: terms that mix pawn structure with piece locations
        // (outposts, passer blockers/escorts, space). Runs after the piece loop
        // so both sides' pawn attacks are already computed.
        for (int c = 0; c < 2; c++)
        {
            Color color = (Color)c;
            int sign = color == Color.White ? 1 : -1;
            ulong passers = color == Color.White ? whitePassers : blackPassers;
            Score side = KnightOutposts(board, color, pawnAttacks)
                       + PasserPieceTerms(board, color, passers)
                       + Space(board, color, pawnAttacks)
                       + Threats(board, color);
            score += side * sign;
        }

        // King safety: turn the accumulated attack units (plus a pawn-shield
        // check) into a middlegame penalty on the endangered king.
        score += KingSafety(board, Color.White, whitePawns) * 1;
        score += KingSafety(board, Color.Black, blackPawns) * -1;

        if (phase > EvaluationParams.PhaseMax)
            phase = EvaluationParams.PhaseMax; // Early promotions can exceed it.

        int tapered = score.Taper(phase);

        // Mop-up: nudge a won bare-king endgame towards the mate (see below).
        tapered += MopUp(board, Color.White) - MopUp(board, Color.Black);

        // Negamax: score relative to the side to move, plus a small tempo
        // bonus for having the initiative (always positive for the evaluee).
        return (board.SideToMove == Color.White ? tapered : -tapered)
             + EvaluationParams.Tempo;
    }

    // Pawn attack squares of an arbitrary pawn set (not just the full pawn
    // bitboard cached in _pawnAttacks).
    private static ulong PawnAttacksOf(Color color, ulong pawns) =>
        color == Color.White
            ? ((pawns & ~Bitboard.FileA) << 7) | ((pawns & ~Bitboard.FileH) << 9)
            : ((pawns & ~Bitboard.FileA) >> 9) | ((pawns & ~Bitboard.FileH) >> 7);

    // Threats (SF Evaluation::threats): bonuses by the types of the attacking
    // and the attacked pieces. The key concept is "strongly protected": a
    // square defended by an enemy pawn, or defended more times than we attack
    // it. Enemy pieces that are attacked and NOT strongly protected are
    // "weak" and everything below feeds on that classification. This is what
    // the removed v2.4.0 threat terms lacked — they rewarded any attack, even
    // on a healthily defended piece, which distorted the material judgement.
    // Public for the test suite (which probes the term in isolation, free of
    // PST/material confounds); requires Evaluate() to have run on the same
    // board first so the attackedBy tables are current.
    public Score Threats(Board board, Color us)
    {
        Color them = Board.OppositeColor(us);
        int u = (int)us, t = (int)them;
        ulong occ = board.AllOccupancy;
        Score score = default;

        // Non-pawn enemies.
        ulong nonPawnEnemies = board.Occupancy(them) & ~board.Pieces(them, PieceType.Pawn);

        // Squares strongly protected by the enemy: pawn-defended, or defended
        // twice while we do not attack them twice.
        ulong stronglyProtected = _attackedBy[t, (int)PieceType.Pawn]
                                | (_attackedBy2[t] & ~_attackedBy2[u]);

        // Non-pawn enemies, strongly protected.
        ulong defended = nonPawnEnemies & stronglyProtected;

        // Enemies not strongly protected and under our attack.
        ulong weak = board.Occupancy(them) & ~stronglyProtected & _attackedBy[u, AllPieces];

        if ((defended | weak) != 0)
        {
            // Minor attacks on a defended or weak piece (a knight/bishop hit
            // is a threat even against a defended target: minors are cheap).
            ulong b = (defended | weak)
                    & (_attackedBy[u, (int)PieceType.Knight] | _attackedBy[u, (int)PieceType.Bishop]);
            while (b != 0)
            {
                int sq = Bitboard.PopLsb(ref b);
                score += EvaluationParams.ThreatByMinor[(int)board.PieceTypeAt(sq)];
            }

            // Rook attacks count only against weak pieces.
            b = weak & _attackedBy[u, (int)PieceType.Rook];
            while (b != 0)
            {
                int sq = Bitboard.PopLsb(ref b);
                score += EvaluationParams.ThreatByRook[(int)board.PieceTypeAt(sq)];
            }

            if ((weak & _attackedBy[u, (int)PieceType.King]) != 0)
                score += EvaluationParams.ThreatByKing;

            // Hanging: weak and either completely undefended, or a non-pawn
            // that we attack twice.
            b = ~_attackedBy[t, AllPieces] | (nonPawnEnemies & _attackedBy2[u]);
            score += EvaluationParams.Hanging * Bitboard.PopCount(weak & b);

            // Additional bonus if the weak piece is only protected by a queen.
            score += EvaluationParams.WeakQueenProtection
                   * Bitboard.PopCount(weak & _attackedBy[t, (int)PieceType.Queen]);
        }

        // Bonus for restricting their piece moves: squares the enemy attacks,
        // not strongly protected, that we attack too.
        ulong restricted = _attackedBy[t, AllPieces] & ~stronglyProtected & _attackedBy[u, AllPieces];
        score += EvaluationParams.RestrictedPiece * Bitboard.PopCount(restricted);

        // Protected or unattacked squares.
        ulong safe = ~_attackedBy[t, AllPieces] | _attackedBy[u, AllPieces];

        // Bonus for attacking enemy non-pawns with our relatively safe pawns.
        ulong safePawnAttacks = PawnAttacksOf(us, board.Pieces(us, PieceType.Pawn) & safe)
                              & nonPawnEnemies;
        score += EvaluationParams.ThreatBySafePawn * Bitboard.PopCount(safePawnAttacks);

        // Squares where our pawns can push on the next move (single pushes,
        // plus double pushes through a free relative rank 3 square)...
        ulong pushes = us == Color.White
            ? (board.Pieces(us, PieceType.Pawn) << 8) & ~occ
            : (board.Pieces(us, PieceType.Pawn) >> 8) & ~occ;
        ulong rank3 = us == Color.White ? 0xFFUL << 16 : 0xFFUL << 40;
        pushes |= us == Color.White
            ? ((pushes & rank3) << 8) & ~occ
            : ((pushes & rank3) >> 8) & ~occ;

        // ...keeping only the relatively safe ones, then the non-pawns those
        // pushed pawns would attack.
        pushes &= ~_attackedBy[t, (int)PieceType.Pawn] & safe;
        ulong pushAttacks = PawnAttacksOf(us, pushes) & nonPawnEnemies;
        score += EvaluationParams.ThreatByPawnPush * Bitboard.PopCount(pushAttacks);

        // Threats on the next moves against the enemy queen. Doubled when the
        // enemy queen is the only queen left (losing it is not repayable).
        ulong enemyQueen = board.Pieces(them, PieceType.Queen);
        if (Bitboard.PopCount(enemyQueen) == 1)
        {
            int queenImbalance =
                Bitboard.PopCount(enemyQueen | board.Pieces(us, PieceType.Queen)) == 1 ? 1 : 0;

            int qsq = Bitboard.Lsb(enemyQueen);
            ulong queenSafe = _mobilityArea[u]
                            & ~board.Pieces(us, PieceType.Pawn)
                            & ~stronglyProtected;

            ulong knightHits = _attackedBy[u, (int)PieceType.Knight] & Attacks.Knight(qsq);
            score += EvaluationParams.KnightOnQueen
                   * (Bitboard.PopCount(knightHits & queenSafe) * (1 + queenImbalance));

            ulong sliderHits = (_attackedBy[u, (int)PieceType.Bishop] & Attacks.Bishop(qsq, occ))
                             | (_attackedBy[u, (int)PieceType.Rook] & Attacks.Rook(qsq, occ));
            score += EvaluationParams.SliderOnQueen
                   * (Bitboard.PopCount(sliderHits & queenSafe & _attackedBy2[u]) * (1 + queenImbalance));
        }

        return score;
    }

    // Rook on the 7th rank: attacks the opponent's pawns on their starting rank
    // and cuts the enemy king off from the rest of the board. The endgame value
    // is high because a 7th-rank rook frequently decides the game outright.
    private static Score RookOnSeventh(Board board, Color color)
    {
        ulong seventhRank = color == Color.White ? 0xFFUL << 48 : 0xFFUL << 8;
        ulong rooks = board.Pieces(color, PieceType.Rook) & seventhRank;
        return EvaluationParams.RookOnSeventh * Bitboard.PopCount(rooks);
    }

    // Knight outposts: a knight on relative ranks 4-6, protected by a friendly
    // pawn, on a square no enemy pawn can ever attack (no enemy pawns on the
    // adjacent files ahead of it). Such a knight is a permanent thorn.
    private static Score KnightOutposts(Board board, Color color, ulong[] pawnAttacks)
    {
        Color enemy = Board.OppositeColor(color);
        ulong enemyPawns = board.Pieces(enemy, PieceType.Pawn);
        ulong knights = board.Pieces(color, PieceType.Knight) & pawnAttacks[(int)color];
        Score score = default;

        while (knights != 0)
        {
            int sq = Bitboard.PopLsb(ref knights);
            int relativeRank = color == Color.White ? Squares.RankOf(sq) : 7 - Squares.RankOf(sq);
            if (relativeRank is < 3 or > 5)
                continue;

            // The passed-pawn cone minus the own file = squares from which an
            // enemy pawn could ever attack this square's file neighbours.
            ulong evictors = PawnStructureEvaluator.PassedPawnMask[(int)color, sq]
                           & PawnStructureEvaluator.AdjacentFilesMask[Squares.FileOf(sq)];
            if ((enemyPawns & evictors) == 0)
                score += EvaluationParams.KnightOutpost;
        }
        return score;
    }

    // Piece-dependent passed pawn terms (the rank bonus itself lives in the
    // cached pawn evaluation): a blocked passer gives back part of its bonus,
    // a rook behind the passer earns the Tarrasch bonus.
    private static Score PasserPieceTerms(Board board, Color color, ulong passers)
    {
        if (passers == 0)
            return default;

        Color enemy = Board.OppositeColor(color);
        ulong ownRooks = board.Pieces(color, PieceType.Rook);
        Score score = default;

        while (passers != 0)
        {
            int sq = Bitboard.PopLsb(ref passers);
            int relativeRank = color == Color.White ? Squares.RankOf(sq) : 7 - Squares.RankOf(sq);
            int stopSq = color == Color.White ? sq + 8 : sq - 8;

            // Blocked passer: an enemy piece parked on the stop square.
            if (stopSq is >= 0 and < 64
                && (board.Occupancy(enemy) & Bitboard.SquareBB(stopSq)) != 0)
            {
                Score pp = EvaluationParams.PassedPawn[relativeRank];
                score += new Score(-pp.Mg / EvaluationParams.BlockedPasserDivisor,
                                   -pp.Eg / EvaluationParams.BlockedPasserDivisor);
            }

            // Rook behind the passer on the same file (behind = towards the
            // own back rank), with nothing between rook and pawn.
            ulong file = PawnStructureEvaluator.FileMask[Squares.FileOf(sq)];
            ulong behind = color == Color.White
                ? file & (Bitboard.SquareBB(sq) - 1)
                : file & ~(Bitboard.SquareBB(sq) - 1) & ~Bitboard.SquareBB(sq);
            ulong rooksBehind = ownRooks & behind;
            if (rooksBehind != 0)
            {
                // The rook must see the pawn: no blockers between them.
                ulong between = behind & board.AllOccupancy & ~rooksBehind;
                // Keep only blockers strictly between the closest rook and pawn.
                int rookSq = color == Color.White
                    ? 63 - System.Numerics.BitOperations.LeadingZeroCount(rooksBehind)
                    : Bitboard.Lsb(rooksBehind);
                ulong span = color == Color.White
                    ? behind & ~(Bitboard.SquareBB(rookSq) | (Bitboard.SquareBB(rookSq) - 1))
                    : behind & (Bitboard.SquareBB(rookSq) - 1);
                if ((between & span) == 0)
                    score += EvaluationParams.RookBehindPasser;
            }
        }
        return score;
    }

    // Space: safe central squares (files c-f, relative ranks 2-4) that are not
    // occupied by friendly pawns and not attacked by enemy pawns. Only worth
    // counting while there are enough pieces to use the room.
    private static Score Space(Board board, Color color, ulong[] pawnAttacks)
    {
        Color enemy = Board.OppositeColor(color);
        ulong safe = SpaceMask[(int)color]
                   & ~board.Pieces(color, PieceType.Pawn)
                   & ~pawnAttacks[(int)enemy];
        return EvaluationParams.SpacePerSquare * Bitboard.PopCount(safe);
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
