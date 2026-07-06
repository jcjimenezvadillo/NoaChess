namespace NoaChess.Core;

// Move generator.
//
// Two-phase strategy (the easiest to verify): generate PSEUDO-LEGAL moves
// (they follow piece movement rules but could leave the own king in check),
// then filter to LEGAL by making each move, checking whether the own king is
// attacked, and unmaking. Castling is fully validated at generation time
// because its conditions cannot be checked with a single make/unmake.
//
// Two APIs:
// - MoveList overloads: allocation-free, for hot paths (search, perft). The
//   caller owns a reusable MoveList per recursion level.
// - List<Move> overloads: convenience wrappers for cold paths (GUI, UCI,
//   game-over detection, tests).
//
// 'capturesOnly' generates just captures and promotions — what quiescence
// search needs — skipping all quiet-move work (a large share of search nodes
// are quiescence nodes).
public static class MoveGenerator
{
    // ---------- Cold-path conveniences ----------

    public static List<Move> GenerateLegalMoves(Board board)
    {
        var list = new MoveList();
        GenerateLegalMoves(board, list);
        var result = new List<Move>(list.Count);
        for (int i = 0; i < list.Count; i++)
            result.Add(list[i]);
        return result;
    }

    public static List<Move> GeneratePseudoLegalMoves(Board board)
    {
        var list = new MoveList();
        GeneratePseudoLegalMoves(board, list);
        var result = new List<Move>(list.Count);
        for (int i = 0; i < list.Count; i++)
            result.Add(list[i]);
        return result;
    }

    // ---------- Hot-path API ----------

    // Fills 'list' with every legal move for the side to move.
    public static void GenerateLegalMoves(Board board, MoveList list)
    {
        GeneratePseudoLegalMoves(board, list);

        // In-place legality filter: legal moves are compacted to the front.
        Color us = board.SideToMove;
        int legalCount = 0;
        for (int i = 0; i < list.Count; i++)
        {
            Move move = list[i];
            board.MakeMove(move);
            // After MakeMove it is the opponent's turn; check whether OUR king was left attacked.
            if (!board.IsSquareAttacked(board.KingSquare(us), board.SideToMove))
                list[legalCount++] = move;
            board.UnmakeMove();
        }
        list.Truncate(legalCount);
    }

    // Fills 'list' with pseudo-legal moves (castling excepted: fully legal).
    // With capturesOnly=true only captures and promotions are produced.
    public static void GeneratePseudoLegalMoves(Board board, MoveList list, bool capturesOnly = false)
    {
        list.Clear();
        Color us = board.SideToMove;
        Color them = Board.OppositeColor(us);
        ulong ourPieces = board.Occupancy(us);
        ulong theirPieces = board.Occupancy(them);
        ulong occupied = board.AllOccupancy;

        // In captures-only mode every piece's targets are masked to enemy
        // pieces; quiet destinations are never even enumerated.
        ulong targetMask = capturesOnly ? theirPieces : ~ourPieces;

        GeneratePawnMoves(board, list, us, occupied, theirPieces, capturesOnly);

        AddPieceMoves(list, board.Pieces(us, PieceType.Knight), Attacks.Knight, targetMask, theirPieces);
        AddSliderMoves(list, board.Pieces(us, PieceType.Bishop), occupied, bishopLike: true, rookLike: false, targetMask, theirPieces);
        AddSliderMoves(list, board.Pieces(us, PieceType.Rook), occupied, bishopLike: false, rookLike: true, targetMask, theirPieces);
        AddSliderMoves(list, board.Pieces(us, PieceType.Queen), occupied, bishopLike: true, rookLike: true, targetMask, theirPieces);
        AddPieceMoves(list, board.Pieces(us, PieceType.King), Attacks.King, targetMask, theirPieces);

        if (!capturesOnly)
            GenerateCastlingMoves(board, list, us);
    }

    private static void AddPieceMoves(MoveList list, ulong pieces, Func<int, ulong> attacksOf,
                                      ulong targetMask, ulong theirPieces)
    {
        while (pieces != 0)
        {
            int from = Bitboard.PopLsb(ref pieces);
            ulong targets = attacksOf(from) & targetMask;
            while (targets != 0)
            {
                int to = Bitboard.PopLsb(ref targets);
                bool isCapture = Bitboard.IsSet(theirPieces, to);
                list.Add(new Move(from, to, isCapture ? MoveFlag.Capture : MoveFlag.Quiet));
            }
        }
    }

    // Sliders take the occupancy, so they get their own helper (avoiding a
    // closure allocation per call, which the Func-based helper would need).
    private static void AddSliderMoves(MoveList list, ulong pieces, ulong occupied,
                                       bool bishopLike, bool rookLike,
                                       ulong targetMask, ulong theirPieces)
    {
        while (pieces != 0)
        {
            int from = Bitboard.PopLsb(ref pieces);
            ulong attacks = 0;
            if (bishopLike) attacks |= Attacks.Bishop(from, occupied);
            if (rookLike) attacks |= Attacks.Rook(from, occupied);

            ulong targets = attacks & targetMask;
            while (targets != 0)
            {
                int to = Bitboard.PopLsb(ref targets);
                bool isCapture = Bitboard.IsSet(theirPieces, to);
                list.Add(new Move(from, to, isCapture ? MoveFlag.Capture : MoveFlag.Quiet));
            }
        }
    }

