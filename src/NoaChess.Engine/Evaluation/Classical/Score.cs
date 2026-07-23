namespace NoaChess.Engine.Evaluation.Classical;

// A pair of scores: one for the middlegame (Mg) and one for the endgame (Eg),
// both in centipawns, from White's point of view. The final evaluation
// interpolates between the two according to how much material is left on the
// board ("tapered eval"): a knight on an outpost is worth more with queens on,
// a passed pawn is worth more once they are gone. Keeping both phases in one
// value lets every evaluation term carry its own middlegame/endgame nuance
// without the evaluator juggling two running totals by hand.
//
// It is a readonly struct with operators so the evaluator reads like plain
// arithmetic ("score += Pst(...)") while allocating nothing in the hot path.
public readonly struct Score(int mg, int eg)
{
    public readonly int Mg = mg;
    public readonly int Eg = eg;

    public static Score operator +(Score a, Score b) => new(a.Mg + b.Mg, a.Eg + b.Eg);
    public static Score operator -(Score a, Score b) => new(a.Mg - b.Mg, a.Eg - b.Eg);
    public static Score operator *(Score a, int k) => new(a.Mg * k, a.Eg * k);

    // Collapses the two phases into a single centipawn score. 'phase' runs from
    // 24 (all pieces on, pure middlegame) down to 0 (bare kings, pure endgame).
    public int Taper(int phase) => (Mg * phase + Eg * (24 - phase)) / 24;
}
