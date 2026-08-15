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

    // Threat delta state, allocated once and only used by threat nets. Indexed
    // [level * 2 + perspective] for the feature lists and [level] for the
    // changed squares, because the squares do not depend on the perspective.
    private readonly int[] _beforeThreats;
    private readonly int[] _beforeCount;
    private readonly int[] _changed;
    private readonly int[] _changedCount;
    private int _top;

    public NnueAccumulatorStack(NnueNetwork network)
    {
        _network = network;
        _stack = new NnueAccumulator[MaxPly];
        for (int i = 0; i < MaxPly; i++)
            _stack[i] = new NnueAccumulator(network.FtOutputs);
        _pending = new Pending[MaxPly];
        _cache = new NnueAccumulatorCache(network);

        // Allocated only for a net that uses them: 64 KB is nothing next to the
        // network, but a HalfKA search should not carry an array it never reads.
        int levels = network.UsesThreats ? MaxPly : 0;
        _beforeThreats = new int[levels * 2 * ThreatFeatureIndex.MaxActiveFeatures];
        _beforeCount = new int[levels * 2];
        _changed = new int[levels * ThreatDelta.MaxChangedSquares];
        _changedCount = new int[levels];
    }

    // Re-anchors the stack at a new root position (start of every search).
    public void Reset(Board board)
    {
        _top = 0;
        NnueAccumulator root = _stack[0];

        // Same split as in the materialise path: the cache only knows HalfKA, so
        // a threat-carrying net has to be anchored from the board. Getting this
        // one wrong would be worse than the other, because it seeds the root that
        // every chain terminates on.
        if (_network.UsesThreats)
        {
            root.Refresh(_network, board, Color.White);
            root.Refresh(_network, board, Color.Black);
        }
        else
        {
            _cache.Refresh(root, board, Color.White);
            _cache.Refresh(root, board, Color.Black);
        }
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

        // The threat "before" side, taken HERE because this is the last moment
        // the pre-move position exists. CompleteThreatDelta takes the other side
        // once the move is made and differences the two.
        if (_network.UsesThreats)
        {
            int level = _top + 1;
            Span<int> squares = _changed.AsSpan(level * ThreatDelta.MaxChangedSquares,
                                                ThreatDelta.MaxChangedSquares);
            int changedCount = ThreatDelta.ChangedSquares(board, move, squares);
            _changedCount[level] = changedCount;

            ulong affected = ThreatDelta.AffectedAttackers(board, squares[..changedCount]);
            for (int p = 0; p < 2; p++)
            {
                int idx = level * 2 + p;
                Span<int> dst = _beforeThreats.AsSpan(
                    idx * ThreatFeatureIndex.MaxActiveFeatures,
                    ThreatFeatureIndex.MaxActiveFeatures);
                _beforeCount[idx] = ThreatDelta.CollectFrom(board, (Color)p, affected, dst);
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

    // Finishes the threat half of the level PushMove opened, with the board now
    // in its POST-move position. Does nothing for a net without threat features.
    //
    // WHY IT IS A SECOND CALL. The rest of this class is lazy: PushMove only
    // records a Pending, and ApplyPending replays the chain later WITHOUT the
    // board, which it can afford because a HalfKA index is a pure function of
    // (king, piece, square). A threat feature is not: it depends on the whole
    // position, including every DISCOVERED relation where a slider's ray opened
    // or closed. And by the time the chain is replayed the board sits at the
    // deepest position, not at this level's. So the delta has to be taken while
    // both positions exist, which is two moments, which is two calls.
    //
    // THE TRADE. A threat net therefore materialises EAGERLY on both halves:
    // applying threat rows onto an array that has not had its HalfKA delta
    // replayed yet would corrupt it. HalfKA nets keep the lazy path untouched.
    // Laziness was worth +3.1% NPS, so this gives some of that back and takes
    // an incremental update in exchange - about six row operations instead of
    // thirty-seven plus a full board scan. Which way that nets out is a
    // measurement, not an assumption.
    public void CompleteThreatDelta(Board board)
    {
        if (!_network.UsesThreats || _top == 0)
            return;

        NnueAccumulator parent = _stack[_top - 1];
        NnueAccumulator child = _stack[_top];
        ref Pending pd = ref _pending[_top];
        int slot = _top * 2;

        for (int p = 0; p < 2; p++)
        {
            var perspective = (Color)p;

            // This perspective's king moved: every feature it has is
            // king-relative and the whole thing is renumbered, so there is no
            // delta to take. Same rule HalfKA already follows.
            //
            // LEFT UNCOMPUTED ON PURPOSE, and it took a sabotage to be sure.
            // Refreshing here as well would be correct and was the first
            // version, but Valid is MONOTONE down the chain - PushMove writes
            // `child.Valid = parent.Valid && mover != King` and PushNull just
            // copies it - so once a perspective is invalidated every level
            // below it lands here too, and none of them can chain from an
            // uncomputed ancestor. The fallback in GetPerspective then rebuilds
            // exactly the levels somebody actually evaluates, which is the
            // laziness this class exists for. Removing the refresh changed no
            // test, and that is explained rather than lucky.
            if (!child.Valid[p])
                continue;

            // HalfKA half, replayed from the parent rather than deferred.
            child.CopyPerspectiveFrom(parent, perspective);
            if (!pd.IsNull)
                ApplyPending(child, perspective, board.KingSquare(perspective), in pd);
            child.Computed[p] = true;

            // Threat half. The parent's values already carry ITS threat rows,
            // so subtracting what the affected attackers generated before and
            // adding what they generate now turns the parent's contribution
            // into the child's.
            if (pd.IsNull)
                continue;

            Span<int> after = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
            ulong affectedAfter = ThreatDelta.AffectedAttackers(
                board, _changed.AsSpan(_top * ThreatDelta.MaxChangedSquares, _changedCount[_top]));
            int afterCount = ThreatDelta.CollectFrom(board, perspective, affectedAfter, after);

            int idx = slot + p;
            ReadOnlySpan<int> before = _beforeThreats.AsSpan(
                idx * ThreatFeatureIndex.MaxActiveFeatures, _beforeCount[idx]);

            // Only the DIFFERENCE is applied. Subtracting every "before" row and
            // adding every "after" row would be correct and would also cost more
            // than the full refresh this replaces, which is the entire point.
            // Both lists hold a handful of entries, so a linear scan beats any
            // structure that would have to be allocated.
            for (int i = 0; i < before.Length; i++)
            {
                if (!Contains(after[..afterCount], before[i]))
                    child.SubtractThreat(_network, perspective, before[i]);
            }
            for (int i = 0; i < afterCount; i++)
            {
                if (!Contains(before, after[i]))
                    child.AddThreat(_network, perspective, after[i]);
            }
        }
    }

    private static bool Contains(ReadOnlySpan<int> haystack, int needle)
    {
        for (int i = 0; i < haystack.Length; i++)
            if (haystack[i] == needle)
                return true;
        return false;
    }

    public void Pop() => _top--;

    // Accumulator for 'perspective' at the current top, materialised now if it
    // has not been already. 'board' must be the position this stack level
    // mirrors.
    public short[] GetPerspective(Board board, Color perspective)
    {
        int p = (int)perspective;
        NnueAccumulator acc = _stack[_top];

        // A net carrying threat features cannot use the incremental path AT ALL,
        // and this is a correctness stop rather than a performance choice.
        //
        // The incremental machinery below replays HalfKA feature deltas: a piece
        // left a square, a piece arrived at one. Threat features do not move
        // like that. Playing a move changes what the moved piece attacks, what
        // now attacks it, AND every DISCOVERED relation where a slider's ray
        // opened or closed through the square it vacated or occupied - none of
        // which appears in a HalfKA delta. Replaying those deltas over a
        // threat-carrying accumulator would leave the threat half frozen at
        // whatever the root position had, and the engine would evaluate a
        // position that does not exist while every test still passed.
        //
        // So threats refresh from the board every time. That is measured at
        // roughly three times the cost of the evaluation it feeds, which is why
        // a threat net cannot ship until the incremental update is written; it
        // is fine for measuring STRENGTH at fixed nodes, where speed leaves the
        // comparison entirely.
        if (_network.UsesThreats)
        {
            // Maintained eagerly by CompleteThreatDelta, so the common case is
            // already done and this is a read.
            //
            // The refresh below is the fallback for the levels that call never
            // reaches: the root, and anything materialised after a Pop. It stays
            // acc.Refresh and NOT _cache.Refresh - the cache rebuilds a
            // perspective from per-king-square state it keeps for HalfKA only,
            // knows nothing about threat features, and would hand back an
            // accumulator missing half its input. The accumulator's own refresh
            // is the one that sums both transformers.
            if (!acc.Computed[p])
                acc.Refresh(_network, board, perspective);
            return acc.Values[p];
        }

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
