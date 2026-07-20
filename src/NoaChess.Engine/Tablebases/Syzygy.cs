using NoaChess.Core;

namespace NoaChess.Engine.Tablebases;

// Probe result of a WDL lookup, from the point of view of the side to move.
public enum WdlScore
{
    Loss = -2,          // Loss
    BlessedLoss = -1,   // Loss, but drawn by the fifty-move rule
    Draw = 0,
    CursedWin = 1,      // Win, but drawn by the fifty-move rule
    Win = 2
}

internal enum ProbeState
{
    Fail = 0,
    Ok = 1,
    ChangeStm = -1,         // DTZ stores the other side's result: search one ply
    ZeroingBestMove = 2
}

// Syzygy tablebase probing. See SyzygyTables.cs for the rationale behind the
// managed port.
public static class Syzygy
{
    private static readonly Dictionary<ulong, SyzygyTable> WdlByKey = [];
    private static readonly Dictionary<ulong, SyzygyTable> DtzByKey = [];

    /// Largest number of men any loaded table covers. 0 means "no tablebases".
    public static int Cardinality { get; private set; }

    public static bool Available => Cardinality > 0;

    public static string CurrentPath { get; private set; } = "";

    // Counts probes that actually hit a table; used by the UCI "info" line.
    public static long Hits;

    // ---- Initialisation ----

    /// (Re)loads the tablebase index from a semicolon-separated path list.
    /// Safe to call repeatedly: a failed or empty path simply disables probing.
    public static void Init(string paths)
    {
        WdlByKey.Clear();
        DtzByKey.Clear();
        Cardinality = 0;
        CurrentPath = paths ?? "";
        Hits = 0;

        if (string.IsNullOrWhiteSpace(paths) || paths == "<empty>")
            return;

        SyzygyTables.EnsureInitialised();

        var dirs = paths.Split(';', StringSplitOptions.RemoveEmptyEntries
                                  | StringSplitOptions.TrimEntries);

        // Enumerate the same material codes the reference does, in the same
        // order, and register the ones whose .rtbw exists.
        for (int p1 = 0; p1 < 5; p1++)
        {
            Add(dirs, $"K{C(p1)}vK");
            for (int p2 = 0; p2 <= p1; p2++)
            {
                Add(dirs, $"K{C(p1)}{C(p2)}vK");
                Add(dirs, $"K{C(p1)}vK{C(p2)}");
                for (int p3 = 0; p3 < 5; p3++)
                    Add(dirs, $"K{C(p1)}{C(p2)}vK{C(p3)}");
                for (int p3 = 0; p3 <= p2; p3++)
                {
                    Add(dirs, $"K{C(p1)}{C(p2)}{C(p3)}vK");
                    for (int p4 = 0; p4 <= p3; p4++)
                    {
                        Add(dirs, $"K{C(p1)}{C(p2)}{C(p3)}{C(p4)}vK");
                        for (int p5 = 0; p5 <= p4; p5++)
                            Add(dirs, $"K{C(p1)}{C(p2)}{C(p3)}{C(p4)}{C(p5)}vK");
                        for (int p5 = 0; p5 < 5; p5++)
                            Add(dirs, $"K{C(p1)}{C(p2)}{C(p3)}{C(p4)}vK{C(p5)}");
                    }
                    for (int p4 = 0; p4 < 5; p4++)
                    {
                        Add(dirs, $"K{C(p1)}{C(p2)}{C(p3)}vK{C(p4)}");
                        for (int p5 = 0; p5 <= p4; p5++)
                            Add(dirs, $"K{C(p1)}{C(p2)}{C(p3)}vK{C(p4)}{C(p5)}");
                    }
                }
                for (int p3 = 0; p3 <= p1; p3++)
                    for (int p4 = 0; p4 <= (p3 == p1 ? p2 : p3); p4++)
                        Add(dirs, $"K{C(p1)}{C(p2)}vK{C(p3)}{C(p4)}");
            }
        }
    }

    private static char C(int pieceType) => "PNBRQ"[pieceType];

