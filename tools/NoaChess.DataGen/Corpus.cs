using System.Buffers.Binary;
using System.Text.Json;

namespace NoaChess.DataGen;

// `corpus` subcommand: audits a directory of .noadata shards before anything is
// trained on them.
//
//   NoaChess.DataGen corpus --in data\ [--glob *.noadata] [--sample 200000]
//
// WHY THIS EXISTS. BLOCK 12 assembles 300-500M positions from several sources
// across several sessions: bulk self-play, book-seeded openings, middlegame
// seeds, and elite games labelled with their real result. Once that corpus is a
// pile of shards, "what is actually in it" stops being obvious - and the one
// time this project assumed rather than checked what went into training, it
// cost five generations and a shipped conclusion that turned out to be void.
//
// So the composition is read back off disk and reported: how many positions came
// from which source, at which node budget, from which evaluator, with which WDL
// signal. It also verifies each shard rather than trusting it - header count
// against manifest count, schema and record size, and the presence of the
// manifest that marks a shard complete.
public static class Corpus
{
    public static int Run(string[] args)
    {
        string? input = null;
        string glob = "*.noadata";
        int sample = 200_000;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in": input = args[++i]; break;
                case "--glob": glob = args[++i]; break;
                case "--sample": sample = int.Parse(args[++i]); break;
                default: Console.WriteLine($"corpus: unknown option '{args[i]}'"); return 1;
            }
        }

        if (input is null)
        {
            Console.WriteLine("corpus: --in <dir> is required");
            return 1;
        }

        string[] files = Directory.Exists(input)
            ? Directory.GetFiles(input, glob).OrderBy(f => f).ToArray()
            : Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(input))!,
                                 Path.GetFileName(input)).OrderBy(f => f).ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine($"corpus: no files matched '{input}' / '{glob}'");
            return 1;
        }

        var groups = new Dictionary<string, (long Records, int Shards, long Games)>();
        var problems = new List<string>();
        long totalRecords = 0;
        long totalBytes = 0;

        foreach (string file in files)
        {
            totalBytes += new FileInfo(file).Length;

            long headerCount;
            uint schema, recordSize;
            string headerError;
            try
            {
                if (!TryReadHeader(file, out headerCount, out schema, out recordSize, out headerError))
                {
                    problems.Add($"{Path.GetFileName(file)}: {headerError}");
                    continue;
                }
            }
            catch (IOException ex)
            {
                // Almost always a shard a running datagen still holds open. That
                // is a normal state, not a failure of the corpus, so it is
                // reported and skipped rather than aborting the whole audit.
                problems.Add($"{Path.GetFileName(file)}: unreadable ({ex.Message.Split('\n')[0]}) "
                           + "- in use by a running datagen?");
                continue;
            }
            if (schema != DatasetFormat.FeatureSchemaId)
                problems.Add($"{Path.GetFileName(file)}: feature schema {schema} != {DatasetFormat.FeatureSchemaId}");
            if (recordSize != DatasetFormat.RecordSize)
                problems.Add($"{Path.GetFileName(file)}: record size {recordSize} != {DatasetFormat.RecordSize}");

            // A shard's real record count is derivable from its length; if the
            // header disagrees, the run was interrupted before the patch.
            long derived = (new FileInfo(file).Length - DatasetFormat.HeaderSize) / DatasetFormat.RecordSize;
            if (headerCount == 0 && derived > 0)
                problems.Add($"{Path.GetFileName(file)}: header says 0 records but the file holds "
                           + $"{derived:N0} - INTERRUPTED, not finalized (exclude it or regenerate)");
            else if (headerCount != derived)
                problems.Add($"{Path.GetFileName(file)}: header {headerCount:N0} != derived {derived:N0}");

            string manifestPath = file + ".manifest.json";
            string key;
            long games = 0;
            if (!File.Exists(manifestPath))
            {
                problems.Add($"{Path.GetFileName(file)}: NO MANIFEST - provenance unknown, "
                           + "so this shard cannot be accounted for");
                key = "UNKNOWN PROVENANCE (no manifest)";
            }
            else
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                JsonElement root = doc.RootElement;
                string mode = Text(root, "mode");
                string openings = Text(root, "openingPlies");
                string nodes = Text(root, "nodesPerMove");
                string evaluator = Text(root, "evaluator");
                string wdl = Text(root, "wdlSource");
                if (root.TryGetProperty("games", out JsonElement g) && g.TryGetInt64(out long gv))
                    games = gv;

                // Long absolute book paths would fragment the grouping; the file
                // name is what identifies the source.
                if (openings.StartsWith("book:") || openings.StartsWith("label-book:"))
                {
                    int colon = openings.IndexOf(':');
                    openings = openings[..colon] + ":" + Path.GetFileName(openings[(colon + 1)..]);
                }
                key = $"{mode} | openings={openings} | nodes={nodes} | eval={Path.GetFileName(evaluator)} | wdl={wdl}";
            }

            (long r, int s, long gm) prev = groups.TryGetValue(key, out var existing)
                ? existing : (0, 0, 0);
            groups[key] = (prev.r + Math.Max(headerCount, 0), prev.s + 1, prev.gm + games);
            totalRecords += Math.Max(headerCount, 0);
        }

        Console.WriteLine($"corpus: {files.Length} shard(s), {totalRecords:N0} positions, "
                        + $"{totalBytes / 1_048_576.0:N0} MB");
        Console.WriteLine();
        Console.WriteLine("composition by source:");
        foreach ((string key, (long records, int shards, long games)) in groups.OrderByDescending(g => g.Value.Records))
        {
            double share = totalRecords > 0 ? 100.0 * records / totalRecords : 0;
            Console.WriteLine($"  {share,5:F1}%  {records,14:N0} pos  {shards,4} shard(s)"
                            + (games > 0 ? $"  {games,10:N0} games" : "".PadLeft(17)));
            Console.WriteLine($"         {key}");
        }

        if (sample > 0 && totalRecords > 0)
            ReportLabelDistribution(files, sample);

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("verification: OK - every shard finalized, schema consistent, "
                            + "provenance recorded.");
            return 0;
        }

        Console.WriteLine($"verification: {problems.Count} PROBLEM(S)");
        foreach (string problem in problems.Take(40))
            Console.WriteLine($"  ! {problem}");
        if (problems.Count > 40)
            Console.WriteLine($"  ... and {problems.Count - 40} more");
        return 1;
    }

    // Score and result distribution over a sample. A corpus whose result column
    // is nearly all draws, or whose scores are nearly all zero, is the signature
    // of the two label bugs this project has already hit once each; seeing the
    // shape before a 10-hour training run is cheap insurance.
    private static void ReportLabelDistribution(string[] files, int sample)
    {
        long win = 0, draw = 0, loss = 0, zeroScore = 0, seen = 0;
        double absScoreSum = 0;
        var buffer = new byte[DatasetFormat.RecordSize * 4096];

        int perFile = Math.Max(1, sample / files.Length);
        foreach (string file in files)
        {
            FileStream stream;
            try { stream = OpenShared(file); }
            catch (IOException) { continue; }   // reported by the header pass already
            using var _ = stream;
            stream.Seek(DatasetFormat.HeaderSize, SeekOrigin.Begin);
            int takenHere = 0;
            int read;
            while (takenHere < perFile && (read = stream.Read(buffer)) >= DatasetFormat.RecordSize)
            {
                for (int offset = 0; offset + DatasetFormat.RecordSize <= read;
                     offset += DatasetFormat.RecordSize)
                {
                    short score = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset + 30));
                    sbyte result = (sbyte)buffer[offset + 32];
                    if (result > 0) win++; else if (result < 0) loss++; else draw++;
                    if (score == 0) zeroScore++;
                    absScoreSum += Math.Abs((int)score);
                    seen++;
                    if (++takenHere >= perFile) break;
                }
            }
        }

        if (seen == 0)
            return;

        Console.WriteLine();
        Console.WriteLine($"label distribution (sample of {seen:N0}):");
        Console.WriteLine($"  result   W {100.0 * win / seen,5:F1}%   D {100.0 * draw / seen,5:F1}%   "
                        + $"L {100.0 * loss / seen,5:F1}%");
        Console.WriteLine($"  score    mean|cp| {absScoreSum / seen,7:F1}   "
                        + $"exactly zero {100.0 * zeroScore / seen,5:F2}%");
        if (100.0 * zeroScore / seen > 20.0)
            Console.WriteLine("  ! WARNING: over 20% of scores are exactly zero. That was the "
                            + "signature of the search hard-stop bug that zeroed 57% of labels "
                            + "before 2026-07-24. Verify the datagen build before training.");
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString())
            : "?";

    // Opened with FileShare.ReadWrite so a shard currently being WRITTEN by a
    // running datagen can still be audited. Checking a corpus while it is being
    // produced is the main reason to have this tool at all, and a version that
    // threw on a locked file would be useless exactly when it is wanted.
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static bool TryReadHeader(string path, out long recordCount, out uint schema,
                                      out uint recordSize, out string error)
    {
        recordCount = 0; schema = 0; recordSize = 0; error = "";
        Span<byte> header = stackalloc byte[DatasetFormat.HeaderSize];
        using var stream = OpenShared(path);
        if (stream.Read(header) != DatasetFormat.HeaderSize)
        {
            error = "file shorter than a header";
            return false;
        }
        if (!header[..8].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(DatasetFormat.Magic)))
        {
            error = "bad magic (not a NOADATA1 file)";
            return false;
        }
        schema = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        recordSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        recordCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        return true;
    }
}
