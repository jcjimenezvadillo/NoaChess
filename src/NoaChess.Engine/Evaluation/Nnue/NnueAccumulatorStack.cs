using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// Stack of accumulators, one per search ply, preallocated once (zero
// allocations per node). The search pushes BEFORE making a move on the board
// and pops after unmaking it, so the top of the stack always mirrors the
// board's current position.
//
// LAZY (v4.5.0). A push no longer computes anything: it only RECORDS what the
// update would be and marks the level uncomputed. The accumulator is
// materialised on the first evaluation that actually asks for it, by walking
// back to the nearest computed ancestor, copying that perspective once and
// applying the recorded chain forward.
//
// WHY. The eager version copied both perspectives (2 x FtOutputs int16) and did
// the feature math on every single MakeMove, whether or not the child was ever
// evaluated. Profiling put PushMove at 7.39% of engine self time and the
// king-move refreshes at another 2.49%. v4.4.0's quiescence transposition work
// made it worse rather than better: far more children now return from a cached
// static eval or a TT score cutoff, and every one of those was an accumulator
// update nobody read.
//
// Incremental update rules per move (relative to the position BEFORE it):
// - Non-king move: -from +to in both perspectives.
// - Capture: additionally -capturedSquare in both perspectives.
// - Promotion: -pawn(from) +promotedPiece(to).
// - King move (incl. castling): every feature of the mover's perspective is
//   king-relative (bucket + horizontal orientation), so that whole perspective
//   is invalidated and refreshed from the accumulator cache at the next
//   evaluation. In HalfKA the king IS a feature, so the OTHER perspective
//   (whose king did not move) is patched with the king's displacement, plus any
//   capture or castling rook.
// - Null move: nothing changes but the side to move.
public sealed class NnueAccumulatorStack
{
    // Must stay ABOVE the search's own ply ceiling, which is what actually
    // bounds how deep the stack is pushed. The two constants live in different
    // files and neither knows about the other: today the search stops at 128 and
    // this is 160, so there are 31 spare slots, but raising the search ceiling
    // past this number would make PushMove write past the end of _pending with
    // no bounds check and no exception on a release build. The assertion below
    // turns that silent corruption into a build-time failure.
    private const int MaxPly = 160;

    // Build-time, not run-time: the failure this guards against has no symptom
    // to catch at run time on a release build.
    static NnueAccumulatorStack()
    {
        if (MaxPly <= Search.AlphaBetaSearch.MaxPly)
            throw new InvalidOperationException(
                $"NnueAccumulatorStack.MaxPly ({MaxPly}) must exceed the search ceiling "
              + $"({Search.AlphaBetaSearch.MaxPly}): PushMove writes _pending[_top + 1] unchecked.");
    }

    // What one push would do to the accumulators, captured at push time because
    // the board is only in the PRE-move position then (the victim's piece type
    // in particular cannot be recovered later).
    //
    // NOTE what is deliberately NOT here: the king squares. Along any chain of
    // pending updates for a perspective, that perspective's king CANNOT have
    // moved - a king move sets Valid=false for its own perspective, and an
    // invalid perspective is refreshed instead of chained. So the king square is
    // constant across the whole chain and the current board's value is the right
    // one for every link. That is what keeps this refactor cheap: no positional
    // snapshot is needed, just a handful of integers.
    private struct Pending
    {
        public bool IsNull;          // null move: no feature change at all
        public bool IsCastle;
        public Color Us;
        public PieceType Mover;      // King means "the mover's own perspective is invalid"
        public PieceType Landed;     // promotion piece, else Mover
        public PieceType Victim;     // PieceType.None when not a capture
        public int From;
        public int To;
        public int VictimSquare;     // differs from To on en passant
        public int RookFrom;
        public int RookTo;
    }

    private readonly NnueNetwork _network;
    private readonly NnueAccumulator[] _stack;
    // _pending[i] describes the move that leads INTO level i. Valid for every
    // i <= _top: PushMove writes it after incrementing and Pop only decrements,
    // so the levels at or below the top always describe the current path.
    private readonly Pending[] _pending;
    // Per-stack (therefore per-thread) finny table: king-move refreshes become
    // a diff against the last accumulator built for that king square instead of
    // a full rebuild from the bias. See NnueAccumulatorCache.
    private readonly NnueAccumulatorCache _cache;
    private int _top;