    private static void Add(string[] dirs, string code)
    {
        string? wdlPath = Find(dirs, code + ".rtbw");
        if (wdlPath is null)
            return;

        var wdl = new SyzygyTable(wdlPath, isWdl: true);
        wdl.DescribeFromCode(code);
        WdlByKey[wdl.Key] = wdl;
        if (wdl.Key2 != wdl.Key)
            WdlByKey[wdl.Key2] = wdl;

        if (wdl.PieceCount > Cardinality)
            Cardinality = wdl.PieceCount;

        string? dtzPath = Find(dirs, code + ".rtbz");
        if (dtzPath is not null)
        {
            var dtz = new SyzygyTable(dtzPath, isWdl: false);
            dtz.CopyDescriptionFrom(wdl);
            DtzByKey[dtz.Key] = dtz;
            if (dtz.Key2 != dtz.Key)
                DtzByKey[dtz.Key2] = dtz;
        }
    }

    private static string? Find(string[] dirs, string file)
    {
        foreach (string d in dirs)
        {
            string full = Path.Combine(d, file);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    // ---- Probing ----

    /// Probes WDL. Returns false when the position is not in the tablebases.
    public static bool ProbeWdl(Board board, out WdlScore score)
    {
        score = WdlScore.Draw;
        if (!Available)
            return false;

        var state = ProbeState.Ok;
        WdlScore v = Search(board, checkZeroing: false, ref state);
        if (state == ProbeState.Fail)
            return false;
        score = v;
        Hits++;
        return true;
    }

    /// Probes DTZ (plies to the next irreversible move), from the point of view
    /// of the side to move. Returns false when the position is not covered.
    public static bool ProbeDtz(Board board, out int dtz)
    {
        dtz = 0;
        if (!Available)
            return false;

        var state = ProbeState.Ok;
        WdlScore wdl = Search(board, checkZeroing: true, ref state);

        if (state == ProbeState.Fail)
            return false;
        if (wdl == WdlScore.Draw)           // DTZ tables do not store draws
            return true;                    // dtz stays 0, which is correct

        if (state == ProbeState.ZeroingBestMove)
        {
            dtz = DtzBeforeZeroing(wdl);
            Hits++;
            return true;
        }

        int value = ProbeTable(board, isWdl: false, wdl, ref state);
        if (state == ProbeState.Fail)
            return false;

        if (state != ProbeState.ChangeStm)
        {
            int cursed = wdl is WdlScore.BlessedLoss or WdlScore.CursedWin ? 1 : 0;
            dtz = (value + 100 * cursed) * Sign((int)wdl);
            Hits++;
            return true;
        }

        // The table stores the other side's result: resolve with a one-ply
        // search picking the move that minimises DTZ.
        int minDtz = 0xFFFF;
        var moves = new MoveList();
        MoveGenerator.GenerateLegalMoves(board, moves);

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            bool zeroing = m.IsCapture || board.PieceTypeAt(m.From) == PieceType.Pawn;

            board.MakeMove(m);
            int d;
            if (zeroing)
            {
                var s2 = ProbeState.Ok;
                d = -DtzBeforeZeroing(Search(board, checkZeroing: false, ref s2));
                if (s2 == ProbeState.Fail) { board.UnmakeMove(); return false; }
            }
            else
            {
                if (!ProbeDtz(board, out int child)) { board.UnmakeMove(); return false; }
                d = -child;
            }

            // A move that mates forces DTZ 1.
            if (d == 1 && board.IsInCheck())
            {
                var reply = new MoveList();
                MoveGenerator.GenerateLegalMoves(board, reply);
                if (reply.Count == 0)
                    minDtz = 1;
            }

            if (!zeroing)
                d += Sign(d);

            if (d < minDtz && Sign(d) == Sign((int)wdl))
                minDtz = d;

            board.UnmakeMove();
        }

        dtz = minDtz == 0xFFFF ? -1 : minDtz;
        Hits++;
        return true;
    }

    private static int Sign(int v) => v > 0 ? 1 : v < 0 ? -1 : 0;

    // DTZ tables store no value for moves that reset the fifty-move counter,
    // but the DTZ of the move BEFORE such a move follows from the WDL score.
    private static int DtzBeforeZeroing(WdlScore wdl) => wdl switch
    {
        WdlScore.Win => 1,
        WdlScore.CursedWin => 101,
        WdlScore.BlessedLoss => -101,
        WdlScore.Loss => -1,
        _ => 0
    };

    // Reference search(): resolves captures (and, for DTZ, pawn moves) before
    // consulting a table, because the tables do not store positions with an
    // en-passant right and a capture may leave the covered material set.
    private static WdlScore Search(Board board, bool checkZeroing, ref ProbeState state)
    {
        WdlScore bestValue = WdlScore.Loss;
        var moves = new MoveList();
        MoveGenerator.GenerateLegalMoves(board, moves);
        int totalCount = moves.Count, moveCount = 0;

        for (int i = 0; i < moves.Count; i++)
        {
            Move m = moves[i];
            if (!m.IsCapture && (!checkZeroing || board.PieceTypeAt(m.From) != PieceType.Pawn))
                continue;

            moveCount++;
            board.MakeMove(m);
            WdlScore v = (WdlScore)(-(int)Search(board, false, ref state));
            board.UnmakeMove();

            if (state == ProbeState.Fail)
                return WdlScore.Draw;

            if (v > bestValue)
            {
                bestValue = v;
                if (v >= WdlScore.Win)
                {
                    state = ProbeState.ZeroingBestMove;
                    return v;
                }
            }
        }

        // With every legal move already searched the table must not be probed:
        // its stored score would be wrong (tables carry no en-passant rights).
        bool noMoreMoves = moveCount > 0 && moveCount == totalCount;

        WdlScore value;
        if (noMoreMoves)
        {
            value = bestValue;
        }
        else
        {
            value = (WdlScore)ProbeTable(board, isWdl: true, WdlScore.Draw, ref state);
            if (state == ProbeState.Fail)
                return WdlScore.Draw;
        }

        if (bestValue >= value)
        {
            state = bestValue > WdlScore.Draw || noMoreMoves
                ? ProbeState.ZeroingBestMove : ProbeState.Ok;
            return bestValue;
        }

        state = ProbeState.Ok;
        return value;
    }

    // Reference do_probe_table(): maps the position to a table index.
    private static int ProbeTable(Board board, bool isWdl, WdlScore wdl, ref ProbeState state)
    {
        // No piece may be capturable en passant and castling must be gone:
        // the tables know nothing about either.
        int pieceCount = System.Numerics.BitOperations.PopCount(board.AllOccupancy);

        // Bare kings. No two-man table exists, yet captures routinely reduce to
        // it (KQvK answered with KxQ, say), and the recursion would otherwise
        // fail and discard a perfectly known result. It is a dead draw.
        if (pieceCount <= 2)
        {
            state = ProbeState.Ok;
            return isWdl ? 0 : 0;
        }

        if (pieceCount > Cardinality || board.CastlingRights != CastlingRights.None)
        {
            state = ProbeState.Fail;
            return 0;
        }

        ulong key = SyzygyTables.MaterialKey(board);
        var dict = isWdl ? WdlByKey : DtzByKey;
        if (!dict.TryGetValue(key, out SyzygyTable? entry) || !entry.EnsureLoaded())
        {
            state = ProbeState.Fail;
            return 0;
        }

        Span<int> squares = stackalloc int[SyzygyTables.TbPieces];
        Span<int> pieces = stackalloc int[SyzygyTables.TbPieces];
        int size = 0, leadPawnsCnt = 0;
        int tbFile = 0;
        ulong idx;

        // A table like KRK carries two material keys (KRvk and Kvkr). When both
        // sides hold the same material the file only stores white to move, so a
        // black-to-move lookup has to flip colours and squares.
        bool symmetricBlackToMove = entry.Key == entry.Key2 && board.SideToMove == Color.Black;
        // Files are generated with white as the stronger side.
        bool blackStronger = key != entry.Key;

        bool flip = symmetricBlackToMove || blackStronger;
        int flipColor = flip ? 8 : 0;
        int flipSquares = flip ? 56 : 0;
        int stm = (flip ? 1 : 0) ^ (board.SideToMove == Color.Black ? 1 : 0);

        ulong leadPawns = 0;

        if (entry.HasPawns)
        {
            // Pawns come first in the stored sequence and carry the reference
            // colour, so the first entry identifies which side's pawns lead.
            int pc = entry.Get(0, 0).Pieces[0] ^ flipColor;
            Color pawnColor = (pc >> 3) != 0 ? Color.Black : Color.White;

            leadPawns = board.Pieces(pawnColor, PieceType.Pawn);
            ulong b = leadPawns;
            while (b != 0)
            {
                int sq = System.Numerics.BitOperations.TrailingZeroCount(b);
                b &= b - 1;
                squares[size++] = sq ^ flipSquares;
            }
            leadPawnsCnt = size;

            // The leading pawn is the one with the highest MapPawns value.
            int best = 0;
            for (int i = 1; i < leadPawnsCnt; i++)
                if (SyzygyTables.MapPawns[squares[i]] > SyzygyTables.MapPawns[squares[best]])
                    best = i;
            (squares[0], squares[best]) = (squares[best], squares[0]);

            int f = squares[0] & 7;
            tbFile = Math.Min(f, 7 - f);          // Distance to the nearer edge
        }

        // DTZ files are one-sided; if this position is stored for the other
        // side the caller has to resolve it with a one-ply search.
        if (!isWdl)
        {
            var pd0 = entry.Get(stm, tbFile);
            bool ok = ((pd0.Flags & (int)TbFlag.Stm) != 0 ? 1 : 0) == stm
                      || (entry.Key == entry.Key2 && !entry.HasPawns);
            if (!ok)
            {
                state = ProbeState.ChangeStm;
                return 0;
            }
        }

        // Everything except the leading pawns, mapped to the reference colour.
        ulong rest = board.AllOccupancy ^ leadPawns;
        while (rest != 0)
        {
            int sq = System.Numerics.BitOperations.TrailingZeroCount(rest);
            rest &= rest - 1;
            squares[size] = sq ^ flipSquares;
            // Reference piece encoding: colour * 8 + (type + 1).
            int color = board.ColorAt(sq) == Color.White ? 0 : 1;
            int type = (int)board.PieceTypeAt(sq) + 1;
            pieces[size++] = (color * 8 + type) ^ flipColor;
        }

        PairsData d = entry.Get(stm, tbFile);

        // Reorder to match the stored piece sequence, which is what makes the
        // grouping (and therefore the index arithmetic) line up.
        for (int i = leadPawnsCnt; i < size - 1; i++)
            for (int j = i + 1; j < size; j++)
                if (d.Pieces[i] == pieces[j])
                {
                    (pieces[i], pieces[j]) = (pieces[j], pieces[i]);
                    (squares[i], squares[j]) = (squares[j], squares[i]);
                    break;
                }

        // Map so the leading piece sits in the a1-d1-d4 triangle.
        if ((squares[0] & 7) > 3)
            for (int i = 0; i < size; i++)
                squares[i] ^= 7;                  // flip file

        if (entry.HasPawns)
        {
            idx = (ulong)SyzygyTables.LeadPawnIdx[leadPawnsCnt, squares[0]];

            // Remaining lead pawns in ascending MapPawns order.
            for (int i = 1; i < leadPawnsCnt - 1; i++)
                for (int j = i + 1; j < leadPawnsCnt; j++)
                    if (SyzygyTables.MapPawns[squares[i]] > SyzygyTables.MapPawns[squares[j]])
                        (squares[i], squares[j]) = (squares[j], squares[i]);

            for (int i = 1; i < leadPawnsCnt; i++)
                idx += (ulong)SyzygyTables.Binomial[i, SyzygyTables.MapPawns[squares[i]]];

            return EncodeRemaining(entry, d, squares, size, leadPawnsCnt, idx,
                                   tbFile, isWdl, wdl, ref state);
        }

        // Without pawns, flip further so the leading piece is below rank 5.
        if ((squares[0] >> 3) > 3)
            for (int i = 0; i < size; i++)
                squares[i] ^= 56;                 // flip rank

        // Ensure the first leading piece off the diagonal is BELOW it.
        for (int i = 0; i < d.GroupLen[0]; i++)
        {
            if (SyzygyTables.OffA1H8(squares[i]) == 0)
                continue;
            if (SyzygyTables.OffA1H8(squares[i]) > 0)
                for (int j = i; j < size; j++)
                    squares[j] = ((squares[j] >> 3) | (squares[j] << 3)) & 63;  // a1-h8 mirror
            break;
        }

        if (entry.HasUniquePieces)
        {
            int adjust1 = squares[1] > squares[0] ? 1 : 0;
            int adjust2 = (squares[2] > squares[0] ? 1 : 0) + (squares[2] > squares[1] ? 1 : 0);

            if (SyzygyTables.OffA1H8(squares[0]) != 0)
                idx = (ulong)((SyzygyTables.MapA1D1D4[squares[0]] * 63 + (squares[1] - adjust1))
                              * 62 + squares[2] - adjust2);
            else if (SyzygyTables.OffA1H8(squares[1]) != 0)
                idx = (ulong)((6 * 63 + (squares[0] >> 3) * 28 + SyzygyTables.MapB1H1H7[squares[1]])
                              * 62 + squares[2] - adjust2);
            else if (SyzygyTables.OffA1H8(squares[2]) != 0)
                idx = (ulong)(6 * 63 * 62 + 4 * 28 * 62 + (squares[0] >> 3) * 7 * 28
                              + ((squares[1] >> 3) - adjust1) * 28 + SyzygyTables.MapB1H1H7[squares[2]]);
            else
                idx = (ulong)(6 * 63 * 62 + 4 * 28 * 62 + 4 * 7 * 28 + (squares[0] >> 3) * 7 * 6
                              + ((squares[1] >> 3) - adjust1) * 6 + ((squares[2] >> 3) - adjust2));
        }
        else
        {
            idx = (ulong)SyzygyTables.MapKK[SyzygyTables.MapA1D1D4[squares[0]], squares[1]];
        }

        return EncodeRemaining(entry, d, squares, size, leadPawnsCnt, idx,
                               tbFile, isWdl, wdl, ref state);
    }

    // Reference encode_remaining: the groups after the leading one.
    private static int EncodeRemaining(SyzygyTable entry, PairsData d, Span<int> squares,
                                       int size, int leadPawnsCnt, ulong idx, int tbFile,
                                       bool isWdl, WdlScore wdl, ref ProbeState state)
    {
        idx *= d.GroupIdx[0];
        int groupSq = d.GroupLen[0];
        bool remainingPawns = entry.HasPawns && entry.PawnCount[1] > 0;
        int next = 0;

        while (d.GroupLen[++next] != 0)
        {
            // Stable sort of this group's squares, ascending.
            for (int i = groupSq; i < groupSq + d.GroupLen[next] - 1; i++)
                for (int j = i + 1; j < groupSq + d.GroupLen[next]; j++)
                    if (squares[i] > squares[j])
                        (squares[i], squares[j]) = (squares[j], squares[i]);

            ulong n = 0;
            for (int i = 0; i < d.GroupLen[next]; i++)
            {
                int adjust = 0;
                for (int k = 0; k < groupSq; k++)
                    if (squares[groupSq + i] > squares[k])
                        adjust++;
                n += (ulong)SyzygyTables.Binomial[i + 1,
                        squares[groupSq + i] - adjust - (remainingPawns ? 8 : 0)];
            }

            remainingPawns = false;
            idx += n * d.GroupIdx[next];
            groupSq += d.GroupLen[next];
        }

        int value = entry.DecompressPairs(d, idx);
        state = ProbeState.Ok;
        return isWdl ? value - 2 : MapDtzScore(entry, d, value, wdl);
    }

    // Reference map_score() for DTZ: values are stored re-mapped by frequency
    // and may be counted in moves rather than plies.
    private static int MapDtzScore(SyzygyTable entry, PairsData d, int value, WdlScore wdl)
    {
        ReadOnlySpan<int> wdlMap = [1, 3, 0, 2, 0];
        int flags = d.Flags;

        if ((flags & (int)TbFlag.Mapped) != 0)
        {
            int mi = d.MapIdx[wdlMap[(int)wdl + 2]];
            value = (flags & (int)TbFlag.Wide) != 0
                ? entry.MapValueWide(mi + value)
                : entry.MapValue(mi + value);
        }

        if ((wdl == WdlScore.Win && (flags & (int)TbFlag.WinPlies) == 0)
            || (wdl == WdlScore.Loss && (flags & (int)TbFlag.LossPlies) == 0)
            || wdl == WdlScore.CursedWin || wdl == WdlScore.BlessedLoss)
            value *= 2;

        return value + 1;
    }
}
