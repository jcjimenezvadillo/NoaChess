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
    // Cap that triggers a global rescale, keeping scores inside int range and
    // letting new information overtake stale accumulations.
    private const int MaxScore = 1 << 20;

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
    {
        ref int score = ref _scores[(int)color, move.From, move.To];
        score += depth * depth;
        if (score > MaxScore)
            Decay(); // Rescale everything to keep relative ordering.
    }

    // Punishes a quiet move that was searched before the cutoff move at this
    // node and did NOT refute: it sinks in the ordering next time. The clamp
    // (instead of a rescale) keeps one heavily punished move from erasing the
    // accumulated knowledge about every other move.
    public void AddMalus(Color color, Move move, int depth)
    {
        ref int score = ref _scores[(int)color, move.From, move.To];
        score -= depth * depth;
        if (score < -MaxScore)
            score = -MaxScore;
    }

    public int Get(Color color, Move move) => _scores[(int)color, move.From, move.To];
}
