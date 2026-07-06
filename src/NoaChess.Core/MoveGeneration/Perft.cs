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

        // One reusable MoveList per recursion level: perft doubles as the
        // move-generation benchmark, so it uses the allocation-free path.
        var lists = new MoveList[depth];
        for (int i = 0; i < depth; i++)
            lists[i] = new MoveList();

        return Count(board, depth, lists);
    }

    private static long Count(Board board, int depth, MoveList[] lists)
    {
        MoveList moves = lists[depth - 1];
        MoveGenerator.GenerateLegalMoves(board, moves);

        // Common shortcut: at depth 1 the result is simply the number of legal
        // moves, without needing to make them.
        if (depth == 1)
            return moves.Count;

        long nodes = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            board.MakeMove(moves[i]);
            nodes += Count(board, depth - 1, lists);
            board.UnmakeMove();
        }
        return nodes;
    }
}
