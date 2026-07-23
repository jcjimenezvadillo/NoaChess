using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Killer moves heuristic. A "killer" is a QUIET move that caused a beta cutoff
// at a given ply. Sibling nodes at the same ply usually face very similar
// positions, so a move that refuted one branch very often refutes the others:
// trying killers early (right after captures) produces many cheap cutoffs.
//
// Two killers are remembered per ply; a new one shifts the old first killer to
// the second slot, so the most recent refutation is always tried first.
public sealed class KillerTable(int maxPly)
{
    private readonly Move[,] _killers = new Move[maxPly, 2];

    public void Clear() => Array.Clear(_killers);

    // Records a quiet move that produced a beta cutoff at 'ply'.
    public void Store(int ply, Move move)
    {
        if (ply >= _killers.GetLength(0) || _killers[ply, 0] == move)
            return;
        _killers[ply, 1] = _killers[ply, 0];
        _killers[ply, 0] = move;
    }

    // 2 = most recent killer, 1 = older killer, 0 = not a killer.
    // Used by the move picker to rank killers between captures and history.
    public int Rank(int ply, Move move)
    {
        if (ply >= _killers.GetLength(0))
            return 0;
        if (_killers[ply, 0] == move) return 2;
        if (_killers[ply, 1] == move) return 1;
        return 0;
    }
}
