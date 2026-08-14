namespace NoaChess.Engine.Heuristics;

// The continuation-history lookup a quiet move is scored against: the tables
// keyed on the PREVIOUS moves, paired with each of those moves' (piece,
// destination).
//
// A level is inactive when its table is null - at the root, after a null move
// (the search writes -1 into the piece stack), or when the search is not yet
// deep enough for that distance to exist. Inactive contributes nothing rather
// than reading a stale key.
//
// FIVE DISTANCES, AND THE GAPS ARE DELIBERATE. The reference WRITES six levels
// (1 to 6 plies back) with different weights, and READS five of them with equal
// weight in the quiet ordering - skipping distance 5, which it maintains and
// never consults there. Both facts are copied, not rationalised: the write side
// is weighted because a move one ply back explains a reply far better than one
// six plies back, and the read side is flat because the ordering only needs the
// sum of independent evidence.
//
// The tables must stay INDEPENDENT per distance. A single shared table cost
// -26 Elo when 5G first tried this, because every distance overwrites the same
// entry and the levels destroy each other.
//
// HISTORY OF THIS RETRY. 5G was buried on four builds (-33.9, -10.9, [0.496],
// -4.2) and the project's own ROADMAP records that the final zero was caused by
// the hard killer/counter bands, which returned killers ahead of any history and
// left up to three moves per node ordered by a constant. Those bands were
// removed in v4.4.0 and were worth +8.0 over 1125 games. 5G was never re-run
// afterwards. A two-level version WAS tried on 2026-08-07 and dropped for
// costing 0.9% nps and buying nothing, but two flat levels is not this: that
// attempt had neither the weighted write side nor the reference's set of
// distances.
//
// This is a struct so building one per node costs no allocation; it is passed
// by 'in' on the hot path so it is never copied per move either.
public readonly struct ContinuationContext(
    ContinuationHistory? level0, int piece0, int to0,
    ContinuationHistory? level1 = null, int piece1 = 0, int to1 = 0,
    ContinuationHistory? level2 = null, int piece2 = 0, int to2 = 0,
    ContinuationHistory? level3 = null, int piece3 = 0, int to3 = 0,
    ContinuationHistory? level5 = null, int piece5 = 0, int to5 = 0)
{
    private readonly ContinuationHistory? _level0 = level0;
    private readonly ContinuationHistory? _level1 = level1;
    private readonly ContinuationHistory? _level2 = level2;
    private readonly ContinuationHistory? _level3 = level3;
    private readonly ContinuationHistory? _level5 = level5;
    private readonly int _piece0 = piece0, _to0 = to0;
    private readonly int _piece1 = piece1, _to1 = to1;
    private readonly int _piece2 = piece2, _to2 = to2;
    private readonly int _piece3 = piece3, _to3 = to3;
    private readonly int _piece5 = piece5, _to5 = to5;

    // True when at least one distance has a usable previous move.
    public bool IsActive => _level0 is not null;

    // Equal-weight sum over the active distances.
    public int Sum(int piece, int to)
    {
        int value = 0;
        if (_level0 is not null)
            value += _level0.Get(_piece0, _to0, piece, to);
        if (_level1 is not null)
            value += _level1.Get(_piece1, _to1, piece, to);
        if (_level2 is not null)
            value += _level2.Get(_piece2, _to2, piece, to);
        if (_level3 is not null)
            value += _level3.Get(_piece3, _to3, piece, to);
        if (_level5 is not null)
            value += _level5.Get(_piece5, _to5, piece, to);
        return value;
    }
}
