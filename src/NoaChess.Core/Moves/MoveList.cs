namespace NoaChess.Core;

// Reusable, fixed-capacity move container for hot paths.
//
// The search visits millions of nodes and each one generates moves; putting
// them in a freshly allocated List<Move> per node floods the garbage
// collector. A MoveList is allocated ONCE per search ply and reused: Clear()
// just resets the count, no memory is touched.
//
// 256 slots is the traditional safe capacity (the most legal moves ever found
// in a reachable position is 218).
//
// The parallel Scores array belongs to move ordering: keeping scores next to
// the moves lets the picker sort in place without allocating per node.
public sealed class MoveList
{
    public const int Capacity = 256;

    // Exposed as a raw array for the same reason Scores is: the move picker's
    // sort loops shuffle both arrays in step, and reaching the moves through the
    // indexer costs a property call plus a bounds check the JIT cannot hoist out
    // of an insertion sort's inner loop, while the scores next to them are read
    // straight off the array. Only the first Count entries are meaningful; the
    // slots beyond it hold whatever a previous node left there.
    public readonly Move[] Moves = new Move[Capacity];

    // Ordering scores, parallel to the moves (managed by the engine's picker).
    public readonly int[] Scores = new int[Capacity];

    public int Count { get; private set; }

    public Move this[int index]
    {
        get => Moves[index];
        set => Moves[index] = value;
    }

    public void Add(Move move) => Moves[Count++] = move;

    public void Clear() => Count = 0;

    // Shrinks the list to the first 'count' entries (used by the in-place
    // legality filter, which compacts legal moves to the front).
    public void Truncate(int count) => Count = count;

    // Swaps two entries (moves and scores together). Used by selection-style
    // ordering in the engine.
    public void Swap(int a, int b)
    {
        (Moves[a], Moves[b]) = (Moves[b], Moves[a]);
        (Scores[a], Scores[b]) = (Scores[b], Scores[a]);
    }

    public bool Contains(Move move)
    {
        for (int i = 0; i < Count; i++)
            if (Moves[i] == move)
                return true;
        return false;
    }
}
