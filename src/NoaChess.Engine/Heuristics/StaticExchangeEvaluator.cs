using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Static Exchange Evaluation (SEE): answers "if the whole capture sequence on
// this square is played out with both sides always recapturing with their
// LEAST valuable attacker, who ends up ahead, and by how much?" — without
// touching the board.
//
// It replaces MVV-LVA as the tactical judge of a capture: QxP looks great to
// MVV-LVA if the pawn is defended, while SEE correctly reports -800. Uses:
// ordering captures (winning ones first), pruning losing captures in
// quiescence, and skipping tactically bad captures at shallow search depths.
//
// Implementation: the classic "swap algorithm". A gain list is filled as if
// every capture happened, then folded backwards with negamax logic — at each
// step the side to move may STOP capturing if continuing loses material.
// Sliding attackers hidden behind the piece that just captured ("x-rays") are
// discovered automatically because attackers are recomputed against the
// updated occupancy after each virtual capture.
public static class StaticExchangeEvaluator
{
    // Piece values for exchanges (index = PieceType). The king's huge value
    // means "capturing" it ends any sequence in practice.
    private static readonly int[] Value = [100, 320, 330, 500, 900, 20_000, 0];

    // Net material gain (in centipawns, from the mover's point of view) of
    // playing 'move' and resolving all recaptures on the destination square.
    public static int Evaluate(Board board, Move move)
    {
        // Promotions change the piece mid-sequence, which the plain swap
        // algorithm cannot model. They are rare and almost always worth
        // examining, so report them as winning the victim (optimistic).
        if (move.IsPromotion)
            return move.IsCapture ? Value[(int)board.PieceTypeAt(move.To)] : Value[(int)PieceType.Pawn];

        int to = move.To;
        PieceType victim = move.Flag == MoveFlag.EnPassant
            ? PieceType.Pawn
            : board.PieceTypeAt(to);

        // gain[d] = best material balance for the side moving at depth d,
        // assuming the sequence continues. Filled forward, folded backwards.
        Span<int> gain = stackalloc int[32];
        int depth = 0;
        gain[0] = Value[(int)victim];

        // Virtual occupancy: the initial attacker leaves its square (and the
        // en-passant victim leaves the board, since it is not on 'to').
        ulong occupancy = board.AllOccupancy ^ Bitboard.SquareBB(move.From);
        if (move.Flag == MoveFlag.EnPassant)
            occupancy ^= Bitboard.SquareBB(board.SideToMove == Color.White ? to - 8 : to + 8);

        PieceType pieceOnSquare = board.PieceTypeAt(move.From);
        Color side = Board.OppositeColor(board.SideToMove);

        while (depth < 31)
        {
            // All remaining pieces of 'side' that attack the square, given
            // the current virtual occupancy (this is what reveals x-rays).
            ulong attackers = AttackersTo(board, to, occupancy) & occupancy & board.Occupancy(side);
            if (attackers == 0)
                break;

            // Recapture with the least valuable attacker.
            PieceType attacker = PieceType.None;
            ulong attackerBit = 0;
            for (int t = 0; t < 6; t++)
            {
                ulong candidates = attackers & board.Pieces(side, (PieceType)t);
                if (candidates != 0)
                {
                    attacker = (PieceType)t;
                    attackerBit = candidates & (~candidates + 1); // Lowest bit.
                    break;
                }
            }

            depth++;
            gain[depth] = Value[(int)pieceOnSquare] - gain[depth - 1];

            pieceOnSquare = attacker;
            occupancy ^= attackerBit;
            side = Board.OppositeColor(side);
        }

        // Backward negamax fold: at each depth the side to move takes the
        // better of "stop here" and "keep capturing".
        while (depth > 0)
        {
            gain[depth - 1] = -Math.Max(-gain[depth - 1], gain[depth]);
            depth--;
        }

        return gain[0];
    }

    // Every piece (of both colors) attacking 'square' under the given virtual
    // occupancy. Pawn attackers are found with the reverse-color table trick
    // (see Board.IsSquareAttacked).
    private static ulong AttackersTo(Board board, int square, ulong occupancy)
    {
        ulong attackers = 0;

        attackers |= Attacks.Pawn(Color.Black, square) & board.Pieces(Color.White, PieceType.Pawn);
        attackers |= Attacks.Pawn(Color.White, square) & board.Pieces(Color.Black, PieceType.Pawn);
        attackers |= Attacks.Knight(square) &
            (board.Pieces(Color.White, PieceType.Knight) | board.Pieces(Color.Black, PieceType.Knight));
        attackers |= Attacks.King(square) &
            (board.Pieces(Color.White, PieceType.King) | board.Pieces(Color.Black, PieceType.King));

        ulong bishopLike = board.Pieces(Color.White, PieceType.Bishop) | board.Pieces(Color.Black, PieceType.Bishop)
                         | board.Pieces(Color.White, PieceType.Queen) | board.Pieces(Color.Black, PieceType.Queen);
        attackers |= Attacks.Bishop(square, occupancy) & bishopLike;

        ulong rookLike = board.Pieces(Color.White, PieceType.Rook) | board.Pieces(Color.Black, PieceType.Rook)
                       | board.Pieces(Color.White, PieceType.Queen) | board.Pieces(Color.Black, PieceType.Queen);
        attackers |= Attacks.Rook(square, occupancy) & rookLike;

        return attackers;
    }
}
