using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Continuation history: like the classic history heuristic, but conditioned on
// the PREVIOUS move. Indexed by (previous mover piece, previous destination,
// current mover piece, current destination), it learns "after the opponent
// plays THIS, THAT reply keeps refuting" - a much sharper signal than the
// global (from, to) butterfly history, because the best reply to h7-h5 is
// rarely the best reply to d7-d5 even from the same square.
//
// Pieces are indexed 0-11 (color * 6 + type) so the table distinguishes a
// white knight from a black one. The full table is 12*64*12*64 ints (~2.3 MB),
// small enough to keep hot in cache for the entries that actually occur.
public sealed class ContinuationHistory
{
    // Gravity bound, and it has to BE the operating range to do anything. At the
    // previous 2^20 the decay term was score*|bonus|/MaxScore = 7289*169/2^20,
    // which integer-truncates to zero on every realistic update: the rule looked
    // like gravity and was inert, which is why v2.8.2 credited it with part of an
    // Elo gain it could not have produced. Sized here just above the measured
    // ceiling of this table (p99 642, max 7289) so entries settle symmetrically
    // instead of drifting, exactly as the butterfly table was fixed in v2.8.3.
    //
    // Deliberately NOT the reference's 30000. That constant sits four times above
    // where our values live, so adopting it would activate gravity AND quadruple
    // the scale this table contributes to move ordering and to the RFP guard -
    // two effects in one measurement. The v2.8.3 lesson applies: formula fidelity
    // is not semantic fidelity, so the bound is measured rather than copied.
    private const int MaxScore = 8192;

    // short, not int, and the reason is 5G rather than tidiness.
    //
    // Entries are bounded at +-MaxScore = 8192 by the gravity rule above, which
    // fits a short with room to spare, so the int was paying four bytes to store
    // fourteen bits. That was affordable while the search kept ONE of these
    // tables. Multi-level continuation history keeps SIX, and at 4 bytes that is
    // 14.2 MB of working set on the hottest lookup in move ordering; at 2 it is
    // 7.1 MB. This engine has measured before that layout beats algorithm here -
    // flattening the history and piece tables was worth +6.1% nps at identical
    // node counts - so halving the footprint of the biggest table is the first
    // thing to try against the cost 5G adds.
    //
    // Behaviour must not move. The values stored are the same integers as
    // before, so node counts have to come out byte-identical; if they do not,
    // the change is wrong rather than merely slow.
    private readonly short[] _scores = new short[12 * 64 * 12 * 64];

    public static int PieceIndex(Color color, PieceType type) => (int)color * 6 + (int)type;

    private static int Index(int prevPiece, int prevTo, int piece, int to)
        => ((prevPiece * 64 + prevTo) * 12 + piece) * 64 + to;

    public void Clear() => Array.Clear(_scores);

    public int Get(int prevPiece, int prevTo, int piece, int to)
        => _scores[Index(prevPiece, prevTo, piece, to)];

    // Rewards the quiet reply that caused a beta cutoff after 'prev' was played.
    public void AddBonus(int prevPiece, int prevTo, int piece, int to, int depth)
        => Update(prevPiece, prevTo, piece, to, depth * depth);

    // Punishes quiet replies that were searched before the cutoff move and
    // failed to produce it - they sink in the ordering next time.
    public void AddMalus(int prevPiece, int prevTo, int piece, int to, int depth)
        => Update(prevPiece, prevTo, piece, to, -depth * depth);

    // Weighted variant, for the multi-level update. The reference does not credit
    // every ply-distance equally: the move one ply back explains a reply far
    // better than the move six plies back, so its bonuses carry weights
    // {1040, 780, 290, 502, 132, 418} out of 1024. Kept as a separate entry
    // point so the single-level callers stay byte-identical.
    //
    // DAMPING. The reference feeds the continuation update `bonus * 750 / 1024`
    // where the butterfly table gets the bonus whole, so continuation history
    // moves about three quarters as fast. With gravity that does not change
    // where an entry SATURATES - the equilibrium is the bound either way - it
    // changes how heavily the table weights what happened recently. Without
    // this the port ran roughly 1.75x faster than the reference on the one
    // mechanism it is being re-run for being unfaithful.
    //
    // Not modelled: the reference also multiplies by a consistency factor of
    // 94-126 depending on how many nearer distances already hold a positive
    // entry. That needs state this engine does not keep, so it is left out
    // rather than guessed, and it is the remaining known gap.
    private const int ContinuationDamping = 750;

    // ONE division, not two, and in 64-bit.
    //
    // Dividing by 1024 for the damping and again by 1024 for the weight
    // truncates twice, and at shallow depths the second truncation took the far
    // distances to exactly zero: at depth 2 and 3 the tables for four, five and
    // six plies back learned NOTHING. That is the same failure as the gravity
    // term of v2.8.2, which looked implemented, evaluated to zero on every
    // realistic update, and was credited with Elo it could not have produced.
    //
    // The 64-bit cast is not decoration either: depth can reach the ply cap, so
    // depth*depth*750*1040 overflows a signed 32-bit int well before it.
    private static int Scale(int depth, int weight)
        => (int)((long)depth * depth * ContinuationDamping * weight / (1024L * 1024L));

    public void AddWeighted(int prevPiece, int prevTo, int piece, int to, int depth, int weight)
        => Update(prevPiece, prevTo, piece, to, Scale(depth, weight));

    public void AddWeightedMalus(int prevPiece, int prevTo, int piece, int to, int depth, int weight)
        => Update(prevPiece, prevTo, piece, to, -Scale(depth, weight));

    private void Update(int prevPiece, int prevTo, int piece, int to, int bonus)
    {
        bonus = Math.Clamp(bonus, -MaxScore, MaxScore);
        ref short score = ref _scores[Index(prevPiece, prevTo, piece, to)];

        // Computed in int and stored in short. The arithmetic is identical to
        // the int version - same operands, same order, same truncation - so the
        // stored value is the same integer it always was.
        //
        // The clamp is a belt on top of braces. The gravity rule keeps entries
        // inside +-MaxScore by construction (at score = MaxScore the decay term
        // exactly cancels the bonus), but "by construction" is what was said
        // about the v2.8.2 gravity that turned out to be inert, so the invariant
        // is enforced rather than trusted. Reaching it would mean the rule is
        // broken, and silently wrapping a short is the worst way to find out.
        int updated = score + bonus - (int)((long)score * Math.Abs(bonus) / MaxScore);
        score = (short)Math.Clamp(updated, -MaxScore, MaxScore);
    }
}
