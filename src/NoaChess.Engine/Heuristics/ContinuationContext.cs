namespace NoaChess.Engine.Heuristics;

// The continuation-history lookup a quiet move is scored against: the table
// keyed on the PREVIOUS move, paired with that move's (piece, destination).
//
// A level is inactive when its table is null - at the root, after a null move
// (the search writes -1 into the piece stack), or when the search is not yet
// deep enough for that distance to exist. Inactive contributes nothing rather
// than reading a stale key.
//
// Shaped as a multi-level container on purpose even though only one distance
// is wired up. A second table keyed on the move TWO plies back was built and
// measured on 2026-08-07 (see the ordering campaign): it cost 0.9% NPS and
// bought nothing, so it was removed - but the read side summing several
// independent distances is still where this has to go, and keeping the shape
// means the retry is a one-line change rather than another signature refactor.
// Whoever retries it: the tables must stay INDEPENDENT per distance (a shared
// one cost -26 Elo in 5G) and the read side sums them with equal weight.
//
// This is a struct so building one per node costs no allocation; it is passed
// by 'in' on the hot path so it is never copied per move either.
public readonly struct ContinuationContext(
    ContinuationHistory? level0, int piece0, int to0)
{
    private readonly ContinuationHistory? _level0 = level0;
    private readonly int _piece0 = piece0, _to0 = to0;

    // True when at least one distance has a usable previous move.
    public bool IsActive => _level0 is not null;

    // Equal-weight sum over the active distances.
    public int Sum(int piece, int to)
    {
        int value = 0;
        if (_level0 is not null)
            value += _level0.Get(_piece0, _to0, piece, to);
        return value;
    }
}
