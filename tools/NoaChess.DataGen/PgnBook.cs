using System.Diagnostics;
using NoaChess.Core;

namespace NoaChess.DataGen;

// `pgnbook` subcommand: turns human PGN databases (Lichess elite, chess.com GMs)
// into an opening-seed book of FENs. For each game it replays the main line and
// records the position at a random ply in [min-ply, max-ply] - a diverse but
// REALISTIC set of opening/early-middlegame positions to seed the datagen with,
// replacing the "8-9 random legal moves" that over-sample junk positions.
//
//   NoaChess.DataGen pgnbook --in games\ --out books\human.fens [--min-ply 12]
//       [--max-ply 20] [--per-game 1] [--max 0] [--seed N] [--dedup]
//       [--with-result] [--skip-bots]
//
// --with-result writes "FEN;R" (R = +1/0/-1 from White) instead of a bare FEN,
// which is what the datagen's --label-book mode needs for elite-game WDL
// anchoring. Games with no outcome ("*") are skipped in that mode.
public static class PgnBook
{
    public static int Run(string[] args)
    {
        string? input = null, output = null;
        int minPly = 12, maxPly = 20, perGame = 1, seed = Environment.TickCount;
        long max = 0;
        bool dedup = false;
        // --with-result appends the game's final result to each line ("FEN;R",
        // R = +1/0/-1 from White). That is what the datagen's --label-book mode
        // consumes for elite-game WDL anchoring; games still in progress ("*")
        // are skipped because they carry no outcome to anchor to.
        bool withResult = false;
        // --skip-bots drops games where either side is an engine account
        // ([WhiteTitle "BOT"] / [BlackTitle "BOT"]). Measured on the 2023-03
        // Lichess elite file: ~20k BOT tags per 400 MB, so a few percent of the
        // corpus. It matters most for --with-result, where the entire value of
        // the label is that a HUMAN game's outcome is information the engine
        // cannot generate for itself.
        bool skipBots = false;
        bool append = false;

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
                case "--with-result": withResult = true; break;
                case "--skip-bots": skipBots = true; break;
                case "--append": append = true; break;
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

        long totalBytes = files.Sum(f => new FileInfo(f).Length);

        var rng = new Random(seed);
        var seen = dedup ? new HashSet<string>() : null;
        long games = 0, written = 0, failed = 0, tooShort = 0, noResult = 0, botGames = 0;
        long bytesCompleted = 0;
        var sw = Stopwatch.StartNew();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var writer = new StreamWriter(output, append);

        Console.WriteLine($"pgnbook: {files.Length} file(s), {totalBytes / 1_048_576:N0} MB, ply=[{minPly},{maxPly}], per-game={perGame}, seed={seed}, dedup={dedup}, append={append}, with-result={withResult}, skip-bots={skipBots}");

        foreach (string file in files)
        {
            using var reader = new StreamReader(file);
            foreach (PgnGame game in PgnReader.ReadGames(reader))
            {
                games++;
                if (skipBots && game.HasBot) { botGames++; continue; }
                List<string> moves = game.Moves;
                if (moves.Count < minPly) { tooShort++; continue; }
                // WDL anchoring needs an actual outcome; an unfinished game has none.
                if (withResult && !game.HasResult) { noResult++; continue; }
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
                    if (!ok) { failed++; break; }

                    string fen = Fen.Save(board);
                    if (seen != null && !seen.Add(fen)) continue;
                    writer.WriteLine(withResult ? $"{fen};{game.Result}" : fen);
                    written++;
                    if (max > 0 && written >= max) { Report(games, written, failed, tooShort, noResult, botGames); return 0; }
                }

                if (games % 100000 == 0)
                {
                    double secs = sw.Elapsed.TotalSeconds;
                    double rate = secs > 0 ? games / secs : 0;
                    long bytesRead = bytesCompleted + reader.BaseStream.Position;
                    double pct = totalBytes > 0 ? 100.0 * bytesRead / totalBytes : 0;
                    double etaSecs = pct > 0 ? secs * (100.0 - pct) / pct : 0;
                    var eta = TimeSpan.FromSeconds(etaSecs);
                    Console.WriteLine($"  {games:N0} games, {written:N0} pos, {rate:N0} g/s, {pct:F1}% done, ETA {eta:hh\\:mm\\:ss}");
                }
            }
            bytesCompleted += new FileInfo(file).Length;
        }

        Report(games, written, failed, tooShort, noResult, botGames);
        return 0;
    }

    private static void Report(long games, long written, long failed, long tooShort, long noResult, long botGames) =>
        Console.WriteLine($"pgnbook done: games={games} written={written} failed={failed} "
                        + $"tooShort={tooShort} noResult={noResult} botGames={botGames}");

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
