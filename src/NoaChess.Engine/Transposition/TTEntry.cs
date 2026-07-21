using NoaChess.Core;

namespace NoaChess.Engine.Transposition;

// One slot of the transposition table, packed to exactly 16 bytes so a
// 64-byte cache line holds a full 4-entry cluster (the reference packs 3
// per 32 bytes by shrinking scores to 16 bits; our mate scores live at
// +-100000 so both score fields stay int32 and the cluster is 4-wide
// instead).
public struct TTEntry
{
    // Upper 32 bits of the Zobrist key. The cluster index consumes the LOW
    // bits, so the high half is what discriminates positions that share a
    // cluster. A 2^-32 per-probe false match is acceptable because every TT
    // move is pseudo-legality-vetted before use.
    public uint Key32;

    // Score of the position (mate scores normalized to the node; see the
    // to/from-TT conversion in the search). Only meaningful when Bound is
    // not None.
    public int Score;

    // Static evaluation of the position, cached so a TT hit skips the
    // evaluator entirely (the reference's eval16). NoStaticEval when the
    // entry was stored from an in-check node (no eval exists there).
    public int StaticEval;

    // Best move found when this position was searched. Even when the stored
    // score cannot be reused (insufficient depth), this move is gold for move
    // ordering: it is tried first and usually causes an immediate cutoff.
    public Move BestMove;

    // Depth of the search that produced this entry (0 for eval-only entries).
    public byte Depth;

    // Packed bookkeeping: bits 0-1 bound, bit 2 ttPv ("this position is or
    // was on the principal variation"), bits 3-7 generation. Generation zero
    // is reserved for an empty slot, leaving a 31-step ageing cycle. This is
    // what makes even a non-PV eval-only entry (bound None) unambiguously
    // occupied without spending another byte in the 16-byte entry.
    public byte GenBound;

    public const int NoStaticEval = int.MinValue / 2;

    public readonly BoundType Bound => (BoundType)(GenBound & 0b11);
    public readonly bool IsPv => (GenBound & 0b100) != 0;
    public readonly int Generation => GenBound >> 3;

    public static byte PackGenBound(int generation, bool isPv, BoundType bound)
        => (byte)((generation << 3) | (isPv ? 0b100 : 0) | (int)bound);
}