    public NnueAccumulatorStack(NnueNetwork network)
    {
        _network = network;
        _stack = new NnueAccumulator[MaxPly];
        for (int i = 0; i < MaxPly; i++)
            _stack[i] = new NnueAccumulator(network.FtOutputs);
        _pending = new Pending[MaxPly];
        _cache = new NnueAccumulatorCache(network);
    }

    // Re-anchors the stack at a new root position (start of every search).
    public void Reset(Board board)
    {
        _top = 0;
        NnueAccumulator root = _stack[0];
        _cache.Refresh(root, board, Color.White);
        _cache.Refresh(root, board, Color.Black);
        // Level 0 is the anchor every chain walk terminates on; it must always
        // be computed, or GetPerspective would copy uninitialised values.
        root.Computed[0] = true;
        root.Computed[1] = true;
    }

    // Records the update for 'move' and marks the child uncomputed. MUST be
    // called with the board still in the PRE-move position (the capture target
    // is read from it). No copy and no feature math happen here.
    public void PushMove(Board board, Move move)
    {
        NnueProfiling.CountPush();

        Color us = board.SideToMove;
        PieceType mover = board.PieceTypeAt(move.From);

        ref Pending pd = ref _pending[_top + 1];
        pd.IsNull = false;
        pd.IsCastle = false;
        pd.Us = us;
        pd.Mover = mover;
        pd.Landed = move.IsPromotion ? move.PromotionPiece : mover;
        pd.From = move.From;
        pd.To = move.To;

        if (move.Flag == MoveFlag.EnPassant)
        {
            pd.Victim = PieceType.Pawn;
            pd.VictimSquare = us == Color.White ? move.To - 8 : move.To + 8;
        }
        else if (move.IsCapture)
        {
            pd.Victim = board.PieceTypeAt(move.To);
            pd.VictimSquare = move.To;
        }
        else
        {
            pd.Victim = PieceType.None;
            pd.VictimSquare = 0;
            if (move.Flag == MoveFlag.KingCastle || move.Flag == MoveFlag.QueenCastle)
            {
                pd.IsCastle = true;
                (pd.RookFrom, pd.RookTo) = move.Flag == MoveFlag.KingCastle
                    ? (move.To + 1, move.To - 1)
                    : (move.To - 2, move.To + 1);
            }
        }

        NnueAccumulator parent = _stack[_top];
        NnueAccumulator child = _stack[++_top];

        // Our king moved: every feature of our perspective is king-relative, so
        // the whole perspective is invalidated and will be rebuilt from the
        // cache rather than chained. The opponent's perspective survives.
        int ours = (int)us;
        int theirs = ours ^ 1;
        child.Valid[ours] = parent.Valid[ours] && mover != PieceType.King;
        child.Valid[theirs] = parent.Valid[theirs];
        child.Computed[0] = false;
        child.Computed[1] = false;
    }

    // Null move: the position's pieces are identical, so the recorded update is
    // a no-op. The level still has to exist so Pop stays symmetrical with the
    // search's make/unmake pairing.
    public void PushNull()
    {
        NnueProfiling.CountPush();

        _pending[_top + 1].IsNull = true;

        NnueAccumulator parent = _stack[_top];
        NnueAccumulator child = _stack[++_top];
        child.Valid[0] = parent.Valid[0];
        child.Valid[1] = parent.Valid[1];
        child.Computed[0] = false;
        child.Computed[1] = false;
    }

    public void Pop() => _top--;

