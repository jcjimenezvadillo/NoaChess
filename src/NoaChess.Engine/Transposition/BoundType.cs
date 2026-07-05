namespace NoaChess.Engine.Transposition;

// What a transposition-table score means. Alpha-Beta rarely computes the exact
// value of a node: when a branch is pruned we only learn a BOUND on its value.
// Storing which kind of bound we learned lets a later probe reuse it safely.
public enum BoundType : byte
{
    // The score is the exact value of the node (searched with a full window).
    Exact = 0,

    // The node failed high (score >= beta): its real value is AT LEAST this.
    LowerBound = 1,

    // The node failed low (score <= alpha): its real value is AT MOST this.
    UpperBound = 2
}
