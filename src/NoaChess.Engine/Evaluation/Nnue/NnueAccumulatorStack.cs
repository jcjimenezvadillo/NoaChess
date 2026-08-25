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

    // Threat delta state, allocated once and only used by threat nets. All of
    // it is indexed by [level] alone: what is stored is geometry - which piece
    // attacks which - and that does not depend on the perspective. It used to
    // be [level * 2 + perspective], which stored the same fact twice under two
    // numberings.
    private readonly int[] _beforeThreats;
    private readonly int[] _beforeCount;
    private readonly int[] _changed;
    private readonly int[] _afterScratch;
    private readonly int[] _removedScratch;
    private readonly int[] _addedScratch;
    // The DIFFERENCE of each level, kept as perspective-free pairs so it can be
    // applied later instead of now. This is what makes a threat net lazy.
    private readonly int[] _deltaPairs;
    private readonly int[] _deltaRemovedCount;
    private readonly int[] _deltaAddedCount;
    // Whether this level's difference has actually been computed. NOT
    // decoration: PushNull has no CompleteThreatDelta at all, and a pushed move
    // found illegal is popped before reaching one, so a level can exist with
    // nothing but a previous sibling's numbers in its slot. Applying those is
    // silent corruption, and it is what the node-identity check caught here.
    private readonly bool[] _deltaKnown;
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
        // One list per level, not one per level PER PERSPECTIVE: what is stored
        // is the geometry, which both perspectives share. That also halves what
        // this costs per search thread.
        _beforeThreats = new int[levels * ThreatFeatureIndex.MaxActiveFeatures];
        _beforeCount = new int[levels];
        _changed = new int[levels * ThreatDelta.MaxChangedSquares];
        // Scratch for CompleteThreatDelta, allocated once instead of being
        // zeroed at every node. See the comment at its use.
        _afterScratch = new int[ThreatFeatureIndex.MaxActiveFeatures];
        _removedScratch = new int[ThreatFeatureIndex.MaxActiveFeatures];
        _addedScratch = new int[ThreatFeatureIndex.MaxActiveFeatures];
        // Removed first, then added, in one block per level. The bound is the
        // same one the collectors use: a difference cannot hold more relations
        // than the lists it came from, and undersizing this is how the buffer
        // overflow that killed seven games happened the first time.
        _deltaPairs = new int[levels * 2 * ThreatFeatureIndex.MaxActiveFeatures];
        _deltaRemovedCount = new int[levels];
        _deltaAddedCount = new int[levels];
        _deltaKnown = new bool[levels];
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
            Span<int> dst = _beforeThreats.AsSpan(
                level * ThreatFeatureIndex.MaxActiveFeatures,
                ThreatFeatureIndex.MaxActiveFeatures);
            _beforeCount[level] = ThreatDelta.CollectPairs(board, affected, dst);
            _deltaKnown[level] = false;   // CompleteThreatDelta has not run yet
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

        // A threat net copies the parent here instead of leaving the level for
        // the fallback to REBUILD FROM THE BOARD.
        //
        // A null move changes the side to move and nothing else: the pieces do
        // not move, so every threat feature is identical and the right answer is
        // the parent's accumulator unchanged. Without this the level stayed
        // uncomputed, CompleteThreatDelta never runs for a null (the search only
        // calls it after a real move), and the first evaluation below it paid a
        // full refresh - the exact cost this whole path exists to avoid, on one
        // of the most frequent pushes in the search, since null-move pruning is
        // attempted at most interior nodes.
        //
        // Correct either way, which is why the parity test passed before this
        // and passes after: the fallback rebuilds the same numbers, just slowly.
        if (_network.UsesThreats)
        {
            // A null move changes the side to move and nothing else: the pieces
            // do not move, so every threat relation is identical and this
            // level's difference is EMPTY - known, not merely absent. Saying so
            // is what lets the chain walk cross a null without falling back to
            // a rebuild, and null-move pruning is attempted at most interior
            // nodes.
            //
            // Nothing is copied here any more. The walk copies from the nearest
            // computed ancestor and applies an empty difference, which is the
            // same answer for levels somebody evaluates and free for the rest.
            _deltaRemovedCount[_top] = 0;
            _deltaAddedCount[_top] = 0;
            _deltaKnown[_top] = true;
        }
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

        ref Pending pd = ref _pending[_top];

        // NOTHING IS APPLIED HERE ANY MORE. What this computes is the
        // DIFFERENCE, which needs both boards and therefore cannot be deferred;
        // applying it needs neither, and that is the part that costs.
        //
        // WHY THAT IS THE WHOLE POINT. A threat net used to bypass the lazy
        // machinery completely: every node copied both perspectives and applied
        // every row, whether or not anybody ever evaluated that node. A
        // HalfKA-only net defers the same work and ends up applying 54.4% of it.
        // The rows are the expensive half - measured at 8.0 delta rows per node
        // at about 109 ns each once they are random rows in a 14.8 MB table,
        // some 16% of search time - so paying them for nodes nobody evaluates
        // was the single biggest thing left.
        //
        // Stored as PAIRS, not indices: the difference is a fact about the
        // board, so one copy serves both perspectives and the numbering happens
        // when it is applied. See ThreatFeatureIndex.Pack.
        int slot = _top * 2 * ThreatFeatureIndex.MaxActiveFeatures;
        _deltaRemovedCount[_top] = 0;
        _deltaAddedCount[_top] = 0;
        _deltaKnown[_top] = true;

        if (pd.IsNull)
            return;

        ulong affectedAfter = ThreatDelta.AffectedAttackers(
            board, _changed.AsSpan(_top * ThreatDelta.MaxChangedSquares, _changedCount[_top]));

        Span<int> after = _afterScratch;
        int afterCount = ThreatDelta.CollectPairs(board, affectedAfter, after);
        ReadOnlySpan<int> before = _beforeThreats.AsSpan(
            _top * ThreatFeatureIndex.MaxActiveFeatures, _beforeCount[_top]);

        // Only the DIFFERENCE is kept. Subtracting every "before" row and adding
        // every "after" row would be correct and would cost more than the full
        // refresh this replaces, which is the entire point.
        //
        // A LINEAR SCAN, AND THAT IS MEASURED RATHER THAN ASSUMED. Rewritten
        // once as a sorted merge on the reasoning that comparing every element
        // of one list against all of the other is n*n. The profile refused it
        // twice:
        //
        //     linear scan                 19.87% of search self time
        //     sorted merge + IntroSort    21.61%  (5.47% of it sorting)
        //     sorted merge + insertion    22.51%
        //
        // n is small enough that a tight scan with no setup beats anything with
        // a preamble, and 83% of relations SURVIVE a move, so Contains almost
        // always exits early on a hit. The sort pays for every element every
        // time.
        Span<int> removed = _deltaPairs.AsSpan(slot, ThreatFeatureIndex.MaxActiveFeatures);
        Span<int> added = _deltaPairs.AsSpan(slot + ThreatFeatureIndex.MaxActiveFeatures,
                                             ThreatFeatureIndex.MaxActiveFeatures);
        int removedCount = 0;
        int addedCount = 0;

        for (int i = 0; i < before.Length; i++)
        {
            if (!Contains(after[..afterCount], before[i]))
                removed[removedCount++] = before[i];
        }
        for (int i = 0; i < afterCount; i++)
        {
            if (!Contains(before, after[i]))
                added[addedCount++] = after[i];
        }

        _deltaRemovedCount[_top] = removedCount;
        _deltaAddedCount[_top] = addedCount;
    }

    // Applies one level's stored threat difference to one perspective. Called
    // from the chain walk, so it runs only for levels somebody actually needs.
    private void ApplyThreatDelta(NnueAccumulator target, Color perspective,
                                  int kingSquare, int level)
    {
        int slot = level * 2 * ThreatFeatureIndex.MaxActiveFeatures;
        ReadOnlySpan<int> removed = _deltaPairs.AsSpan(slot, _deltaRemovedCount[level]);
        ReadOnlySpan<int> added = _deltaPairs.AsSpan(
            slot + ThreatFeatureIndex.MaxActiveFeatures, _deltaAddedCount[level]);

        for (int i = 0; i < removed.Length; i++)
        {
            int index = ThreatFeatureIndex.IndexOfPacked(perspective, kingSquare, removed[i]);
            if (index >= 0)
                target.SubtractThreat(_network, perspective, index);
        }
        for (int i = 0; i < added.Length; i++)
        {
            int index = ThreatFeatureIndex.IndexOfPacked(perspective, kingSquare, added[i]);
            if (index >= 0)
                target.AddThreat(_network, perspective, index);
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
    // The psqt half of the evaluation, read AFTER GetPerspective has
    // materialised both perspectives of the top level (the lane rides every
    // materialisation path, so by then it is current). Halved exactly as the
    // reference does: each perspective counts the position from its own side,
    // so the difference counts everything twice.
    public int PsqtDiff(Color sideToMove, int bucket)
    {
        int[] psqt = _stack[_top].Psqt;
        int stm = (int)sideToMove * NnueAccumulator.MaxPsqtBuckets + bucket;
        int opp = (1 - (int)sideToMove) * NnueAccumulator.MaxPsqtBuckets + bucket;
        return (psqt[stm] - psqt[opp]) / 2;
    }

    public short[] GetPerspective(Board board, Color perspective)
    {
        int p = (int)perspective;
        NnueAccumulator acc = _stack[_top];

        // THREATS NO LONGER BYPASS THIS PATH, and the reason the old comment
        // gave for bypassing it was sound but incomplete.
        //
        // It said, correctly, that HalfKA deltas cannot carry threat features: a
        // move changes what the moved piece attacks, what now attacks it, and
        // every DISCOVERED relation where a slider's ray opened or closed, none
        // of which appears in a "piece left here, piece arrived there" record.
        // Replaying HalfKA deltas over a threat accumulator would freeze its
        // threat half and evaluate a position that does not exist.
        //
        // What it missed is that the threat DIFFERENCE does not have to be
        // replayed - it can be COMPUTED at the node, where both boards exist,
        // and applied later. CompleteThreatDelta now stores it per level and the
        // walk below applies it alongside the HalfKA pending, so a threat net
        // gets the same laziness as any other: levels nobody evaluates cost
        // nothing.

        // A king move of this perspective invalidated it: rebuild from the
        // finny table. Everything pending below is subsumed by the rebuild.
        if (!acc.Valid[p])
        {
            RefreshPerspective(acc, board, perspective);
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
            RefreshPerspective(acc, board, perspective);
            acc.Computed[p] = true;
            return acc.Values[p];
        }

        // A THREAT NET CAN ONLY CROSS LEVELS WHOSE DIFFERENCE IS KNOWN. Every
        // level on the current path normally has one - PushNull records an
        // empty difference and every legal move reaches CompleteThreatDelta -
        // but a level whose move turned out illegal is popped before that call,
        // so this is checked rather than assumed. Rebuilding the top from the
        // board is always a legal answer and is what the old code did at every
        // one of these levels.
        if (_network.UsesThreats)
        {
            for (int i = src + 1; i <= _top; i++)
            {
                if (_deltaKnown[i])
                    continue;
                RefreshPerspective(acc, board, perspective);
                acc.Computed[p] = true;
                return acc.Values[p];
            }
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
            if (_network.UsesThreats)
                ApplyThreatDelta(level, perspective, kingSquare, i);
            level.Computed[p] = true;
        }

        return acc.Values[p];
    }

    // The finny table knows HalfKA and NOTHING about threat features, so a
    // threat net rebuilding through it would get back an accumulator missing
    // half its input - silently, since every value would look plausible. The
    // accumulator's own refresh is the one that sums both transformers.
    private void RefreshPerspective(NnueAccumulator target, Board board, Color perspective)
    {
        if (_network.UsesThreats)
            target.Refresh(_network, board, perspective);
        else
            _cache.Refresh(target, board, perspective);
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
