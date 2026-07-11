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

    // Per-color king square and king ring (SF kingRing: the king attacks of
    // the king square clamped to files b-g / ranks 2-7, plus the square
    // itself, minus squares defended by two of our own pawns), filled each call.
    private readonly int[] _kingSquare = new int[2];
    private readonly ulong[] _kingRing = new ulong[2];

    // SF king-attack bookkeeping, indexed by the ATTACKING color: number of
    // pieces attacking the enemy king ring, their summed attack weights, and
    // the count of their attacks on squares adjacent to the enemy king.
    private readonly int[] _kingAttackersCount = new int[2];
    private readonly int[] _kingAttackersWeight = new int[2];
    private readonly int[] _kingAttacksCount = new int[2];

    // Summed mobility Score per color (feeds the king danger formula).
    private readonly Score[] _mobilitySum = new Score[2];

    // Shelter cache. SF stores king safety in its pawn hash entry and only
    // recomputes when the king square or castling rights change (~20% of
    // calls); this direct-mapped table keyed by pawn key + king square +
    // castling rights + color achieves the same reuse. Slot 0 sentinel-safe:
    // a zero key never matches because the mixer never produces 0 for the
    // stored keys in practice and a false miss only costs a recompute.
    private readonly (ulong Key, int Mg, int Eg)[] _shelterCache = new (ulong, int, int)[16384];

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
    public ulong MobilityArea(Color color) => _mobilityArea[(int)color];
    public ulong BlockersForKing(Color color) => _blockersForKing[(int)color];

    // Space area per color: files c-f, relative ranks 2-4.
    private static readonly ulong[] SpaceMask = new ulong[2];

    // King flank by king file (SF KingFlank): the files "around" the king used
    // by the flank attack/defense terms. Queenside = a-d, center = c-f,
    // kingside = e-h, with the outermost file dropped on the very edges.
    private static readonly ulong[] KingFlank = BuildKingFlanks();

    private static ulong[] BuildKingFlanks()
    {
        ulong FileBB(int f) => Bitboard.FileA << f;
        ulong queenSide = FileBB(0) | FileBB(1) | FileBB(2) | FileBB(3);
        ulong centerFiles = FileBB(2) | FileBB(3) | FileBB(4) | FileBB(5);
        ulong kingSide = FileBB(4) | FileBB(5) | FileBB(6) | FileBB(7);
        return
        [
            queenSide ^ FileBB(3), queenSide, queenSide,
            centerFiles, centerFiles,
            kingSide, kingSide, kingSide ^ FileBB(4),
        ];
    }

    // LineThrough[s1*64+s2]: the full line (rank/file/diagonal) through both
    // squares including them, or 0 if not aligned. Between[s1*64+s2]: squares
    // strictly between two aligned squares. Used for pins (a piece blocking a
    // slider attack on its king may only move along the pin line).
    private static readonly ulong[] LineThrough = new ulong[64 * 64];
    private static readonly ulong[] Between = new ulong[64 * 64];

    // Pieces (of either color) that are the single blocker of an enemy slider
    // attack on this color's king, filled each call. A friendly piece here is
    // pinned; its attacks are restricted to the pin line.
    private readonly ulong[] _blockersForKing = new ulong[2];

    static ClassicalEvaluator()
    {
        ulong files = PawnStructureEvaluator.FileMask[2] | PawnStructureEvaluator.FileMask[3]
                    | PawnStructureEvaluator.FileMask[4] | PawnStructureEvaluator.FileMask[5];
        ulong whiteRanks = (0xFFUL << 8) | (0xFFUL << 16) | (0xFFUL << 24);  // ranks 2-4
        ulong blackRanks = (0xFFUL << 48) | (0xFFUL << 40) | (0xFFUL << 32); // ranks 7-5
        SpaceMask[(int)Color.White] = files & whiteRanks;
        SpaceMask[(int)Color.Black] = files & blackRanks;

        for (int s1 = 0; s1 < 64; s1++)
        {
            for (int s2 = 0; s2 < 64; s2++)
            {
                if (s1 == s2)
                    continue;
                ulong bb1 = Bitboard.SquareBB(s1), bb2 = Bitboard.SquareBB(s2);
                if ((Attacks.Rook(s1, 0) & bb2) != 0)
                {
                    LineThrough[s1 * 64 + s2] =
                        (Attacks.Rook(s1, 0) & Attacks.Rook(s2, 0)) | bb1 | bb2;
                    Between[s1 * 64 + s2] = Attacks.Rook(s1, bb2) & Attacks.Rook(s2, bb1);
                }
                else if ((Attacks.Bishop(s1, 0) & bb2) != 0)
                {
                    LineThrough[s1 * 64 + s2] =
                        (Attacks.Bishop(s1, 0) & Attacks.Bishop(s2, 0)) | bb1 | bb2;
                    Between[s1 * 64 + s2] = Attacks.Bishop(s1, bb2) & Attacks.Bishop(s2, bb1);
                }
            }
        }
    }

    // Pieces of either color that are the only blocker between an enemy
    // slider and this color's king (SF pos.blockers_for_king).
    private static ulong ComputeBlockersForKing(Board board, Color us)
    {
        int ksq = board.KingSquare(us);
        Color them = Board.OppositeColor(us);
        ulong occ = board.AllOccupancy;
        ulong snipers =
              (Attacks.Rook(ksq, 0) & (board.Pieces(them, PieceType.Rook) | board.Pieces(them, PieceType.Queen)))
            | (Attacks.Bishop(ksq, 0) & (board.Pieces(them, PieceType.Bishop) | board.Pieces(them, PieceType.Queen)));
        ulong blockers = 0;

        while (snipers != 0)
        {
            int s = Bitboard.PopLsb(ref snipers);
            ulong b = Between[ksq * 64 + s] & occ;
            if (b != 0 && (b & (b - 1)) == 0) // exactly one piece in between
                blockers |= b;
        }
        return blockers;
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
            var us = (Color)c;
            _kingSquare[c] = board.KingSquare(us);
            _blockersForKing[c] = ComputeBlockersForKing(board, us);
            _mobilitySum[c] = default;

            // King ring (SF initialize): king attacks of the king square
            // clamped to files b-g and ranks 2-7 (so edge kings get a full
            // 3x3 ring pointing inward), plus the clamped square itself,
            // minus squares defended by two of our own pawns.
            int rf = Math.Clamp(Squares.FileOf(_kingSquare[c]), 1, 6);
            int rr = Math.Clamp(Squares.RankOf(_kingSquare[c]), 1, 6);
            int ringSq = rr * 8 + rf;
            _kingRing[c] = (Attacks.King(ringSq) | Bitboard.SquareBB(ringSq))
                         & ~_pawnDoubleAttacks[c];

            // Enemy pawns attacking our ring seed their side's attacker count
            // (SF: kingAttackersCount[Them] = popcount(kingRing[Us] & pawns)).
            _kingAttackersCount[1 - c] = Bitboard.PopCount(_kingRing[c] & pawnAttacks[1 - c]);
            _kingAttackersWeight[1 - c] = 0;
            _kingAttacksCount[1 - c] = 0;

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

            // Mobility area (SF Evaluation::initialize): exclude pawns that
            // are blocked or on the first two relative ranks, our king and
            // queen, blockers for our king (pinned pieces cannot really move)
            // and squares controlled by enemy pawns.
            ulong ownPawns = us == Color.White ? whitePawns : blackPawns;
            ulong occAll = board.AllOccupancy;
            ulong lowRanks = us == Color.White
                ? (0xFFUL << 8) | (0xFFUL << 16)    // ranks 2-3
                : (0xFFUL << 48) | (0xFFUL << 40);  // ranks 7-6
            ulong shiftedDown = us == Color.White ? occAll >> 8 : occAll << 8;
            ulong dullPawns = ownPawns & (shiftedDown | lowRanks);
            Color enemyOf = Board.OppositeColor(us);
            _mobilityArea[c] = ~(dullPawns
                               | board.Pieces(us, PieceType.King)
                               | board.Pieces(us, PieceType.Queen)
                               | _blockersForKing[c]
                               | pawnAttacks[(int)enemyOf]);
        }

        Score score = default; // White's point of view.
        int phase = 0;

        for (int c = 0; c < 2; c++)
        {
            Color color = (Color)c;
            int sign = color == Color.White ? 1 : -1;
            Color enemy = Board.OppositeColor(color);
            ulong occ = board.AllOccupancy;
            ulong mobilityArea = _mobilityArea[c];

            // X-ray occupancies (SF Evaluation::pieces): bishops see through
            // queens of both colors; rooks see through queens and own rooks.
            ulong allQueens = board.Pieces(Color.White, PieceType.Queen)
                            | board.Pieces(Color.Black, PieceType.Queen);
            ulong bishopOcc = occ ^ allQueens;
            ulong rookOcc = occ ^ allQueens ^ board.Pieces(color, PieceType.Rook);
            int ownKingSq = _kingSquare[c];

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
                            PieceType.Bishop => Attacks.Bishop(sq, bishopOcc),
                            PieceType.Rook => Attacks.Rook(sq, rookOcc),
                            _ => Attacks.Queen(sq, occ),
                        };

                        // A piece pinned against its own king only really
                        // attacks along the pin line (SF pieces() 410-411).
                        if ((_blockersForKing[c] & Bitboard.SquareBB(sq)) != 0)
                            attacks &= LineThrough[ownKingSq * 64 + sq];

                        // attackedBy bookkeeping (SF Evaluation::pieces): any
                        // square this piece hits that was already covered by an
                        // earlier friendly unit becomes a double attack.
                        _attackedBy2[c] |= _attackedBy[c, AllPieces] & attacks;
                        _attackedBy[c, p] |= attacks;
                        _attackedBy[c, AllPieces] |= attacks;

                        // Non-linear mobility: table lookup by the number of
                        // attacked squares inside the mobility area. The sum
                        // also feeds the king danger formula.
                        int moves = Bitboard.PopCount(attacks & mobilityArea);
                        Score mob = EvaluationParams.MobilityBonus[p - 1][moves];
                        side += mob;
                        _mobilitySum[c] += mob;

                        // King-attack bookkeeping (SF pieces): a piece hitting
                        // the enemy king ring registers as an attacker; rooks
                        // and bishops merely AIMED at the ring (through any
                        // blockers) earn a small positional bonus instead.
                        int e = (int)enemy;
                        if ((attacks & _kingRing[e]) != 0)
                        {
                            _kingAttackersCount[c]++;
                            _kingAttackersWeight[c] += EvaluationParams.KingAttackWeights[p];
                            _kingAttacksCount[c] += Bitboard.PopCount(attacks & _attackedBy[e, (int)PieceType.King]);
                        }
                        else if (type == PieceType.Rook
                              && ((Bitboard.FileA << Squares.FileOf(sq)) & _kingRing[e]) != 0)
                            side += EvaluationParams.RookOnKingRing;
                        else if (type == PieceType.Bishop
                              && (Attacks.Bishop(sq, whitePawns | blackPawns) & _kingRing[e]) != 0)
                            side += EvaluationParams.BishopOnKingRing;
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

        // King safety (SF system): shelter/storm plus the quadratic danger
        // curve fed by the attack bookkeeping gathered in the piece loop.
        score += KingSafety(board, Color.White) * 1;
        score += KingSafety(board, Color.Black) * -1;

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

    // King safety (SF Evaluation::king + Pawns::Entry::do_king_safety).
    // Everything here is computed in RAW SF internal units — the attack
    // weights, the shelter/storm tables and the quadratic danger transform
    // (danger^2/4096) are a jointly-tuned system — and the final Score is
    // converted to NoaChess centipawns (x0.48) once at the end. Safe/unsafe
    // check terms are deliberately not ported (the v2.4.6 attempt failed on a
    // safe-check mask bug); everything else of SF's king() is here.
    private Score KingSafety(Board board, Color us)
    {
        Color them = Board.OppositeColor(us);
        int u = (int)us, t = (int)them;
        int ksq = _kingSquare[u];
        int kingFile = Squares.FileOf(ksq);

        // Shelter + storm + KingOnFile + pre-castling max + EG pawn proximity.
        Score score = DoKingSafety(board, us);

        // Squares attacked by the enemy and defended at most once, and only
        // by our king or queen.
        ulong weak = _attackedBy[t, AllPieces]
                   & ~_attackedBy2[u]
                   & (~_attackedBy[u, AllPieces]
                      | _attackedBy[u, (int)PieceType.King]
                      | _attackedBy[u, (int)PieceType.Queen]);

        // King flank: enemy attacks (double attacks count twice) and our
        // defenses on the files around the king, restricted to our camp
        // (everything except the enemy's three home ranks).
        ulong camp = us == Color.White
            ? ~((0xFFUL << 40) | (0xFFUL << 48) | (0xFFUL << 56))
            : ~((0xFFUL << 0) | (0xFFUL << 8) | (0xFFUL << 16));
        ulong flank = KingFlank[kingFile];
        ulong b1 = _attackedBy[t, AllPieces] & flank & camp;
        ulong b2 = b1 & _attackedBy2[t];
        ulong b3 = _attackedBy[u, AllPieces] & flank & camp;
        int kingFlankAttack = Bitboard.PopCount(b1) + Bitboard.PopCount(b2);
        int kingFlankDefense = Bitboard.PopCount(b3);

        // Mobility difference in SF units (our tables are stored x0.48).
        int mobilityDiffMg = (_mobilitySum[t].Mg - _mobilitySum[u].Mg) * 208 / 100;

        int kingDanger =
              _kingAttackersCount[t] * _kingAttackersWeight[t]
            + 183 * Bitboard.PopCount(_kingRing[u] & weak)
            + 98 * Bitboard.PopCount(_blockersForKing[u])
            + 69 * _kingAttacksCount[t]
            + 3 * kingFlankAttack * kingFlankAttack / 8
            + mobilityDiffMg
            - 873 * (board.Pieces(them, PieceType.Queen) == 0 ? 1 : 0)
            - 100 * ((_attackedBy[u, (int)PieceType.Knight] & _attackedBy[u, (int)PieceType.King]) != 0 ? 1 : 0)
            - 6 * score.Mg / 8
            - 4 * kingFlankDefense
            + 37;

        if (kingDanger > 100)
            score -= new Score(kingDanger * kingDanger / 4096, kingDanger / 16);

        // Penalty when the king is on a flank with no pawns of either color.
        ulong allPawns = board.Pieces(Color.White, PieceType.Pawn)
                       | board.Pieces(Color.Black, PieceType.Pawn);
        if ((allPawns & flank) == 0)
            score -= EvaluationParams.PawnlessFlank;

        // Penalty proportional to the attacks on the king's flank.
        score -= EvaluationParams.FlankAttacks * kingFlankAttack;

        // SF units -> NoaChess centipawns.
        return new Score(score.Mg * 48 / 100, score.Eg * 48 / 100);
    }

    // SF Pawns::Entry::do_king_safety — shelter at the current king square,
    // improved by the post-castling shelter when castling is still possible,
    // plus an endgame pull towards the closest own pawn. Raw SF units.
    private Score DoKingSafety(Board board, Color us)
    {
        int ksq = _kingSquare[(int)us];
        bool white = us == Color.White;
        var rights = board.CastlingRights;

        // Cache probe: the result depends only on the pawn structure, the
        // king square, our castling rights and the color.
        int ourRights = (int)rights & (white ? 0b0011 : 0b1100);
        ulong key = board.PawnZobristKey
                  ^ ((ulong)(ksq | (ourRights << 6) | ((int)us << 10)) * 0x9E3779B97F4A7C15UL);
        int slot = (int)(key & 16383);
        (ulong cachedKey, int cachedMg, int cachedEg) = _shelterCache[slot];
        if (cachedKey == key)
            return new Score(cachedMg, cachedEg);

        Score shelter = EvaluateShelter(board, us, ksq);
        bool kingSide = white
            ? (rights & CastlingRights.WhiteKingSide) != 0
            : (rights & CastlingRights.BlackKingSide) != 0;
        bool queenSide = white
            ? (rights & CastlingRights.WhiteQueenSide) != 0
            : (rights & CastlingRights.BlackQueenSide) != 0;

        // Compare by middlegame value only, like SF.
        if (kingSide)
        {
            Score s = EvaluateShelter(board, us, white ? 6 : 62); // g1 / g8
            if (s.Mg > shelter.Mg)
                shelter = s;
        }
        if (queenSide)
        {
            Score s = EvaluateShelter(board, us, white ? 2 : 58); // c1 / c8
            if (s.Mg > shelter.Mg)
                shelter = s;
        }

        // In the endgame the king wants to stay near its own pawns.
        ulong pawns = board.Pieces(us, PieceType.Pawn);
        int minPawnDist = 6;
        if ((pawns & Attacks.King(ksq)) != 0)
            minPawnDist = 1;
        else
        {
            while (pawns != 0)
            {
                int s = Bitboard.PopLsb(ref pawns);
                int dist = Math.Max(Math.Abs(Squares.FileOf(s) - Squares.FileOf(ksq)),
                                    Math.Abs(Squares.RankOf(s) - Squares.RankOf(ksq)));
                if (dist < minPawnDist)
                    minPawnDist = dist;
            }
        }

        Score result = shelter - new Score(0, 16 * minPawnDist);
        _shelterCache[slot] = (key, result.Mg, result.Eg);
        return result;
    }

    // SF Pawns::Entry::evaluate_shelter — shelter bonus and storm penalty on
    // the king file and the two adjacent files, plus the KingOnFile term.
    // Raw SF units.
    private Score EvaluateShelter(Board board, Color us, int ksq)
    {
        Color them = Board.OppositeColor(us);
        bool white = us == Color.White;
        int kingRank = Squares.RankOf(ksq);

        // Only pawns on the king's rank or in front of it (from our side).
        ulong notBehind = white
            ? ulong.MaxValue << (kingRank * 8)
            : ulong.MaxValue >> ((7 - kingRank) * 8);

        ulong ourPawns = board.Pieces(us, PieceType.Pawn) & notBehind
                       & ~_pawnAttacks[(int)them];
        ulong theirPawns = board.Pieces(them, PieceType.Pawn) & notBehind;

        var bonus = new Score(5, 5);

        int center = Math.Clamp(Squares.FileOf(ksq), 1, 6);
        for (int f = center - 1; f <= center + 1; f++)
        {
            ulong fileBB = Bitboard.FileA << f;

            // Our pawn on this file closest to the king (least advanced);
            // rank 0 means no pawn (or the pawn is behind the king).
            ulong b = ourPawns & fileBB;
            int ourRank = b == 0 ? 0
                : white ? Squares.RankOf(Bitboard.Lsb(b))
                        : 7 - Squares.RankOf(63 - System.Numerics.BitOperations.LeadingZeroCount(b));

            // Their most advanced storm pawn on this file (closest to us).
            b = theirPawns & fileBB;
            int theirRank = b == 0 ? 0
                : white ? Squares.RankOf(Bitboard.Lsb(b))
                        : 7 - Squares.RankOf(63 - System.Numerics.BitOperations.LeadingZeroCount(b));

            int d = Math.Min(f, 7 - f);
            bonus += new Score(EvaluationParams.ShelterStrength[d][ourRank], 0);

            if (ourRank != 0 && ourRank == theirRank - 1)
                bonus -= EvaluationParams.BlockedStorm[theirRank];
            else
                bonus -= new Score(EvaluationParams.UnblockedStorm[d][theirRank], 0);
        }

        // KingOnFile[our file is semi-open][their file is semi-open]
        // (semi-open for a color = it has no pawns on the king's file).
        ulong kingFileBB = Bitboard.FileA << Squares.FileOf(ksq);
        int usOpen = (board.Pieces(us, PieceType.Pawn) & kingFileBB) == 0 ? 1 : 0;
        int themOpen = (board.Pieces(them, PieceType.Pawn) & kingFileBB) == 0 ? 1 : 0;
        bonus -= EvaluationParams.KingOnFile[usOpen, themOpen];

        return bonus;
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
