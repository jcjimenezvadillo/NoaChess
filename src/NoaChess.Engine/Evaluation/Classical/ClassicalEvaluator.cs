using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Classical evaluator, v1.0: material + piece-square tables + pawn structure.
//
// The pieces of the evaluation live in separate files so each can grow (and
// be tuned) independently:
// - EvaluationParams: every tunable constant.
// - PieceSquareTables: the positional tables.
// - PawnStructureEvaluator: doubled/isolated/passed pawns, cached by pawn hash.
//
// Still missing (later versions): mobility, king safety, bishop pair, rook on
// open files, tapered middlegame/endgame interpolation, NNUE (v2.0).
public sealed class ClassicalEvaluator : IPositionEvaluator
{
    private readonly PawnStructureEvaluator _pawnStructure = new();

    public int Evaluate(Board board)
    {
        int score = 0; // Positive = white advantage (converted at the end).
        Span<int> material = stackalloc int[2]; // Total material per side (no king).

        for (int c = 0; c < 2; c++)
        {
            Color color = (Color)c;
            int sign = color == Color.White ? 1 : -1;

            for (int p = 0; p < 6; p++)
            {
                ulong pieces = board.Pieces(color, (PieceType)p);
                while (pieces != 0)
                {
                    int sq = Bitboard.PopLsb(ref pieces);
                    material[c] += EvaluationParams.MaterialValue[p];
                    score += sign * (EvaluationParams.MaterialValue[p]
                                     + PieceSquareTables.Value((PieceType)p, color, sq));
                }
            }
        }

        // Pawn structure comes from its own cached evaluator (white-relative).
        score += _pawnStructure.Evaluate(board);

        // Mop-up: converting won endgames (material + PST alone leave the
        // engine shuffling with K+R vs K until the search stumbles on a mate,
        // burning clock and risking fifty-move draws).
        score += MopUp(board, Color.White, material[0], material[1])
               - MopUp(board, Color.Black, material[1], material[0]);

        // Negamax needs the score relative to the side to move.
        return board.SideToMove == Color.White ? score : -score;
    }

    // Bonus for the clearly winning side in a bare-bones endgame: push the
    // enemy king towards the edge/corner (where mates happen) and bring the
    // own king closer (rook/queen mates need the king's help). The gradient
    // gives the search a slope to follow long before the mate itself is
    // within its horizon.
    private static int MopUp(Board board, Color us, int myMaterial, int theirMaterial)
    {
        // Only when clearly winning against (nearly) a lone king.
        if (myMaterial < theirMaterial + 400 || theirMaterial > 300)
            return 0;

        int myKing = board.KingSquare(us);
        int theirKing = board.KingSquare(Board.OppositeColor(us));

        // Enemy king's Manhattan distance from the board center (0 in the
        // middle, 6 in a corner)...
        int centerDistance = Math.Abs(2 * Squares.FileOf(theirKing) - 7) / 2
                           + Math.Abs(2 * Squares.RankOf(theirKing) - 7) / 2;

        // ...and the distance between both kings (2..14).
        int kingsDistance = Math.Abs(Squares.FileOf(myKing) - Squares.FileOf(theirKing))
                          + Math.Abs(Squares.RankOf(myKing) - Squares.RankOf(theirKing));

        return 10 * centerDistance + 4 * (14 - kingsDistance);
    }
}
