using NoaChess.Core;
using System.IO.MemoryMappedFiles;

namespace NoaChess.Engine.Tablebases;

// One tablebase file (a .rtbw or .rtbz) and the indexing metadata needed to
// read it. See SyzygyTables.cs for why this is a managed port.
//
// The file is memory-mapped lazily and every "pointer" is a 64-bit offset into
// its read-only view. This is essential for 6/7-man DTZ files, many of which
// exceed the 2 GB limit of a managed byte[]; the OS pages in only the blocks a
// probe actually touches, while the port itself stays in safe managed code.
internal sealed class PairsData
{
    public byte Flags;
    public byte MaxSymLen;
    public byte MinSymLen;
    public uint BlocksNum;
    public long SizeofBlock;
    public int Span;
    public long LowestSymOffset;      // -> ushort[] in the file
    public long BtreeOffset;          // -> 3-byte LR records in the file
    public long BlockLengthOffset;    // -> ushort[]
    public uint BlockLengthSize;
    public long SparseIndexOffset;    // -> 6-byte SparseEntry records
    public ulong SparseIndexSize;
    public long DataOffset;           // Start of the Huffman-compressed blocks
    public ulong[] Base64 = [];
    public byte[] SymLen = [];
    public readonly int[] Pieces = new int[SyzygyTables.TbPieces];
    public readonly ulong[] GroupIdx = new ulong[SyzygyTables.TbPieces + 1];
    public readonly int[] GroupLen = new int[SyzygyTables.TbPieces + 1];
    public readonly ushort[] MapIdx = new ushort[4];
}

internal sealed class SyzygyTable : IDisposable
{
    public readonly bool IsWdl;
    public readonly string Path;
    public ulong Key;          // Material key with white as the stronger side
    public ulong Key2;         // ... and with the colours swapped
    public int PieceCount;
    public bool HasPawns;
    public bool HasUniquePieces;
    public readonly int[] PawnCount = new int[2];   // [lead colour, other colour]

    // [white-to-move / black-to-move][file A..D, or 0 when there are no pawns]
    private readonly PairsData[,] _items = new PairsData[2, 4];

    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private long _mapOffset;                 // DTZ value-remap table
    private volatile bool _ready;
    private volatile bool _failed;
    private readonly object _lock = new();

    public SyzygyTable(string path, bool isWdl)
    {
        Path = path;
        IsWdl = isWdl;
        for (int i = 0; i < 2; i++)
            for (int f = 0; f < 4; f++)
                _items[i, f] = new PairsData();
    }

    private int Sides => IsWdl ? 2 : 1;

    public PairsData Get(int stm, int file)
        => _items[IsWdl ? stm : 0, HasPawns ? file : 0];

    // Copies the material description worked out for the WDL table; the DTZ
    // file for the same material shares all of it.
    public void CopyDescriptionFrom(SyzygyTable wdl)
    {
        Key = wdl.Key;
        Key2 = wdl.Key2;
        PieceCount = wdl.PieceCount;
        HasPawns = wdl.HasPawns;
        HasUniquePieces = wdl.HasUniquePieces;
        PawnCount[0] = wdl.PawnCount[0];
        PawnCount[1] = wdl.PawnCount[1];
    }

    // Works out key/key2/pawn counts from a material code such as "KRPvKR".
    public void DescribeFromCode(string code)
    {
        // counts[colour * 6 + pieceType], our own PieceType order.
        Span<int> white = stackalloc int[6];
        Span<int> black = stackalloc int[6];
        bool afterV = false;
        foreach (char ch in code)
        {
            if (ch == 'v') { afterV = true; continue; }
            int pt = "PNBRQK".IndexOf(ch);
            if (pt < 0) continue;
            if (afterV) black[pt]++; else white[pt]++;
        }

        PieceCount = 0;
        for (int i = 0; i < 6; i++)
            PieceCount += white[i] + black[i];

        HasPawns = white[0] + black[0] > 0;

        HasUniquePieces = false;
        for (int pt = 0; pt < 5; pt++)          // Kings excluded
            if (white[pt] == 1 || black[pt] == 1)
                HasUniquePieces = true;

        // The leading colour is the side with FEWER pawns: it compresses better.
        bool leadIsWhite = black[0] == 0 || (white[0] != 0 && black[0] >= white[0]);
        PawnCount[0] = leadIsWhite ? white[0] : black[0];
        PawnCount[1] = leadIsWhite ? black[0] : white[0];

        Span<int> counts = stackalloc int[12];
        for (int i = 0; i < 6; i++) { counts[i] = white[i]; counts[6 + i] = black[i]; }
        Key = SyzygyTables.MaterialKey(counts);
        for (int i = 0; i < 6; i++) { counts[i] = black[i]; counts[6 + i] = white[i]; }
        Key2 = SyzygyTables.MaterialKey(counts);
    }

