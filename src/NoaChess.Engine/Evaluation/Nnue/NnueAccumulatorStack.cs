using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// Stack of accumulators, one per search ply, preallocated once (zero
// allocations per node). The search pushes BEFORE making a move on the board
// and pops after unmaking it, so the top of the stack always mirrors the
// board's current position.
//
// Incremental update rules per move (relative to the position BEFORE it):
// - Non-king move: -from +to in both perspectives.
// - Capture: additionally -capturedSquare in both perspectives.
// - Promotion: -pawn(from) +promotedPiece(to).
// - King move (incl. castling): every feature of the mover's perspective is
//   king-relative, so that whole perspective is invalidated and lazily
//   refreshed at the next evaluation. The OTHER perspective only sees the
//   rook displacement on castling (kings are not features).
// - Null move: nothing changes but the side to move; the top is duplicated.
public sealed class NnueAccumulatorStack
{
    private const int MaxPly = 160;

    private readonly NnueNetwork _network;
    private readonly NnueAccumulator[] _stack;
    private int _top;

    public NnueAccumulatorStack(NnueNetwork network)
    {
        _network = network;
        _stack = new NnueAccumulator[MaxPly];
        for (int i = 0; i < MaxPly; i++)
            _stack[i] = new NnueAccumulator(network.FtOutputs);
    }

    public NnueAccumulator Top => _stack[_top];

    // Re-anchors the stack at a new root position (start of every search).
    public void Reset(Board board)
    {
        _top = 0;
        _stack[0].Refresh(_network, board, Color.White);
        _stack[0].Refresh(_network, board, Color.Black);
    }

    // Prepares the child accumulator for 'move'. MUST be called with the
    // board still in the PRE-move position (the capture target and king
    // squares are read from it).
    public void PushMove(Board board, Move move)
    {
        NnueAccumulator parent = _stack[_top];
        NnueAccumulator child = _stack[++_top];
        child.CopyFrom(parent);

        Color us = board.SideToMove;
        Color them = Board.OppositeColor(us);
        PieceType mover = board.PieceTypeAt(move.From);

        if (mover == PieceType.King)
        {
            // Every feature of our perspective is relative to our king: a
            // king move rewrites them all. Mark invalid; the next Evaluate
            // that needs it will refresh from the then-current board.
            child.Valid[(int)us] = false;

            // The opponent's perspective does not contain kings, so it only
            // changes if the move also captured or displaced a rook (castle).
            if (move.IsCapture)
            {
                PieceType victim = board.PieceTypeAt(move.To);
                RemovePiece(child, perspective: them, board, owner: them, victim, move.To);
            }
            else if (move.Flag == MoveFlag.KingCastle || move.Flag == MoveFlag.QueenCastle)
            {
                (int rookFrom, int rookTo) = move.Flag == MoveFlag.KingCastle
                    ? (move.To + 1, move.To - 1)
                    : (move.To - 2, move.To + 1);
                MovePiece(child, them, board, us, PieceType.Rook, rookFrom, rookTo);
            }
            return;
        }

        // ---- Non-king movers: patch both perspectives ----
        foreach (Color perspective in Perspectives)
        {
            if (!child.Valid[(int)perspective])
                continue; // Already scheduled for a full refresh.

            int kingSq = board.KingSquare(perspective);

            // Captured piece disappears.
            if (move.Flag == MoveFlag.EnPassant)
            {
                int capturedSq = us == Color.White ? move.To - 8 : move.To + 8;
                child.SubtractFeature(_network, perspective,
                    NnueFeatureIndex.Index(perspective, kingSq, them, PieceType.Pawn, capturedSq));
            }
            else if (move.IsCapture)
            {
                PieceType victim = board.PieceTypeAt(move.To);
                child.SubtractFeature(_network, perspective,
                    NnueFeatureIndex.Index(perspective, kingSq, them, victim, move.To));
            }

            // The mover leaves its square...
            child.SubtractFeature(_network, perspective,
                NnueFeatureIndex.Index(perspective, kingSq, us, mover, move.From));

            // ...and lands (possibly transformed by promotion).
            PieceType landed = move.IsPromotion ? move.PromotionPiece : mover;
            child.AddFeature(_network, perspective,
                NnueFeatureIndex.Index(perspective, kingSq, us, landed, move.To));
        }
    }

    // Null move: the position's pieces are identical; duplicate the top so
    // Pop stays symmetrical with the search's make/unmake pairing.
    public void PushNull()
    {
        _stack[_top + 1].CopyFrom(_stack[_top]);
        _top++;
    }

    public void Pop() => _top--;

    // Accumulator for 'perspective', refreshing it first if a king move
    // invalidated it. 'board' must be the position this stack level mirrors.
    public short[] GetPerspective(Board board, Color perspective)
    {
        NnueAccumulator acc = _stack[_top];
        if (!acc.Valid[(int)perspective])
            acc.Refresh(_network, board, perspective);
        return acc.Values[(int)perspective];
    }

    private static readonly Color[] Perspectives = [Color.White, Color.Black];

    private void RemovePiece(NnueAccumulator acc, Color perspective, Board board,
                             Color owner, PieceType type, int square)
    {
        if (!acc.Valid[(int)perspective])
            return;
        int kingSq = board.KingSquare(perspective);
        acc.SubtractFeature(_network, perspective,
            NnueFeatureIndex.Index(perspective, kingSq, owner, type, square));
    }

    private void MovePiece(NnueAccumulator acc, Color perspective, Board board,
                           Color owner, PieceType type, int from, int to)
    {
        if (!acc.Valid[(int)perspective])
            return;
        int kingSq = board.KingSquare(perspective);
        acc.SubtractFeature(_network, perspective,
            NnueFeatureIndex.Index(perspective, kingSq, owner, type, from));
        acc.AddFeature(_network, perspective,
            NnueFeatureIndex.Index(perspective, kingSq, owner, type, to));
    }
}
