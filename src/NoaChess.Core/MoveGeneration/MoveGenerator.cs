namespace NoaChess.Core;

// Move generator.
//
// Two-phase strategy (the easiest to verify for an MVP):
// 1. Generate PSEUDO-LEGAL moves: they follow the movement rules of each piece
//    but could leave the own king in check (e.g. moving a pinned piece).
// 2. Filter down to LEGAL: each move is made on the board, we check whether
//    the own king ends up attacked, and the move is unmade. Moves that leave
//    the king in check are discarded.
//
// This second phase is slower than "pin detection" techniques, but it is
// trivially correct and validated with Perft. It will be optimized in future versions.
public static class MoveGenerator
{
    // Generates every legal move for the side to move.
    public static List<Move> GenerateLegalMoves(Board board)
    {
        var pseudoLegal = GeneratePseudoLegalMoves(board);
        var legal = new List<Move>(pseudoLegal.Count);
        Color us = board.SideToMove;

        foreach (Move move in pseudoLegal)
        {
            board.MakeMove(move);
            // After MakeMove it is the opponent's turn; we check whether OUR king was left attacked.
            if (!board.IsSquareAttacked(board.KingSquare(us), board.SideToMove))
                legal.Add(move);
            board.UnmakeMove();
        }
        return legal;
    }

    // Generates pseudo-legal moves (they may leave the own king in check).
    // Castling is the exception: it is fully validated here, because its
    // conditions ("the king does not pass through attacked squares") cannot be
    // checked with the single-move make/unmake method.
    public static List<Move> GeneratePseudoLegalMoves(Board board)
    {
        var moves = new List<Move>(64);
        Color us = board.SideToMove;
        Color them = Board.OppositeColor(us);
        ulong ourPieces = board.Occupancy(us);
        ulong theirPieces = board.Occupancy(them);
        ulong occupied = board.AllOccupancy;

        GeneratePawnMoves(board, moves, us, them, occupied, theirPieces);

        // Knight, bishop, rook, queen and king share the same pattern:
        // get the attack bitboard, remove squares holding friendly pieces,
        // and emit one move (capture or not) per resulting square.
        AddPieceMoves(moves, board.Pieces(us, PieceType.Knight), sq => Attacks.Knight(sq), ourPieces, theirPieces);
        AddPieceMoves(moves, board.Pieces(us, PieceType.Bishop), sq => Attacks.Bishop(sq, occupied), ourPieces, theirPieces);
        AddPieceMoves(moves, board.Pieces(us, PieceType.Rook), sq => Attacks.Rook(sq, occupied), ourPieces, theirPieces);
        AddPieceMoves(moves, board.Pieces(us, PieceType.Queen), sq => Attacks.Queen(sq, occupied), ourPieces, theirPieces);
        AddPieceMoves(moves, board.Pieces(us, PieceType.King), sq => Attacks.King(sq), ourPieces, theirPieces);

        GenerateCastlingMoves(board, moves, us);

        return moves;
    }

    private static void AddPieceMoves(List<Move> moves, ulong pieces, Func<int, ulong> attacksOf,
                                      ulong ourPieces, ulong theirPieces)
    {
        while (pieces != 0)
        {
            int from = Bitboard.PopLsb(ref pieces);
            // We cannot land on friendly pieces.
            ulong targets = attacksOf(from) & ~ourPieces;
            while (targets != 0)
            {
                int to = Bitboard.PopLsb(ref targets);
                bool isCapture = Bitboard.IsSet(theirPieces, to);
                moves.Add(new Move(from, to, isCapture ? MoveFlag.Capture : MoveFlag.Quiet));
            }
        }
    }

    private static void GeneratePawnMoves(Board board, List<Move> moves, Color us, Color them,
                                          ulong occupied, ulong theirPieces)
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

            // --- Pushes ---
            int oneUp = from + pushDir;
            if (!Bitboard.IsSet(occupied, oneUp)) // The square ahead is free.
            {
                if (Squares.RankOf(oneUp) == promoRank)
                    AddPromotions(moves, from, oneUp, isCapture: false);
                else
                    moves.Add(new Move(from, oneUp, MoveFlag.Quiet));

                // Double push: only from the starting rank and with both squares free.
                if (rank == startRank)
                {
                    int twoUp = from + 2 * pushDir;
                    if (!Bitboard.IsSet(occupied, twoUp))
                        moves.Add(new Move(from, twoUp, MoveFlag.DoublePawnPush));
                }
            }

            // --- Captures (including capture promotions) ---
            ulong captures = Attacks.Pawn(us, from) & theirPieces;
            while (captures != 0)
            {
                int to = Bitboard.PopLsb(ref captures);
                if (Squares.RankOf(to) == promoRank)
                    AddPromotions(moves, from, to, isCapture: true);
                else
                    moves.Add(new Move(from, to, MoveFlag.Capture));
            }

            // --- En passant: the target square is published by the Board after a double push ---
            if (board.EnPassantSquare != Squares.None &&
                Bitboard.IsSet(Attacks.Pawn(us, from), board.EnPassantSquare))
            {
                moves.Add(new Move(from, board.EnPassantSquare, MoveFlag.EnPassant));
            }
        }
    }

    private static void AddPromotions(List<Move> moves, int from, int to, bool isCapture)
    {
        // Each promotion generates 4 moves, one per possible piece.
        MoveFlag baseFlag = isCapture ? MoveFlag.PromoKnightCapture : MoveFlag.PromoKnight;
        for (int i = 0; i < 4; i++)
            moves.Add(new Move(from, to, baseFlag + i));
    }

    private static void GenerateCastlingMoves(Board board, List<Move> moves, Color us)
    {
        // Castling conditions:
        // 1. The right is preserved (king and rook have not moved).
        // 2. Squares between king and rook are empty.
        // 3. The king is not in check, does not pass through, nor lands on an
        //    attacked square. (The final square is checked here as well, so the
        //    generated castle is already legal.)
        Color them = Board.OppositeColor(us);
        ulong occupied = board.AllOccupancy;
        int kingSq = us == Color.White ? Squares.Parse("e1") : Squares.Parse("e8");

        if (board.IsSquareAttacked(kingSq, them))
            return; // Castling is not allowed while in check.

        CastlingRights kingSide = us == Color.White ? CastlingRights.WhiteKingSide : CastlingRights.BlackKingSide;
        CastlingRights queenSide = us == Color.White ? CastlingRights.WhiteQueenSide : CastlingRights.BlackQueenSide;

        // Short castle: f1/g1 (or f8/g8) free and not attacked.
        if (board.CastlingRights.HasFlag(kingSide) &&
            !Bitboard.IsSet(occupied, kingSq + 1) && !Bitboard.IsSet(occupied, kingSq + 2) &&
            !board.IsSquareAttacked(kingSq + 1, them) && !board.IsSquareAttacked(kingSq + 2, them))
        {
            moves.Add(new Move(kingSq, kingSq + 2, MoveFlag.KingCastle));
        }

        // Long castle: b1/c1/d1 free; c1 and d1 not attacked (b1 may be
        // attacked: the king does not pass through it, only the rook does).
        if (board.CastlingRights.HasFlag(queenSide) &&
            !Bitboard.IsSet(occupied, kingSq - 1) && !Bitboard.IsSet(occupied, kingSq - 2) &&
            !Bitboard.IsSet(occupied, kingSq - 3) &&
            !board.IsSquareAttacked(kingSq - 1, them) && !board.IsSquareAttacked(kingSq - 2, them))
        {
            moves.Add(new Move(kingSq, kingSq - 2, MoveFlag.QueenCastle));
        }
    }
}
