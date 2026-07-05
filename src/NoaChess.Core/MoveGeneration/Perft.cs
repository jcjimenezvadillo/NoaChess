namespace NoaChess.Core;

// Perft ("performance test"): counts the number of nodes (positions) reachable
// at a given depth. It is THE standard tool to validate a move generator: the
// correct counts for reference positions are publicly known, and any bug (an
// extra castle, a missing en passant...) makes the count mismatch.
public static class Perft
{
    // Number of leaf positions reachable in exactly 'depth' moves.
    public static long Count(Board board, int depth)
    {
        if (depth == 0)
            return 1;

        var moves = MoveGenerator.GenerateLegalMoves(board);

        // Common shortcut: at depth 1 the result is simply the number of legal
        // moves, without needing to make them.
        if (depth == 1)
            return moves.Count;

        long nodes = 0;
        foreach (Move move in moves)
        {
            board.MakeMove(move);
            nodes += Count(board, depth - 1);
            board.UnmakeMove();
        }
        return nodes;
    }
}
