using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// History heuristic. For every (color, from, to) it accumulates how often that
// quiet move caused beta cutoffs, weighted by depth (a cutoff near the root is
// worth far more search effort than one at the tips, hence the depth*depth
// bonus). The scores are used to sort quiet moves: moves that keep refuting
// lines elsewhere in the tree are probably good here too.
//
// Unlike killers (per-ply, very local) history is global to the whole search,
// which makes it a good tiebreaker for the long tail of quiet moves.
public sealed class HistoryTable
{
    // Gravity bound. Updates pull each entry toward this rail in proportion to
    // how far it already is, which keeps the table symmetric: the previous rule
    // grew on the positive side until a GLOBAL halving rescale fired, but
    // clamped individually on the negative side. Measured, that left the table
    // heavily right-skewed — median -8 against a mean of +71.8, only 25% of
    // entries positive, and a tail reaching 6086 — so any consumer that reads
    // the raw value saw a few moves dominate everything.
    //
    // The bound has to BE the operating range for gravity to do anything. At
    // the old 2^20 the decay term was score*|bonus|/MaxScore = 6086*169/2^20,
    // which integer-truncates to zero: the rule looked like gravity and was
    // numerically inert. The reference sizes its butterfly bound at 7183, just
    // above where the values actually live, so every update pulls the entry
    // back in proportion to how far out it already is.
    private const int MaxScore = 7183;

    private readonly int[,,] _scores = new int[2, 64, 64];

    public void Clear() => Array.Clear(_scores);

    // Halves every score. Called between searches so that history from
    // previous moves still helps ordering but decays over time.
    public void Decay()
    {
        for (int c = 0; c < 2; c++)
            for (int f = 0; f < 64; f++)
                for (int t = 0; t < 64; t++)
                    _scores[c, f, t] >>= 1;
    }

    // Rewards a quiet move that caused a beta cutoff at the given depth.
    public void AddBonus(Color color, Move move, int depth)
        => Update(color, move, depth * depth);

    // Punishes a quiet move that was searched before the cutoff move at this
    // node and did NOT refute: it sinks in the ordering next time.
    public void AddMalus(Color color, Move move, int depth)
        => Update(color, move, -depth * depth);

    private void Update(Color color, Move move, int bonus)
    {
        bonus = Math.Clamp(bonus, -MaxScore, MaxScore);
        ref int score = ref _scores[(int)color, move.From, move.To];
        score += bonus - (int)((long)score * Math.Abs(bonus) / MaxScore);
    }

    public int Get(Color color, Move move) => _scores[(int)color, move.From, move.To];
}