    // Accumulator for 'perspective' at the current top, materialised now if it
    // has not been already. 'board' must be the position this stack level
    // mirrors.
    public short[] GetPerspective(Board board, Color perspective)
    {
        int p = (int)perspective;
        NnueAccumulator acc = _stack[_top];

        // A king move of this perspective invalidated it: rebuild from the
        // finny table. Everything pending below is subsumed by the rebuild.
        if (!acc.Valid[p])
        {
            _cache.Refresh(acc, board, perspective);
            acc.Computed[p] = true;
            return acc.Values[p];
        }

        if (acc.Computed[p])
            return acc.Values[p];

        // Nearest ancestor whose accumulator is already materialised. Level 0 is
        // always computed, so the walk terminates. In practice it stops at
        // _top - 1 almost always: a node evaluates before pushing its children,
        // so chains are length 1 and only grow across plies that skip the static
        // eval (in check, mostly). Valid is monotone down the stack, so every
        // level from src to _top is valid whenever the top is.
        int src = _top;
        while (src > 0 && !_stack[src].Computed[p])
            src--;

        if (!_stack[src].Computed[p])
        {
            // Nothing anchored below: Reset was never called for this stack.
            // Rebuilding the top straight from the board is always a legal
            // answer, and it is the only thing standing between a missing Reset
            // and a chain that copies uninitialised values.
            _cache.Refresh(acc, board, perspective);
            acc.Computed[p] = true;
            return acc.Values[p];
        }

        // Constant for the whole chain - see the note on Pending.
        int kingSquare = board.KingSquare(perspective);

        // MATERIALISE EVERY LEVEL ON THE WAY UP, not just the top. Collapsing
        // the chain straight onto the top is one copy cheaper per evaluation and
        // strictly worse overall: it leaves the intermediate levels uncomputed,
        // so every later sibling replays their updates again. Measured on the
        // first cut of this refactor - 1,087,813 replays against the eager
        // version's 1,029,978 updates, i.e. the "lazy" stack was doing 5.6% MORE
        // feature work than the one it replaced. Writing each level as it is
        // crossed makes a parent that never evaluates pay exactly once for all
        // of its children.
        for (int i = src + 1; i <= _top; i++)
        {
            NnueAccumulator level = _stack[i];
            level.CopyPerspectiveFrom(_stack[i - 1], perspective);
            ApplyPending(level, perspective, kingSquare, in _pending[i]);
            level.Computed[p] = true;
        }

        return acc.Values[p];
    }

    // Applies one recorded update to 'target' for a single perspective. This is
    // the eager PushMove's body, with the king squares replaced by the chain's
    // constant and the invalid-perspective branches removed as unreachable.
    private void ApplyPending(NnueAccumulator target, Color perspective,
                              int kingSquare, in Pending pd)
    {
        if (pd.IsNull)
            return;

        NnueProfiling.CountPendingApplied();

        Color us = pd.Us;
        Color them = Board.OppositeColor(us);

        if (pd.Mover == PieceType.King)
        {
            // Only ever reached for the perspective whose king did NOT move: the
            // mover's own perspective is invalid along any chain and took the
            // refresh path above. In HalfKA the moving king is still a feature
            // here, so patch its displacement plus any capture or castling rook.
            if (pd.Victim != PieceType.None)
            {
                // King captures are never en passant; the victim sits on To.
                target.SubtractFeature(_network, perspective,
                    NnueFeatureIndex.Index(perspective, kingSquare, them, pd.Victim, pd.VictimSquare));
            }
            else if (pd.IsCastle)
            {
                target.MoveFeature(_network, perspective,
                    removeIndex: NnueFeatureIndex.Index(perspective, kingSquare, us, PieceType.Rook, pd.RookFrom),
                    addIndex: NnueFeatureIndex.Index(perspective, kingSquare, us, PieceType.Rook, pd.RookTo));
            }

            target.MoveFeature(_network, perspective,
                removeIndex: NnueFeatureIndex.Index(perspective, kingSquare, us, PieceType.King, pd.From),
                addIndex: NnueFeatureIndex.Index(perspective, kingSquare, us, PieceType.King, pd.To));
            return;
        }

        // Captured piece disappears (en passant took it off a different square).
        if (pd.Victim != PieceType.None)
        {
            target.SubtractFeature(_network, perspective,
                NnueFeatureIndex.Index(perspective, kingSquare, them, pd.Victim, pd.VictimSquare));
        }

        // The mover leaves its square and lands (possibly transformed by
        // promotion) - fused into a single accumulator pass.
        target.MoveFeature(_network, perspective,
            removeIndex: NnueFeatureIndex.Index(perspective, kingSquare, us, pd.Mover, pd.From),
            addIndex: NnueFeatureIndex.Index(perspective, kingSquare, us, pd.Landed, pd.To));
    }
}
