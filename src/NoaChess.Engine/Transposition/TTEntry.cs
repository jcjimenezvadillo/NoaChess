using NoaChess.Core;

namespace NoaChess.Engine.Transposition;

// One slot of the transposition table. Kept as small as possible: the table
// has millions of entries and cache locality dominates its performance.
public struct TTEntry
{
    // Full Zobrist key of the stored position. The table index only uses the
    // low bits of the key, so the full key is needed to detect "index
    // collisions" (two different positions mapping to the same slot).
    public ulong Key;

    // Best move found when this position was searched. Even when the stored
    // score cannot be reused (insufficient depth), this move is gold for move
    // ordering: it is tried first and usually causes an immediate cutoff.
    public Move BestMove;

    // Depth of the search that produced this entry. A stored score can only
    // replace a search of equal or smaller depth.
    public short Depth;

    // How to interpret Score (exact value or a bound). See BoundType.
    public BoundType Bound;

    // Score of the position (with mate scores normalized to the node, see
    // the to/from-TT conversion in the search).
    public int Score;
}
