using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Second-degree polynomial material imbalance (Tord Romstad's formula, ported
// from the reference material tables). Instead of valuing each piece in
// isolation, it scores every PAIR of pieces: synergies between our own pieces
// (QuadraticOurs — e.g. knights gain value with more of our pawns on the
// board, a second rook is worth less than the first) and interactions with
// the enemy's material (QuadraticTheirs — e.g. our queen is strong against
// rooks, our knight fights well against many enemy pawns).
//
// The bishop pair is modelled as an "extended piece" at index 0 with count
// 0/1. Its DIAGONAL entry (the pair's own value) is zeroed here: NoaChess
// keeps the standalone texel-tuned BishopPair term (removing it cost −30 Elo
// in the first 4H attempt), so the imbalance owns only the pair's
// interactions with the rest of the material.
//
// Why this term failed twice before and what changed (v2.6.8): the tables
// were tuned inside the reference's eval ecosystem, while NoaChess's material
// baseline (MaterialMg/Eg + the texel-tuned PSTs) had already absorbed the
// AVERAGE synergies into itself, so adding the polynomial on top double-
// counted them. The fix is the documented rescue path: MaterialMg/Eg and
// BishopPair are texel-retuned WITH this term active, so the tuner moves the
// absorbed average back out of the piece values and the polynomial keeps
// only the context-dependent deviation.
//
// Tables are in raw reference internal units. The reference divides the White
// minus Black difference by 16 and NoaChess rescales every reference value by
// 100/208 ≈ 0.48, so the combined factor applied at the end is
// 1/16 × 0.48 = 3/100. No re-centering of the tables themselves is needed:
// the term is a pure White-minus-Black difference, exactly zero for symmetric
// material, and the average bias is handled by the joint retune above.
//
// Cost control: the polynomial is ~70 table accesses but its inputs are just
// the ten piece counts, which only change on captures and promotions. The
// reference caches the result in a material hash table; here a direct-mapped
// cache keyed by the packed counts (same pattern as the shelter cache)
// achieves the same reuse — the full polynomial only runs on a miss.
public sealed class MaterialImbalance
{
    // Piece indices inside the count arrays and tables:
    // 0 = bishop pair (0/1), 1 = pawn, 2 = knight, 3 = bishop, 4 = rook,
    // 5 = queen. Lower-triangular: entry [pt1][pt2] is only read for
    // pt2 <= pt1 (Ours) / pt2 < pt1 (Theirs).
    // [0][0] is zeroed (reference 1419/1455): the pair's own value lives in
    // the standalone texel-tuned BishopPair term, see the class comment.
    private static readonly int[,] QuadraticOursMg = new int[6, 6]
    {
        {    0,    0,    0,    0,    0,   0 },
        {  101,   37,    0,    0,    0,   0 },
        {   57,  249,  -49,    0,    0,   0 },
        {    0,  118,   10,    0,    0,   0 },
        {  -63,   -5,  100,  132, -246,   0 },
        { -210,   37,  147,  161, -158,  -9 },
    };

    private static readonly int[,] QuadraticOursEg = new int[6, 6]
    {
        {    0,    0,    0,    0,    0,   0 },
        {   28,   39,    0,    0,    0,   0 },
        {   64,  187,  -62,    0,    0,   0 },
        {    0,  137,   27,    0,    0,   0 },
        {  -68,    3,   81,  118, -244,   0 },
        { -211,   14,  141,  105, -174, -31 },
    };

    private static readonly int[,] QuadraticTheirsMg = new int[6, 6]
    {
        {    0,    0,    0,    0,    0,   0 },
        {   33,    0,    0,    0,    0,   0 },
        {   46,  106,    0,    0,    0,   0 },
        {   75,   59,   60,    0,    0,   0 },
        {   26,    6,   38,  -12,    0,   0 },
        {   97,  100,  -58,  112,  276,   0 },
    };

    private static readonly int[,] QuadraticTheirsEg = new int[6, 6]
    {
        {    0,    0,    0,    0,    0,   0 },
        {   30,    0,    0,    0,    0,   0 },
        {   18,   84,    0,    0,    0,   0 },
        {   35,   44,   15,    0,    0,   0 },
        {   35,   22,   39,   -2,    0,   0 },
        {   93,  163,  -91,  192,  225,   0 },
    };

    // Direct-mapped cache keyed by the packed piece counts, per evaluator
    // instance (ClassicalEvaluator is per-thread, like its shelter cache).
    // A zero key only occurs for bare kings, whose imbalance is genuinely
    // zero, so the zero-initialized table is sentinel-safe.
    private readonly (ulong Key, int Mg, int Eg)[] _cache = new (ulong, int, int)[8192];

    // White-minus-Black imbalance Score in centipawns.
    public Score Compute(Board board)
    {
        Span<int> white = stackalloc int[6];
        Span<int> black = stackalloc int[6];
        FillCounts(board, Color.White, white);
        FillCounts(board, Color.Black, black);

        ulong key = PackKey(white, black);
        int slot = (int)((key * 0x9E3779B97F4A7C15UL) >> 51);
        if (_cache[slot].Key == key)
            return new Score(_cache[slot].Mg, _cache[slot].Eg);

        Score s = ComputeFromCounts(white, black);
        _cache[slot] = (key, s.Mg, s.Eg);
        return s;
    }

    // The uncached polynomial on explicit count vectors (index layout above).
    // Public so tooling (the tuner's marginal statistics) and tests can probe
    // hypothetical material configurations directly.
    public static Score ComputeFromCounts(Span<int> white, Span<int> black)
    {
        int mg = 0, eg = 0;
        Accumulate(white, black, 1, ref mg, ref eg);
        Accumulate(black, white, -1, ref mg, ref eg);
        return new Score(mg * 3 / 100, eg * 3 / 100);
    }

    private static ulong PackKey(Span<int> white, Span<int> black)
    {
        ulong key = 0;
        for (int i = 1; i < 6; i++)
            key = (key << 4) | (uint)Math.Min(white[i], 15);
        for (int i = 1; i < 6; i++)
            key = (key << 4) | (uint)Math.Min(black[i], 15);
        return key;
    }

    public static void FillCounts(Board board, Color color, Span<int> counts)
    {
        int bishops = Bitboard.PopCount(board.Pieces(color, PieceType.Bishop));
        counts[0] = bishops > 1 ? 1 : 0;
        counts[1] = Bitboard.PopCount(board.Pieces(color, PieceType.Pawn));
        counts[2] = Bitboard.PopCount(board.Pieces(color, PieceType.Knight));
        counts[3] = bishops;
        counts[4] = Bitboard.PopCount(board.Pieces(color, PieceType.Rook));
        counts[5] = Bitboard.PopCount(board.Pieces(color, PieceType.Queen));
    }

    // One side's polynomial: for each piece type we own, its self term plus
    // the interaction with every lower-indexed friendly and enemy type, all
    // multiplied by how many of the piece we have.
    private static void Accumulate(
        Span<int> us, Span<int> them, int sign, ref int mg, ref int eg)
    {
        for (int pt1 = 0; pt1 < 6; pt1++)
        {
            int count1 = us[pt1];
            if (count1 == 0)
                continue;

            int vMg = QuadraticOursMg[pt1, pt1] * count1;
            int vEg = QuadraticOursEg[pt1, pt1] * count1;

            for (int pt2 = 0; pt2 < pt1; pt2++)
            {
                vMg += QuadraticOursMg[pt1, pt2] * us[pt2]
                     + QuadraticTheirsMg[pt1, pt2] * them[pt2];
                vEg += QuadraticOursEg[pt1, pt2] * us[pt2]
                     + QuadraticTheirsEg[pt1, pt2] * them[pt2];
            }

            mg += sign * count1 * vMg;
            eg += sign * count1 * vEg;
        }
    }
}
