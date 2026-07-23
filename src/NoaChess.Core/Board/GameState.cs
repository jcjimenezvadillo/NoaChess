namespace NoaChess.Core;

// Global game status from the point of view of the rules.
public enum GameResult
{
    Ongoing,              // The game continues.
    Checkmate,            // The side to move is checkmated (it loses).
    Stalemate,            // Draw by stalemate: no legal moves but no check.
    InsufficientMaterial, // Dead position: no legal sequence can end in mate.
    FiftyMoveRule,        // Draw by the fifty-move rule.
    ThreefoldRepetition   // Draw: the same position occurred three times.
}

// Game-over detection. It lives in Core because it is a pure chess rule:
// the GUI and the engine only query the result, they never compute it themselves.
public static class GameState
{
    public static GameResult GetResult(Board board)
    {
        // Checkmate takes precedence over a draw claim on the same move. In
        // particular, a quiet mating move may also advance the halfmove clock
        // to 100; declaring the clock draw first would erase that checkmate.
        if (!MoveGenerator.HasLegalMove(board))
            return board.IsInCheck() ? GameResult.Checkmate : GameResult.Stalemate;

        if (IsDeadPosition(board))
            return GameResult.InsufficientMaterial;

        // 100 half-moves without a capture or a pawn move = draw.
        if (board.HalfmoveClock >= 100)
            return GameResult.FiftyMoveRule;

        // Current position seen twice before (three occurrences in total).
        if (board.CountRepetitions() >= 2)
            return GameResult.ThreefoldRepetition;

        return GameResult.Ongoing;
    }

    // True for positions in which checkmate is impossible by ANY legal series
    // of moves: bare kings, a single minor, or bishops confined to one colour
    // complex. KNN-K is deliberately not included: mate cannot be forced, but
    // a mating position can still arise with cooperation, so it is not dead.
    public static bool IsDeadPosition(Board board)
    {
        ulong pawnsRooksQueens = board.Pieces(Color.White, PieceType.Pawn)
                                | board.Pieces(Color.Black, PieceType.Pawn)
                                | board.Pieces(Color.White, PieceType.Rook)
                                | board.Pieces(Color.Black, PieceType.Rook)
                                | board.Pieces(Color.White, PieceType.Queen)
                                | board.Pieces(Color.Black, PieceType.Queen);
        if (pawnsRooksQueens != 0)
            return false;

        ulong knights = board.Pieces(Color.White, PieceType.Knight)
                      | board.Pieces(Color.Black, PieceType.Knight);
        ulong bishops = board.Pieces(Color.White, PieceType.Bishop)
                      | board.Pieces(Color.Black, PieceType.Bishop);
        if (Bitboard.PopCount(knights | bishops) <= 1)
            return true;
        if (knights != 0)
            return false;

        // a1 is dark; this mask contains b1, d1, ... and alternates by rank.
        const ulong LightSquares = 0x55AA55AA55AA55AAUL;
        return (bishops & LightSquares) == 0 || (bishops & ~LightSquares) == 0;
    }
}
