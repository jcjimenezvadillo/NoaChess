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
                    score += sign * (EvaluationParams.MaterialValue[p]
                                     + PieceSquareTables.Value((PieceType)p, color, sq));
                }
            }
        }

        // Pawn structure comes from its own cached evaluator (white-relative).
        score += _pawnStructure.Evaluate(board);

        // Negamax needs the score relative to the side to move.
        return board.SideToMove == Color.White ? score : -score;
    }
}
