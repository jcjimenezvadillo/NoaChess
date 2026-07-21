using NoaChess.Core;

namespace NoaChess.Engine.Transposition;

// Transposition table: a big hash map from Zobrist key to search results.
//
// Chess move orders transpose constantly (1.d4 d5 2.c4 and 1.c4 d5 2.d4 reach
// the same position), so the same position is searched over and over. The TT
// caches "I already searched this position to depth D and got score S": when
// the position reappears the result is reused, either cutting the node
// immediately or at least providing a proven-good move to try first.
//
// Implementation notes:
// - Fixed-size array indexed by the low bits of the key (power-of-two size,
//   so the index is a cheap bit-mask). No chaining, no resizing.
// - On a slot conflict a depth-preferred scheme applies: an entry can only be
//   evicted by a different position or by an equal-or-deeper search of the
//   same one. Losing information this way is fine — the TT is a cache, never
//   the source of truth.
// - The full key is stored to detect index collisions; a probe only matches
//   when the complete key is equal.
public sealed class TranspositionTable
{
    // Assigned by Resize (called from the constructor); the initializer only
    // silences the compiler, which cannot see through the method call.
    private TTEntry[] _entries = [];
    private ulong _indexMask;

    public TranspositionTable(int sizeMb)
    {
        Resize(sizeMb);
    }

    // Allocates the table. The entry count is rounded down to a power of two
    // so "key % size" becomes "key & mask".
    public void Resize(int sizeMb)
    {
        const int approxEntrySize = 24; // bytes, including padding
        long target = (long)sizeMb * 1024 * 1024 / approxEntrySize;

        int count = 1;
        while ((long)count * 2 <= target)
            count *= 2;

        _entries = new TTEntry[count];
        _indexMask = (ulong)(count - 1);
    }

    // Wipes all entries (new game).
    public void Clear() => Array.Clear(_entries);

    // Looks up a position. Returns true (and the entry) only when the slot
    // holds exactly this position.
    public bool Probe(ulong key, out TTEntry entry)
    {
        entry = _entries[key & _indexMask];
        return entry.Key == key;
    }

    // Stores a search result using the depth-preferred replacement scheme.
    public void Store(ulong key, int depth, int score, BoundType bound, Move bestMove)
    {
        ref TTEntry slot = ref _entries[key & _indexMask];

        // Keep the deeper result when re-storing the same position.
        if (slot.Key == key && depth < slot.Depth)
            return;

        // Do not throw away a known best move when the new result has none
        // (e.g. an all-node where nothing improved alpha).
        if (bestMove == Move.None && slot.Key == key)
            bestMove = slot.BestMove;

        slot.Key = key;
        slot.Depth = (short)depth;
        slot.Score = score;
        slot.Bound = bound;
        slot.BestMove = bestMove;
    }
}
