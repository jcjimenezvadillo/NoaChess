using System.Buffers.Binary;
using NoaChess.Core;

namespace NoaChess.DataGen;

// coarse-encode: writes the COARSE threat companion for a .noadata shard.
//
// One record in, one variable-length record out: u8 count + count x u8
// absolute pair ids (attacker piece code * 12 + victim piece code, codes
// 0-11 as white 0-5 / black 6-11, the record nibble encoding itself). The
// trainer derives both perspectives from the absolute pairs by colour XOR,
// exactly as probe_coarse.coarse_buckets does, so one enumeration serves
// both sides.
//
// The relation set mirrors probe_coarse.coarse_relations STEP FOR STEP -
// the fine schema's target filters (pawns threaten P/N/R; minors and rooks
// P/N/B/R; knights and queens P/N/B/R/Q; kings neither attack nor are
// attacked), the pawn-stopped-by-a-pawn push relation, and NO symmetric
// deduplication: multiplicity is the signal, a repeated id IS the count.
// The +4.14% probe was measured on precisely this multiset; a Python
// parity script checks this encoder against the probe's own enumeration
// before anything trains on its output.
public static class CoarseEncode
{
    private const ulong NotFileA = 0xFEFEFEFEFEFEFEFE;
    private const ulong NotFileH = 0x7F7F7F7F7F7F7F7F;

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: coarse-encode <in.noadata> [out.coarsedata]");
            return 2;
        }
        string src = args[0];
        string dst = args.Length > 1 ? args[1]
            : Path.ChangeExtension(src, ".coarsedata");
        if (!File.Exists(src))
        {
            Console.Error.WriteLine($"coarse-encode: not found: {src}");
            return 2;
        }

        using var input = File.OpenRead(src);
        long records = (input.Length - DatasetFormat.HeaderSize) / DatasetFormat.RecordSize;
        input.Position = DatasetFormat.HeaderSize;

        using var output = new FileStream(dst + ".tmp", FileMode.Create, FileAccess.Write,
                                          FileShare.None, 1 << 20);
        Span<byte> head = stackalloc byte[16];
        "NOACRS1\0"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt64LittleEndian(head[8..], (ulong)records);
        output.Write(head);

        var record = new byte[DatasetFormat.RecordSize];
        var bbs = new ulong[12];
        var boardArr = new byte[64];
        var pairs = new byte[256];
        var buffered = new BufferedStream(input, 1 << 20);
        long done = 0;
        long totalRelations = 0;

        for (long r = 0; r < records; r++)
        {
            buffered.ReadExactly(record);
            int n = EncodeRecord(record, bbs, boardArr, pairs);
            output.WriteByte((byte)n);
            output.Write(pairs, 0, n);
            totalRelations += n;
            if (++done % 1_000_000 == 0)
                Console.WriteLine($"  {done}/{records} records");
        }

        output.Flush();
        output.Close();
        File.Move(dst + ".tmp", dst, overwrite: true);
        Console.WriteLine($"coarse-encode: {records} records, "
            + $"{(double)totalRelations / Math.Max(1, records):F1} relations/record -> {dst}");
        return 0;
    }

    // Enumerates the coarse relation multiset of one record into pairs[],
    // returning the count (saturated at 255, far above any legal position).
    public static int EncodeRecord(ReadOnlySpan<byte> record, ulong[] bbs,
                                   byte[] boardArr, byte[] pairs)
    {
        ulong occupancy = BinaryPrimitives.ReadUInt64LittleEndian(record);
        Array.Clear(bbs);
        int nibble = 0;
        ulong occ = occupancy;
        while (occ != 0)
        {
            int sq = System.Numerics.BitOperations.TrailingZeroCount(occ);
            occ &= occ - 1;
            int byteIndex = 8 + nibble / 2;
            int code = (nibble & 1) == 0 ? record[byteIndex] & 0xF : record[byteIndex] >> 4;
            nibble++;
            bbs[code] |= 1UL << sq;
            boardArr[sq] = (byte)code;
        }

        ulong pawns = bbs[0] | bbs[6];
        ulong pawnTargets = pawns | bbs[1] | bbs[7] | bbs[3] | bbs[9];
        ulong minorSliderTargets = pawnTargets | bbs[2] | bbs[8];
        ulong queenTargets = minorSliderTargets | bbs[4] | bbs[10];

        int n = 0;
        for (int c = 0; c < 2; c++)
        {
            int attacker = c * 6;
            ulong cPawns = bbs[attacker];

            // Captures, one diagonal at a time so multiplicity survives.
            ulong capA = c == 0 ? (cPawns & NotFileH) << 9 : (cPawns & NotFileH) >> 7;
            ulong capB = c == 0 ? (cPawns & NotFileA) << 7 : (cPawns & NotFileA) >> 9;
            foreach (ulong cap in stackalloc[] { capA, capB })
            {
                ulong hits = cap & pawnTargets;
                while (hits != 0 && n < 255)
                {
                    int to = System.Numerics.BitOperations.TrailingZeroCount(hits);
                    hits &= hits - 1;
                    pairs[n++] = (byte)(attacker * 12 + boardArr[to]);
                }
            }

            // The pawn stopped dead by the pawn in front of it.
            ulong pushers = (c == 0 ? pawns >> 8 : pawns << 8) & cPawns;
            ulong blocked = c == 0 ? pushers << 8 : pushers >> 8;
            while (blocked != 0 && n < 255)
            {
                int to = System.Numerics.BitOperations.TrailingZeroCount(blocked);
                blocked &= blocked - 1;
                pairs[n++] = (byte)(attacker * 12 + boardArr[to]);
            }

            for (int pt = 1; pt <= 4; pt++)
            {
                attacker = c * 6 + pt;
                ulong targets = (pt == 1 || pt == 4) ? queenTargets : minorSliderTargets;
                ulong from = bbs[attacker];
                while (from != 0)
                {
                    int sq = System.Numerics.BitOperations.TrailingZeroCount(from);
                    from &= from - 1;
                    ulong att = pt switch
                    {
                        1 => Attacks.Knight(sq),
                        2 => Attacks.Bishop(sq, occupancy),
                        3 => Attacks.Rook(sq, occupancy),
                        _ => Attacks.Queen(sq, occupancy),
                    };
                    ulong hits = att & targets;
                    while (hits != 0 && n < 255)
                    {
                        int to = System.Numerics.BitOperations.TrailingZeroCount(hits);
                        hits &= hits - 1;
                        pairs[n++] = (byte)(attacker * 12 + boardArr[to]);
                    }
                }
            }
        }
        return n;
    }
}
