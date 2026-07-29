using NoaChess.Core;

namespace NoaChess.DataGen;

// `pgnbook` subcommand: turns human PGN databases (Lichess elite, chess.com GMs)
// into an opening-seed book of FENs. For each game it replays the main line and
// records the position at a random ply in [min-ply, max-ply] — a diverse but
// REALISTIC set of opening/early-middlegame positions to seed the datagen with,
// replacing the "8-9 random legal moves" that over-sample junk positions.
//
//   NoaChess.DataGen pgnbook --in games\ --out books\human.fens [--min-ply 12]
//       [--max-ply 20] [--per-game 1] [--max 0] [--seed N] [--dedup]
public static class PgnBook
{
    public static int Run(string[] args)
    {
        string? input = null, output = null;
        int minPly = 12, maxPly = 20, perGame = 1, seed = Environment.TickCount;
        long max = 0;
        bool dedup = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in": input = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--min-ply": minPly = int.Parse(args[++i]); break;
                case "--max-ply": maxPly = int.Parse(args[++i]); break;
                case "--per-game": perGame = int.Parse(args[++i]); break;
                case "--max": max = long.Parse(args[++i]); break;
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--dedup": dedup = true; break;
                default: Console.WriteLine($"pgnbook: unknown option '{args[i]}'"); return 1;
            }
        }

        if (input is null || output is null)
        {
            Console.WriteLine("pgnbook: --in <file|dir|glob> and --out <file> are required");
            return 1;
        }
        if (minPly < 1 || maxPly < minPly)
        {
            Console.WriteLine("pgnbook: need 1 <= min-ply <= max-ply");
            return 1;
        }

        string[] files = ResolveInputs(input);
        if (files.Length == 0)
        {
            Console.WriteLine($"pgnbook: no PGN files matched '{input}'");
            return 1;
        }

        var rng = new Random(seed);
        var seen = dedup ? new HashSet<string>() : null;
        long games = 0, written = 0, failed = 0, tooShort = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var writer = new StreamWriter(output);

        Console.WriteLine($"pgnbook: {files.Length} file(s), ply=[{minPly},{maxPly}], per-game={perGame}, seed={seed}, dedup={dedup}");

        foreach (string file in files)
        {
            using var reader = new StreamReader(file);
            foreach (List<string> moves in PgnReader.ReadGames(reader))
            {
                games++;
                if (moves.Count < minPly) { tooShort++; continue; }
                int hi = Math.Min(maxPly, moves.Count);

                for (int k = 0; k < perGame; k++)
                {
                    int target = minPly + rng.Next(hi - minPly + 1);
                    var board = new Board();
                    bool ok = true;
                    for (int p = 0; p < target; p++)
                    {
                        if (!San.TryParse(board, moves[p], out Move move)) { ok = false; break; }
                        board.MakeMove(move);
                    }
                    if (!ok) { failed++; break; } // a bad token voids the whole game

                    string fen = Fen.Save(board);
                    if (seen != null && !seen.Add(fen)) continue;
                    writer.WriteLine(fen);
                    written++;
                    if (max > 0 && written >= max) { Report(games, written, failed, tooShort); return 0; }
                }

                if (games % 100000 == 0)
                    Console.WriteLine($"  {games} games, {written} positions...");
            }
        }

        Report(games, written, failed, tooShort);
        return 0;
    }

    private static void Report(long games, long written, long failed, long tooShort) =>
        Console.WriteLine($"pgnbook done: games={games} written={written} failed={failed} tooShort={tooShort}");

    private static string[] ResolveInputs(string pattern)
    {
        if (Directory.Exists(pattern))
            return Directory.GetFiles(pattern, "*.pgn");

        string? dir = Path.GetDirectoryName(pattern);
        string name = Path.GetFileName(pattern);
        if (name.Contains('*') || name.Contains('?'))
            return Directory.GetFiles(string.IsNullOrEmpty(dir) ? "." : dir, name);

        return File.Exists(pattern) ? [pattern] : [];
    }
}
