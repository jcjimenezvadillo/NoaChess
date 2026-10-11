using System.Runtime.CompilerServices;
using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Static Exchange Evaluation (SEE): answers "if the whole capture sequence on
// this square is played out with both sides always recapturing with their
// LEAST valuable attacker, who ends up ahead, and by how much?" - without
// touching the board.
//
// It replaces MVV-LVA as the tactical judge of a capture: QxP looks great to
// MVV-LVA if the pawn is defended, while SEE correctly reports -800. Uses:
// ordering captures (winning ones first), pruning losing captures in
// quiescence, and skipping tactically bad captures at shallow search depths.
//
// Implementation: the classic "swap algorithm". A gain list is filled as if
// every capture happened, then folded backwards with negamax logic - at each
// step the side to move may STOP capturing if continuing loses material.
// Sliding attackers hidden behind the piece that just captured ("x-rays") are
// discovered automatically because attackers are recomputed against the
// updated occupancy after each virtual capture.
public static class StaticExchangeEvaluator
{
    // Piece values for exchanges (index = PieceType). The king's huge value
    // means "capturing" it ends any sequence in practice.
    private static readonly int[] Value = [100, 320, 330, 500, 900, 20_000, 0];

    // Fast check: does this capture lose more than 'threshold' centipawns?
    // Shortcut first: capturing an equal-or-higher-valued victim can never
    // lose material (worst case: victim won, attacker lost, net >= 0), so
    // the full swap algorithm - comparatively expensive, and this is called
    // for every capture at every node - only runs for "upward" captures
    // like QxP or RxN.
    //
    // A threshold-only variant with early exits (the reference's see_ge) was
    // written and reverted on 2026-08-07: it passed an exhaustive equivalence
    // test against Evaluate (160 positions, every pseudo-legal move, eleven
    // thresholds, ~70k comparisons) and STILL changed the search's node count
    // by 1.8%. Worth about 1% NPS, so not worth shipping a behaviour change
    // nobody could explain. Anyone retrying it: the node bench is the oracle,
    // not the unit test.
    public static bool LosesAtLeast(Board board, Move move, int threshold = 0)
    {
        if (move.IsPromotion)
            return false;

        PieceType victim = move.Flag == MoveFlag.EnPassant
            ? PieceType.Pawn
            : board.PieceTypeAt(move.To);

        if (Value[(int)victim] >= Value[(int)board.PieceTypeAt(move.From)])
            return false;

        return Evaluate(board, move) < -threshold;
    }

    // Net material gain (in centipawns, from the mover's point of view) of
    // playing 'move' and resolving all recaptures on the destination square.
    // [SkipLocalsInit]: the gain list is written at index 0 and then at each
    // depth before the fold reads it, so its zeroing bought nothing and cost a
    // 128-byte memset on one of the search's most frequent calls.
    [SkipLocalsInit]
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

        // AttackersTo rebuilt ALL of this on every iteration, and only the
        // slider half of it can ever change: removing a piece from the virtual
        // occupancy can open a line, it cannot change what a pawn, a knight or
        // a king attacks. Hoisted into plain locals rather than an array on
        // purpose. A version of this that cached all twelve piece bitboards in
        // a stackalloc span was MEASURED 1.64% SLOWER on 2026-09-16, because
        // most SEE calls end after one or two iterations and it charged them
        // all a fixed setup cost. These five locals add no fixed cost at all:
        // iteration one computes exactly what it used to, and every iteration
        // after it saves twelve bitboard reads and four attack lookups.
        ulong fixedAttackers =
              (Attacks.Pawn(Color.Black, to) & board.Pieces(Color.White, PieceType.Pawn))
            | (Attacks.Pawn(Color.White, to) & board.Pieces(Color.Black, PieceType.Pawn))
            | (Attacks.Knight(to) & (board.Pieces(Color.White, PieceType.Knight)
                                   | board.Pieces(Color.Black, PieceType.Knight)))
            | (Attacks.King(to) & (board.Pieces(Color.White, PieceType.King)
                                 | board.Pieces(Color.Black, PieceType.King)));
        ulong queens = board.Pieces(Color.White, PieceType.Queen)
                     | board.Pieces(Color.Black, PieceType.Queen);
        ulong bishopLike = board.Pieces(Color.White, PieceType.Bishop)
                         | board.Pieces(Color.Black, PieceType.Bishop) | queens;
        ulong rookLike = board.Pieces(Color.White, PieceType.Rook)
                       | board.Pieces(Color.Black, PieceType.Rook) | queens;
        ulong whiteOcc = board.Occupancy(Color.White);
        ulong blackOcc = board.Occupancy(Color.Black);

        while (depth < 31)
        {
            // All remaining pieces of 'side' that attack the square, given
            // the current virtual occupancy (this is what reveals x-rays).
            ulong attackers = (fixedAttackers
                               | (Attacks.Bishop(to, occupancy) & bishopLike)
                               | (Attacks.Rook(to, occupancy) & rookLike))
                            & occupancy
                            & (side == Color.White ? whiteOcc : blackOcc);
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
}
