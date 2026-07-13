using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Tuner;

// Texel tuning of the classical evaluation parameters.
//
// Reads NOADATA1 self-play datasets (positions + game result labels, see
// tools/NoaChess.DataGen/DatasetFormat.cs), then minimizes the mean squared
// error between sigmoid(K * eval) and the actual game result over millions of
// positions, by coordinate descent on the parameters in EvaluationParams.
//
// Usage:
//   NoaChess.Tuner <dataset.noadata> [more.noadata ...]
//       [--max-positions N] [--passes N] [--out file.txt]
//
// The tuned values are printed (and written to --out) as a C# snippet ready to
// paste into EvaluationParams.cs. Material and PSTs are NOT tuned: they are
// the PeSTO anchor that keeps the scale of everything else meaningful.
public static class Program
{
    private const int RecordSize = 40;
    private const int HeaderSize = 64;

    public static int Main(string[] args)
    {
        var files = new List<string>();
        int maxPositions = 2_000_000;
        int passes = 3;
        string? outFile = null;
        bool only4E = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--max-positions": maxPositions = int.Parse(args[++i]); break;
                case "--passes": passes = int.Parse(args[++i]); break;
                case "--out": outFile = args[++i]; break;
                case "--only-4e": only4E = true; break;
                default: files.Add(args[i]); break;
            }
        }

        if (files.Count == 0)
        {
            Console.WriteLine("usage: NoaChess.Tuner <dataset.noadata> [...] [--max-positions N] [--passes N] [--out file]");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        List<Position> positions = LoadPositions(files, maxPositions);
        Console.WriteLine($"Loaded {positions.Count:N0} positions in {sw.Elapsed.TotalSeconds:F1}s");
        if (positions.Count == 0) return 1;

        var tuner = new TexelTuner(positions);
        double k = tuner.OptimizeK();
        Console.WriteLine($"Optimal K = {k:F4}, initial error = {tuner.Error(k):F6}");

        var parameters = only4E ? ParameterRegistry.Build4E() : ParameterRegistry.Build();
        Console.WriteLine($"Tuning {parameters.Count} scalar values over {passes} passes...");
        tuner.CoordinateDescent(parameters, k, passes);

        string snippet = only4E ? ParameterRegistry.ToSnippet4E() : ParameterRegistry.ToSnippet();
        Console.WriteLine();
        Console.WriteLine(snippet);
        if (outFile != null)
            File.WriteAllText(outFile, snippet);

        Console.WriteLine($"Done in {sw.Elapsed.TotalMinutes:F1} min. Final error = {tuner.Error(k):F6}");
        return 0;
    }

    // A position ready to evaluate: the reconstructed board plus the game
    // result from White's point of view (1 / 0.5 / 0).
    public readonly record struct Position(Board Board, double ResultWhite);

    private static List<Position> LoadPositions(List<string> files, int maxPositions)
    {
        // First count total records so the subsampling stride covers all files
        // evenly instead of exhausting the budget on the first one.
        long totalRecords = 0;
        foreach (string f in files)
            totalRecords += (new FileInfo(f).Length - HeaderSize) / RecordSize;
        long stride = Math.Max(1, totalRecords / maxPositions);
        Console.WriteLine($"{totalRecords:N0} records on disk, sampling every {stride} -> ~{totalRecords / stride:N0}");

        var positions = new List<Position>(maxPositions + 1024);
        Span<byte> record = stackalloc byte[RecordSize];
        long index = 0;

        foreach (string file in files)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            fs.Position = HeaderSize;
            while (fs.Read(record) == RecordSize)
            {
                if (index++ % stride != 0)
                    continue;

                short score = BinaryPrimitives.ReadInt16LittleEndian(record[30..]);
                ushort ply = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
                // Skip the opening (book noise) and completely decided
                // positions (the sigmoid learns nothing at +15).
                if (ply < 16 || Math.Abs(score) > 1500)
                    continue;

                sbyte resultStm = (sbyte)record[32];
                bool blackToMove = record[24] != 0;
                double resultWhite = 0.5 + (blackToMove ? -resultStm : resultStm) * 0.5;

                positions.Add(new Position(new Board(RecordToFen(record)), resultWhite));
            }
        }
        return positions;
    }

    // Rebuilds a FEN string from a 40-byte NOADATA1 record.
    private static string RecordToFen(ReadOnlySpan<byte> record)
    {
        ulong occupancy = BinaryPrimitives.ReadUInt64LittleEndian(record);
        var pieceAt = new char[64];

        int nibble = 0;
        ulong occ = occupancy;
        const string codes = "PNBRQKpnbrqk";
        while (occ != 0)
        {
            int sq = Bitboard.PopLsb(ref occ);
            int b = record[8 + nibble / 2];
            int code = (nibble & 1) == 0 ? b & 0xF : b >> 4;
            pieceAt[sq] = codes[code];
            nibble++;
        }

        var sb = new StringBuilder(90);
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                char c = pieceAt[rank * 8 + file];
                if (c == '\0') { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                sb.Append(c);
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }

        sb.Append(record[24] == 0 ? " w " : " b ");

        int castling = record[25];
        if (castling == 0) sb.Append('-');
        else
        {
            if ((castling & 1) != 0) sb.Append('K');
            if ((castling & 2) != 0) sb.Append('Q');
            if ((castling & 4) != 0) sb.Append('k');
            if ((castling & 8) != 0) sb.Append('q');
        }

        int ep = record[26];
        sb.Append(' ');
        if (ep == 255) sb.Append('-');
        else sb.Append((char)('a' + ep % 8)).Append((char)('1' + ep / 8));

        sb.Append(' ').Append(record[27]).Append(" 1");
        return sb.ToString();
    }
}
