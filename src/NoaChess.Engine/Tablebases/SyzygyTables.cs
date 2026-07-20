using NoaChess.Core;

namespace NoaChess.Engine.Tablebases;

// Syzygy endgame tablebases: exact game-theoretic results for positions with
// few pieces. WDL answers "won, drawn or lost", DTZ answers "how many plies to
// the next irreversible move" and is what lets a won ending actually be
// converted under the fifty-move rule.
//
// This is a native C# port of the reference prober rather than a P/Invoke to
// the usual C library, for one hard reason: this engine ships as a SINGLE
// self-contained .exe, and a native DLL would have to travel beside it or be
// unpacked at runtime. A managed port keeps that property. It also removes the
// build-time dependency on a C toolchain, which this machine does not have.
//
// The file format is unforgiving: every index is computed with exact integer
// arithmetic over piece placements, and a wrong index does not crash — it
// returns a WRONG result that looks perfectly valid, which the search then
// trusts absolutely. That is strictly worse than having no tablebases at all.
// The port is therefore differential-tested against an independent prober over
// randomly generated positions (see the SyzygyOracle tests).
//
// Layout of this file: the precomputed index tables and the per-file readers.
// The encoding and the probe entry points live in Syzygy.cs.
internal static class SyzygyTables
{
    public const int TbPieces = 7;   // Max men supported by the format
    public const int TbMaxSymbols = 4096;

    // Magic headers. A mirror that answers an HTTP error with an HTML body
    // produces files that pass a size check and fail here, which is exactly
    // the failure mode this guards against.
    public const uint WdlMagic = 0x5D23E871;
    public const uint DtzMagic = 0xA50C66D7;

    // ---- Precomputed index tables (reference init()) ----

    // MapPawns[s] encodes squares a2-h7 to 0..47: the number of available
    // squares when the leading pawn is on 's'.
    public static readonly int[] MapPawns = new int[64];
    // Maps the b1-h1-h7 triangle to 0..27.
    public static readonly int[] MapB1H1H7 = new int[64];
    // Maps the a1-d1-d4 triangle to 0..9.
    public static readonly int[] MapA1D1D4 = new int[64];
    // King-pair encoding: [MapA1D1D4[wk]][bk] -> 0..461.
    public static readonly int[,] MapKK = new int[10, 64];
    // Binomial[k][n]: ways to choose k elements from n.
    public static readonly int[,] Binomial = new int[7, 64];
    // Leading-pawn group encoding.
    public static readonly int[,] LeadPawnIdx = new int[6, 64];
    public static readonly int[,] LeadPawnsSize = new int[6, 4];

    private static bool _initialised;
    private static readonly object InitLock = new();

    public static void EnsureInitialised()
    {
        if (_initialised)
            return;
        lock (InitLock)
        {
            if (_initialised)
                return;
            BuildTables();
            _initialised = true;
        }
    }

    private static int RankOf(int sq) => sq >> 3;
    private static int FileOf(int sq) => sq & 7;
    private static int MakeSquare(int file, int rank) => rank * 8 + file;
    private static int FlipFile(int sq) => sq ^ 7;
    // Distance from the a1-h8 diagonal: 0 means the square is ON it.
    public static int OffA1H8(int sq) => RankOf(sq) - FileOf(sq);

