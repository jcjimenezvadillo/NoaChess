namespace NoaChess.Core;

// Global game status from the point of view of the rules.
public enum GameResult
{
    Ongoing,              // The game continues.
    Checkmate,            // The side to move is checkmated (it loses).
    Stalemate,            // Draw by stalemate: no legal moves but no check.
    FiftyMoveRule,        // Draw by the fifty-move rule.
    ThreefoldRepetition   // Draw: the same position occurred three times.
}

// Game-over detection. It lives in Core because it is a pure chess rule:
// the GUI and the engine only query the result, they never compute it themselves.
public static class GameState
{
    public static GameResult GetResult(Board board)
    {
        // 100 half-moves without a capture or a pawn move = draw.
        if (board.HalfmoveClock >= 100)
            return GameResult.FiftyMoveRule;

        // Current position seen twice before (three occurrences in total).
        if (board.CountRepetitions() >= 2)
            return GameResult.ThreefoldRepetition;

        // No legal moves: checkmate if in check, stalemate otherwise.
        if (MoveGenerator.GenerateLegalMoves(board).Count == 0)
            return board.IsInCheck() ? GameResult.Checkmate : GameResult.Stalemate;

        return GameResult.Ongoing;
    }
}