    private static void GeneratePawnMoves(Board board, MoveList list, Color us,
                                          ulong occupied, ulong theirPieces, bool capturesOnly)
    {
        // Pawns are the piece with the most special cases: single push, double
        // push, diagonal captures, en passant and promotion (which additionally
        // multiplies each move by 4: queen, rook, bishop, knight).
        int pushDir = us == Color.White ? 8 : -8;      // Square delta when advancing one rank.
        int startRank = us == Color.White ? 1 : 6;     // Starting rank (allows the double push).
        int promoRank = us == Color.White ? 7 : 0;     // Promotion rank.

        ulong pawns = board.Pieces(us, PieceType.Pawn);
        while (pawns != 0)
        {
            int from = Bitboard.PopLsb(ref pawns);
            int rank = Squares.RankOf(from);

            // --- Pushes (promotions always generated: they are "tactical") ---
            int oneUp = from + pushDir;
            if (!Bitboard.IsSet(occupied, oneUp))
            {
                if (Squares.RankOf(oneUp) == promoRank)
                {
                    AddPromotions(list, from, oneUp, isCapture: false);
                }
                else if (!capturesOnly)
                {
                    list.Add(new Move(from, oneUp, MoveFlag.Quiet));

                    // Double push: only from the starting rank and with both squares free.
                    if (rank == startRank)
                    {
                        int twoUp = from + 2 * pushDir;
                        if (!Bitboard.IsSet(occupied, twoUp))
                            list.Add(new Move(from, twoUp, MoveFlag.DoublePawnPush));
                    }
                }
            }

            // --- Captures (including capture promotions) ---
            ulong captures = Attacks.Pawn(us, from) & theirPieces;
            while (captures != 0)
            {
                int to = Bitboard.PopLsb(ref captures);
                if (Squares.RankOf(to) == promoRank)
                    AddPromotions(list, from, to, isCapture: true);
                else
                    list.Add(new Move(from, to, MoveFlag.Capture));
            }

            // --- En passant: the target square is published by the Board after a double push ---
            if (board.EnPassantSquare != Squares.None &&
                Bitboard.IsSet(Attacks.Pawn(us, from), board.EnPassantSquare))
            {
                list.Add(new Move(from, board.EnPassantSquare, MoveFlag.EnPassant));
            }
        }
    }

    private static void AddPromotions(MoveList list, int from, int to, bool isCapture)
    {
        // Each promotion generates 4 moves, one per possible piece.
        MoveFlag baseFlag = isCapture ? MoveFlag.PromoKnightCapture : MoveFlag.PromoKnight;
        for (int i = 0; i < 4; i++)
            list.Add(new Move(from, to, baseFlag + i));
    }

    private static void GenerateCastlingMoves(Board board, MoveList list, Color us)
    {
        // Castling conditions:
        // 1. The right is preserved (king and rook have not moved).
        // 2. Squares between king and rook are empty.
        // 3. The king is not in check, does not pass through, nor lands on an
        //    attacked square. (The final square is checked here as well, so the
        //    generated castle is already legal.)
        Color them = Board.OppositeColor(us);
        ulong occupied = board.AllOccupancy;
        int kingSq = us == Color.White ? 4 : 60; // e1 / e8

        if (board.CastlingRights == CastlingRights.None || board.IsSquareAttacked(kingSq, them))
            return;

        CastlingRights kingSide = us == Color.White ? CastlingRights.WhiteKingSide : CastlingRights.BlackKingSide;
        CastlingRights queenSide = us == Color.White ? CastlingRights.WhiteQueenSide : CastlingRights.BlackQueenSide;

        // Short castle: f1/g1 (or f8/g8) free and not attacked.
        if (board.CastlingRights.HasFlag(kingSide) &&
            !Bitboard.IsSet(occupied, kingSq + 1) && !Bitboard.IsSet(occupied, kingSq + 2) &&
            !board.IsSquareAttacked(kingSq + 1, them) && !board.IsSquareAttacked(kingSq + 2, them))
        {
            list.Add(new Move(kingSq, kingSq + 2, MoveFlag.KingCastle));
        }

        // Long castle: b1/c1/d1 free; c1 and d1 not attacked (b1 may be
        // attacked: the king does not pass through it, only the rook does).
        if (board.CastlingRights.HasFlag(queenSide) &&
            !Bitboard.IsSet(occupied, kingSq - 1) && !Bitboard.IsSet(occupied, kingSq - 2) &&
            !Bitboard.IsSet(occupied, kingSq - 3) &&
            !board.IsSquareAttacked(kingSq - 1, them) && !board.IsSquareAttacked(kingSq - 2, them))
        {
            list.Add(new Move(kingSq, kingSq - 2, MoveFlag.QueenCastle));
        }
    }
}