    private static void BuildTables()
    {
        // MapB1H1H7[]: squares strictly BELOW the a1-h8 diagonal, numbered
        // 0..27 in plain square order. The numbering IS the file format — it
        // is not an implementation detail that can be reorganised.
        int code = 0;
        for (int sq = 0; sq < 64; sq++)
            if (OffA1H8(sq) < 0)
                MapB1H1H7[sq] = code++;

        // MapA1D1D4[]: the a1-d1-d4 triangle, 0..9. Squares below the diagonal
        // come first, squares ON it are numbered last (so b1 gets 0, which is
        // what the king-pair loop below relies on).
        var diagonal = new List<int>();
        code = 0;
        for (int sq = 0; sq <= 27; sq++)            // SQ_A1..SQ_D4
        {
            if (FileOf(sq) > 3)
                continue;
            if (OffA1H8(sq) < 0)
                MapA1D1D4[sq] = code++;
            else if (OffA1H8(sq) == 0)
                diagonal.Add(sq);
        }
        foreach (int sq in diagonal)
            MapA1D1D4[sq] = code++;

        // King pairs. Positions with the kings adjacent are illegal; positions
        // with the first king ON the a1-h8 diagonal require the second not to
        // be above it (otherwise it is a mirror of one already counted).
        // Pairs with BOTH kings on the diagonal are numbered last, exactly as
        // the reference does — the ordering is part of the file format.
        var bothOnDiagonal = new List<(int idx, int sq)>();
        code = 0;
        for (int idx = 0; idx < 10; idx++)
            for (int s1 = 0; s1 <= 27; s1++)      // SQ_A1..SQ_D4
            {
                if (MapA1D1D4[s1] != idx || (idx == 0 && s1 != 1))  // b1 maps to 0
                    continue;
                for (int s2 = 0; s2 < 64; s2++)
                {
                    if ((KingAttacks(s1) & (1UL << s2)) != 0 || s1 == s2)
                        continue;                                   // Kings adjacent
                    if (OffA1H8(s1) == 0 && OffA1H8(s2) > 0)
                        continue;                                   // Mirror duplicate
                    if (OffA1H8(s1) == 0 && OffA1H8(s2) == 0)
                        bothOnDiagonal.Add((idx, s2));
                    else
                        MapKK[idx, s2] = code++;
                }
            }
        foreach (var (idx, sq) in bothOnDiagonal)
            MapKK[idx, sq] = code++;

        // Binomial coefficients by Pascal's rule.
        Binomial[0, 0] = 1;
        for (int n = 1; n < 64; n++)
            for (int k = 0; k < 6 && k <= n; k++)
                Binomial[k, n] = (k > 0 ? Binomial[k - 1, n - 1] : 0)
                               + (k < n ? Binomial[k, n - 1] : 0);

        // Leading-pawn tables. MapPawns is filled on the first pass: with the
        // lead pawn on a2 there are 47 squares left for another pawn, and each
        // rank step removes two by mirroring.
        int availableSquares = 47;
        for (int leadPawnsCnt = 1; leadPawnsCnt <= 5; leadPawnsCnt++)
            for (int file = 0; file <= 3; file++)   // FILE_A..FILE_D only
            {
                int idx = 0;
                for (int rank = 1; rank <= 6; rank++)   // RANK_2..RANK_7
                {
                    int sq = MakeSquare(file, rank);
                    if (leadPawnsCnt == 1)
                    {
                        MapPawns[sq] = availableSquares--;
                        MapPawns[FlipFile(sq)] = availableSquares--;
                    }
                    LeadPawnIdx[leadPawnsCnt, sq] = idx;
                    idx += Binomial[leadPawnsCnt - 1, MapPawns[sq]];
                }
                LeadPawnsSize[leadPawnsCnt, file] = idx;
            }
    }

    // Local king-attack mask: this table is built before the engine's own
    // attack tables are guaranteed to be initialised, so it stays self-contained.
    private static ulong KingAttacks(int sq)
    {
        ulong b = 1UL << sq;
        ulong notA = 0xFEFEFEFEFEFEFEFEUL;
        ulong notH = 0x7F7F7F7F7F7F7F7FUL;
        return ((b & notH) << 1) | ((b & notA) >> 1)
             | (b << 8) | (b >> 8)
             | ((b & notH) << 9) | ((b & notA) << 7)
             | ((b & notH) >> 7) | ((b & notA) >> 9);
    }

    // ---- Material keys ----
    // A table is identified by its material content. The key must be
    // independent of square placement and must distinguish "white has the
    // rook" from "black has the rook", so counts are folded per colour.
    public static ulong MaterialKey(Board board)
    {
        ulong key = 0;
        for (int c = 0; c < 2; c++)
            for (int pt = 0; pt < 6; pt++)
            {
                int n = System.Numerics.BitOperations.PopCount(
                    board.Pieces((Color)c, (PieceType)pt));
                key = key * 8 + (ulong)n;
            }
        return key;
    }

    // Material key of a piece-count array laid out as [colour][pieceType].
    public static ulong MaterialKey(ReadOnlySpan<int> counts)
    {
        ulong key = 0;
        for (int i = 0; i < 12; i++)
            key = key * 8 + (ulong)counts[i];
        return key;
    }
}