    // ---- little/big-endian readers over the mapped file ----
    private byte U8(long o) => _view!.ReadByte(o);
    private ushort U16LE(long o) => (ushort)(U8(o) | (U8(o + 1) << 8));
    private uint U32LE(long o) => (uint)(U8(o) | (U8(o + 1) << 8)
                                      | (U8(o + 2) << 16) | (U8(o + 3) << 24));
    private ulong U64BE(long o)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | U8(o + i);
        return v;
    }
    private uint U32BE(long o) => ((uint)U8(o) << 24) | ((uint)U8(o + 1) << 16)
                                | ((uint)U8(o + 2) << 8) | U8(o + 3);

    // The symbol tree is stored as 3-byte records: 12 bits for the left child,
    // 12 for the right. The base offset is PER PairsData — a pawn table holds
    // eight of them (4 files x 2 sides), each with its own tree — so it must
    // never be cached on the table itself.
    private ushort BtreeLeft(PairsData d, int sym)
    {
        long o = d.BtreeOffset + sym * 3L;
        return (ushort)(((U8(o + 1) & 0xF) << 8) | U8(o));
    }
    private ushort BtreeRight(PairsData d, int sym)
    {
        long o = d.BtreeOffset + sym * 3L;
        return (ushort)((U8(o + 2) << 4) | (U8(o + 1) >> 4));
    }

    // ---- lazy load ----
    public bool EnsureLoaded()
    {
        if (_ready) return true;
        if (_failed) return false;
        lock (_lock)
        {
            if (_ready) return true;
            if (_failed) return false;
            try
            {
                if (new FileInfo(Path).Length < 4)
                    throw new InvalidDataException($"Truncated Syzygy file: {Path}");

                _mapping = MemoryMappedFile.CreateFromFile(
                    Path, FileMode.Open, mapName: null, capacity: 0,
                    MemoryMappedFileAccess.Read);
                _view = _mapping.CreateViewAccessor(
                    0, 0, MemoryMappedFileAccess.Read);

                uint magic = U32LE(0);
                uint expected = IsWdl ? SyzygyTables.WdlMagic : SyzygyTables.DtzMagic;
                if (magic != expected)
                {
                    // A truncated download, or an HTTP error page saved under a
                    // tablebase name, would land here. Refusing is essential:
                    // the search trusts these answers completely.
                    CloseMapping();
                    _failed = true;
                    return false;
                }
                Setup(4);            // Skip the magic
                _ready = true;
                return true;
            }
            catch
            {
                CloseMapping();
                _failed = true;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseMapping();
            _ready = false;
            _failed = true;
        }
    }

    private void CloseMapping()
    {
        _view?.Dispose();
        _mapping?.Dispose();
        _view = null;
        _mapping = null;
    }

    // Reference set(): lays out every PairsData from the file header.
    private void Setup(long data)
    {
        const int Split = 1;
        const int HasPawnsFlag = 2;

        byte header = U8(data);
        bool split = (header & Split) != 0;
        bool hasPawnsFlag = (header & HasPawnsFlag) != 0;
        if (hasPawnsFlag != HasPawns || split != (Key != Key2))
            throw new InvalidDataException($"Syzygy header mismatch in {Path}");
        data++;

        int sides = Sides == 2 && Key != Key2 ? 2 : 1;
        int maxFile = HasPawns ? 3 : 0;
        bool pp = HasPawns && PawnCount[1] > 0;

        for (int f = 0; f <= maxFile; f++)
        {
            var order = new int[2, 2];
            order[0, 0] = U8(data) & 0xF;
            order[0, 1] = pp ? U8(data + 1) & 0xF : 0xF;
            order[1, 0] = U8(data) >> 4;
            order[1, 1] = pp ? U8(data + 1) >> 4 : 0xF;
            data += 1 + (pp ? 1 : 0);

            for (int k = 0; k < PieceCount; k++, data++)
                for (int i = 0; i < sides; i++)
                    Get(i, f).Pieces[k] = i != 0 ? U8(data) >> 4 : U8(data) & 0xF;

            for (int i = 0; i < sides; i++)
                SetGroups(Get(i, f), new[] { order[i, 0], order[i, 1] }, f);
        }

        data += data & 1;                       // Word alignment

        for (int f = 0; f <= maxFile; f++)
            for (int i = 0; i < sides; i++)
                data = SetSizes(Get(i, f), data);

        if (!IsWdl)
            data = SetDtzMap(data, maxFile);

        for (int f = 0; f <= maxFile; f++)
            for (int i = 0; i < sides; i++)
            {
                var d = Get(i, f);
                d.SparseIndexOffset = data;
                data = checked(data + checked((long)d.SparseIndexSize) * 6);
            }

        for (int f = 0; f <= maxFile; f++)
            for (int i = 0; i < sides; i++)
            {
                var d = Get(i, f);
                d.BlockLengthOffset = data;
                data = checked(data + (long)d.BlockLengthSize * 2);
            }

        for (int f = 0; f <= maxFile; f++)
            for (int i = 0; i < sides; i++)
            {
                var d = Get(i, f);
                data = checked((data + 0x3F) & ~0x3FL); // 64-byte alignment
                d.DataOffset = data;
                data = checked(data + (long)d.BlocksNum * d.SizeofBlock);
            }
    }

    // Reference set_groups(): decides which pieces are encoded together and
    // the multiplier each group contributes to the index.
    private void SetGroups(PairsData d, int[] order, int f)
    {
        int n = 0;
        int firstLen = HasPawns ? 0 : HasUniquePieces ? 3 : 2;
        d.GroupLen[n] = 1;

        for (int i = 1; i < PieceCount; i++)
            if (--firstLen > 0 || d.Pieces[i] == d.Pieces[i - 1])
                d.GroupLen[n]++;
            else
                d.GroupLen[++n] = 1;

        d.GroupLen[++n] = 0;                    // Zero-terminated

        bool pp = HasPawns && PawnCount[1] > 0;
        int next = pp ? 2 : 1;
        int freeSquares = 64 - d.GroupLen[0] - (pp ? d.GroupLen[1] : 0);
        ulong idx = 1;

        for (int k = 0; next < n || k == order[0] || k == order[1]; k++)
        {
            if (k == order[0])                  // Leading pawns or pieces
            {
                d.GroupIdx[0] = idx;
                idx *= (ulong)(HasPawns ? SyzygyTables.LeadPawnsSize[d.GroupLen[0], f]
                                        : HasUniquePieces ? 31332 : 462);
            }
            else if (k == order[1])             // Remaining pawns
            {
                d.GroupIdx[1] = idx;
                idx *= (ulong)SyzygyTables.Binomial[d.GroupLen[1], 48 - d.GroupLen[0]];
            }
            else                                // Remaining pieces
            {
                d.GroupIdx[next] = idx;
                idx *= (ulong)SyzygyTables.Binomial[d.GroupLen[next], freeSquares];
                freeSquares -= d.GroupLen[next++];
            }
        }
        d.GroupIdx[n] = idx;
    }

    // Reference set_symlen(): how many values a Huffman symbol expands to.
    // Iterative rather than recursive — the symbol tree can be deep and this
    // runs once per table, so an explicit stack is both safe and cheap.
    private void SetSymLen(PairsData d, int s, bool[] visited)
    {
        var stack = new Stack<int>();
        stack.Push(s);
        while (stack.Count > 0)
        {
            int sym = stack.Peek();
            if (!visited[sym])
                visited[sym] = true;

            int sr = BtreeRight(d, sym);
            if (sr == 0xFFF)                    // Leaf: expands to a single value
            {
                d.SymLen[sym] = 0;
                stack.Pop();
                continue;
            }
            int sl = BtreeLeft(d, sym);

            if (!visited[sl]) { stack.Push(sl); continue; }
            if (!visited[sr]) { stack.Push(sr); continue; }

            d.SymLen[sym] = (byte)(d.SymLen[sl] + d.SymLen[sr] + 1);
            stack.Pop();
        }
    }

    // Reference set_sizes(): block geometry and the canonical Huffman bases.
    private long SetSizes(PairsData d, long data)
    {
        d.Flags = U8(data++);

        if ((d.Flags & (int)TbFlag.SingleValue) != 0)
        {
            // Every position in this table stores the same value.
            d.BlocksNum = 0;
            d.BlockLengthSize = 0;
            d.Span = 0;
            d.SparseIndexSize = 0;
            d.MinSymLen = U8(data++);           // The value itself
            return data;
        }

        int zero = 0;
        while (zero < 7 && d.GroupLen[zero] != 0) zero++;
        ulong tbSize = d.GroupIdx[zero];

        int blockShift = U8(data++);
        int spanShift = U8(data++);
        if (blockShift >= 63 || spanShift >= 31)
            throw new InvalidDataException($"Invalid Syzygy block geometry in {Path}");

        d.SizeofBlock = 1L << blockShift;
        d.Span = 1 << spanShift;
        d.SparseIndexSize = (tbSize + (ulong)d.Span - 1) / (ulong)d.Span;
        int padding = U8(data++);
        d.BlocksNum = U32LE(data);
        data += 4;
        d.BlockLengthSize = d.BlocksNum + (uint)padding;

        d.MaxSymLen = U8(data++);
        d.MinSymLen = U8(data++);
        d.LowestSymOffset = data;

        int base64Size = d.MaxSymLen - d.MinSymLen + 1;
        d.Base64 = new ulong[base64Size];

        // Canonical Huffman: longer codes have lower numeric value, so base64
        // is built from the longest symbol downwards.
        for (int i = base64Size - 2; i >= 0; i--)
            d.Base64[i] = (d.Base64[i + 1]
                         + U16LE(d.LowestSymOffset + i * 2)
                         - U16LE(d.LowestSymOffset + (i + 1) * 2)) / 2;

        for (int i = 0; i < base64Size; i++)
            d.Base64[i] <<= 64 - i - d.MinSymLen;    // Right-pad to 64 bits

        data += base64Size * 2;
        int symLenCount = U16LE(data);
        data += 2;
        d.SymLen = new byte[symLenCount];
        d.BtreeOffset = data;

        var visited = new bool[symLenCount];
        for (int sym = 0; sym < symLenCount; sym++)
            if (!visited[sym])
                SetSymLen(d, sym, visited);

        return checked(data + symLenCount * 3L + (symLenCount & 1));
    }

    // Reference set_dtz_map(): DTZ values are stored re-mapped by frequency.
    private long SetDtzMap(long data, int maxFile)
    {
        _mapOffset = data;

        for (int f = 0; f <= maxFile; f++)
        {
            var d = Get(0, f);
            if ((d.Flags & (int)TbFlag.Mapped) == 0)
                continue;

            if ((d.Flags & (int)TbFlag.Wide) != 0)
            {
                data += data & 1;               // Word alignment
                for (int i = 0; i < 4; i++)
                {
                    d.MapIdx[i] = checked((ushort)((data - _mapOffset) / 2 + 1));
                    data += 2 * U16LE(data) + 2;
                }
            }
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    d.MapIdx[i] = checked((ushort)(data - _mapOffset + 1));
                    data += U8(data) + 1;
                }
            }
        }
        return data + (data & 1);
    }

    // Reference decompress_pairs(): walk the sparse index to the right block,
    // then decode canonical Huffman symbols until the wanted offset is reached.
    public int DecompressPairs(PairsData d, ulong idx)
    {
        if ((d.Flags & (int)TbFlag.SingleValue) != 0)
            return d.MinSymLen;

        ulong k = idx / (ulong)d.Span;

        long sparseEntry = checked(d.SparseIndexOffset + checked((long)k) * 6);
        uint block = U32LE(sparseEntry);
        int offset = U16LE(sparseEntry + 4);

        int diff = (int)(idx % (ulong)d.Span) - d.Span / 2;
        offset += diff;

        while (offset < 0)
            offset += U16LE(d.BlockLengthOffset + (long)(--block) * 2) + 1;

        while (offset > U16LE(d.BlockLengthOffset + (long)block * 2))
            offset -= U16LE(d.BlockLengthOffset + (long)block++ * 2) + 1;

        long ptr = checked(d.DataOffset + (long)block * d.SizeofBlock);
        ulong buf64 = U64BE(ptr);
        ptr += 8;
        int buf64Size = 64;
        int sym;

        while (true)
        {
            int len = 0;
            while (buf64 < d.Base64[len])
                len++;

            sym = (int)((buf64 - d.Base64[len]) >> (64 - len - d.MinSymLen));
            sym += U16LE(d.LowestSymOffset + len * 2);

            if (offset < d.SymLen[sym] + 1)
                break;

            offset -= d.SymLen[sym] + 1;
            len += d.MinSymLen;
            buf64 <<= len;
            buf64Size -= len;

            if (buf64Size <= 32)
            {
                buf64Size += 32;
                buf64 |= (ulong)U32BE(ptr) << (64 - buf64Size);
                ptr += 4;
            }
        }

        // Expand the symbol down to the single value we want.
        while (d.SymLen[sym] != 0)
        {
            int left = BtreeLeft(d, sym);
            if (offset < d.SymLen[left] + 1)
                sym = left;
            else
            {
                offset -= d.SymLen[left] + 1;
                sym = BtreeRight(d, sym);
            }
        }

        return BtreeLeft(d, sym);
    }

    public byte MapValue(int offset) => U8(_mapOffset + offset);
    public ushort MapValueWide(int offset) => U16LE(_mapOffset + offset * 2L);
}

[Flags]
internal enum TbFlag
{
    Stm = 1,
    Mapped = 2,
    WinPlies = 4,
    LossPlies = 8,
    Wide = 16,
    SingleValue = 128
}
