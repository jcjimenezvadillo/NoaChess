using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoaChess.Core;
using NoaChess.DataGen;
using NoaChess.Engine;
using NoaChess.Engine.Search;

// NNUE training data generator: multi-threaded self-play of the current
// engine, labeling every quiet position with the search score and the final
// game result (see DatasetFormat for the binary layout).
//
// Usage:
//   NoaChess.DataGen --games 1000 --nodes 5000 --threads 8 --seed 1 --out data/run1.noadata
//
// Reproducibility: the run parameters, engine commit and dataset hash are
// written to <out>.manifest.json; its SHA-256 is embedded in the file header.

// TEMP debug: `--nnueprobe <model.noannue> "<fen>"` prints the C# NNUE STATIC
// eval of the position (side-to-move POV), to cross-check against the Python
// net's eval on the same FEN.
if (args.Length >= 3 && args[0] == "--nnueprobe")
{
    if (!NoaChess.Engine.Evaluation.Nnue.NnueModelLoader.TryLoad(args[1], out var probeNet, out string loadErr))
    {
        Console.WriteLine($"nnueprobe load error: {loadErr}");
        return 1;
    }
    var probeEval = new NoaChess.Engine.Evaluation.Nnue.NnueEvaluator(probeNet!);
    var probeBoard = new Board(args[2]);
    probeEval.Reset(probeBoard);
    Console.WriteLine($"nnueprobe: {probeEval.Evaluate(probeBoard)}  sha={probeNet!.Sha256[..12]}  fen=[{args[2]}]");
    return 0;
}

// `pgnbook` subcommand: build an opening-seed FEN book from human PGN databases.
if (args.Length >= 1 && args[0] == "pgnbook")
    return PgnBook.Run(args[1..]);

var options = ParseArgs(args);
Console.WriteLine($"datagen: games={options.Games} nodes={options.Nodes} threads={options.Threads} seed={options.Seed}");
Console.WriteLine($"output : {options.Output}");
Console.WriteLine($"limits : resign>=|{options.Resign}|cp/6plies, draw<=|{options.DrawScore}|cp/{options.DrawCount}plies(after ply 60), maxPlies={options.MaxPlies}");
if (options.Model is not null)
    Console.WriteLine($"model  : {options.Model} (self-play uses NNUE instead of the classical evaluator)");

// Optional human-opening seed book (from the pgnbook subcommand): each game
// starts from a random position in it instead of 8-9 random legal plies.
string[]? book = null;
if (options.Book is not null)
{
    book = File.ReadAllLines(options.Book).Where(l => l.Trim().Length > 0).ToArray();
    if (book.Length == 0)
        throw new InvalidOperationException($"Opening book '{options.Book}' is empty.");
    Console.WriteLine($"book   : {options.Book} ({book.Length} seed positions; random openings disabled)");
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);

var stopwatch = Stopwatch.StartNew();
long totalRecords = 0;
int gamesDone = 0;
var writeLock = new object();

using (var stream = new FileStream(options.Output, FileMode.Create, FileAccess.Write))
{
    // Placeholder header; record count and manifest hash are patched at the end.
    DatasetFormat.WriteHeader(stream, 0, stackalloc byte[32]);

    var gameQueue = new ConcurrentQueue<int>(Enumerable.Range(0, options.Games));

    Parallel.For(0, options.Threads, worker =>
    {
        var engine = new ChessEngine();
        if (options.Model is not null)
        {
            if (!engine.TryLoadNnueModel(options.Model, out string error))
                throw new InvalidOperationException($"Failed to load NNUE model '{options.Model}': {error}");
            engine.SetUseNnue(true);
        }
        var buffer = new List<(byte[] Record, Color Stm)>(256);
        var record = new byte[DatasetFormat.RecordSize];

        while (gameQueue.TryDequeue(out int gameIndex))
        {
            var rng = new Random(options.Seed * 1_000_003 + gameIndex);
            engine.NewGame();
            buffer.Clear();

            // Opening seed. With --book: start from a random HUMAN position
            // (realistic and diverse; see the pgnbook subcommand). Without it:
            // 8-9 uniformly random legal plies — variety without a book, but they
            // over-sample junk/unbalanced positions.
            Board board;
            int ply;
            if (book is not null)
            {
                board = new Board(book[rng.Next(book.Length)]);
                if (GameState.GetResult(board) != GameResult.Ongoing)
                    continue;
                ply = 2 * (board.FullmoveNumber - 1) + (board.SideToMove == Color.Black ? 1 : 0);
            }
            else
            {
                board = new Board();
                int openingPlies = 8 + rng.Next(2);
                bool aborted = false;
                for (int i = 0; i < openingPlies; i++)
                {
                    var legal = MoveGenerator.GenerateLegalMoves(board);
                    if (legal.Count == 0) { aborted = true; break; }
                    board.MakeMove(legal[rng.Next(legal.Count)]);
                }
                if (aborted || GameState.GetResult(board) != GameResult.Ongoing)
                    continue;
                ply = openingPlies;
            }

            // Self-play with a fixed node budget per move.
            int whiteResult = 0; // +1 white wins, -1 black wins, 0 draw.
            int decisiveStreak = 0;
            int drawStreak = 0;

            while (ply < options.MaxPlies)
            {
                GameResult state = GameState.GetResult(board);
                if (state != GameResult.Ongoing)
                {
                    whiteResult = state == GameResult.Checkmate
                        ? (board.SideToMove == Color.White ? -1 : 1)
                        : 0;
                    break;
                }

                var result = engine.FindBestMove(board, SearchLimits.Nodes(options.Nodes));
                if (result.BestMove == Move.None)
                    break;

                // Resign adjudication: a stable overwhelming score ends the
                // game early (saves time; matches how matches are run). The
                // threshold is on the active evaluator's scale (see ParseArgs).
                int whiteScore = board.SideToMove == Color.White ? result.Score : -result.Score;
                decisiveStreak = Math.Abs(result.Score) >= options.Resign ? decisiveStreak + 1 : 0;
                if (decisiveStreak >= 6)
                {
                    whiteResult = whiteScore > 0 ? 1 : -1;
                    break;
                }

                // Draw adjudication: once past the opening, a long run of
                // near-zero scores ends the game as a draw. Without it, equal
                // games (which no longer resign) would shuffle to the ply cap,
                // wasting time and over-representing dead-equal positions. The
                // positions themselves are still recorded up to this point.
                drawStreak = Math.Abs(result.Score) <= options.DrawScore ? drawStreak + 1 : 0;
                if (ply >= 60 && drawStreak >= options.DrawCount)
                {
                    whiteResult = 0;
                    break;
                }

                // Record quiet positions only: in-check positions and those
                // whose best move is tactical teach the static evaluator the
                // wrong thing (the search resolves tactics, not the eval).
                bool tactical = result.BestMove.IsCapture || result.BestMove.IsPromotion;
                if (!board.IsInCheck() && !tactical && Math.Abs(result.Score) < 20_000)
                {
                    DatasetFormat.WriteRecord(record, board, ply, result.Score, resultStm: 0);
                    buffer.Add(((byte[])record.Clone(), board.SideToMove));
                }

                board.MakeMove(result.BestMove);
                ply++;
            }

            // Patch the final result into every record (from each record's
            // side to move) and append the game atomically.
            lock (writeLock)
            {
                foreach ((byte[] rec, Color stm) in buffer)
                {
                    int resultStm = stm == Color.White ? whiteResult : -whiteResult;
                    rec[32] = (byte)(sbyte)resultStm;
                    stream.Write(rec);
                }
                totalRecords += buffer.Count;
                int done = ++gamesDone;
                if (done % 50 == 0)
                {
                    double perGame = stopwatch.Elapsed.TotalSeconds / done;
                    Console.WriteLine(
                        $"  {done}/{options.Games} games, {totalRecords:N0} positions, " +
                        $"{perGame:F1}s/game, ETA {(options.Games - done) * perGame / 60:F0} min");
                }
            }
        }
    });
}

// ---- Manifest + header patch ----
string datasetSha;
using (var stream = File.OpenRead(options.Output))
    datasetSha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

var manifest = new
{
    generator = "NoaChess.DataGen",
    formatVersion = DatasetFormat.FormatVersion,
    featureSchemaId = DatasetFormat.FeatureSchemaId,
    games = gamesDone,
    records = totalRecords,
    nodesPerMove = options.Nodes,
    seed = options.Seed,
    openingPlies = options.Book is null ? "8-9 random legal" : $"book:{options.Book}",
    maxPlies = options.MaxPlies,
    filters = "no in-check, no tactical best move, |score| < 20000",
    resignAdjudication = $"|score| >= {options.Resign} for 6 plies",
    drawAdjudication = $"|score| <= {options.DrawScore} for {options.DrawCount} plies after ply 60",
    evaluator = options.Model ?? "classical",
    engineVersion = $"NoaChess {ChessEngine.Version}",
    generatedUtc = DateTime.UtcNow.ToString("o"),
    datasetSha256BeforeHeaderPatch = datasetSha
};
string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
string manifestPath = options.Output + ".manifest.json";
File.WriteAllText(manifestPath, manifestJson);

byte[] manifestSha = SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson));
using (var stream = new FileStream(options.Output, FileMode.Open, FileAccess.Write))
    DatasetFormat.WriteHeader(stream, (ulong)totalRecords, manifestSha);

Console.WriteLine($"done: {gamesDone} games, {totalRecords:N0} positions in {stopwatch.Elapsed.TotalMinutes:F1} min");
Console.WriteLine($"manifest: {manifestPath}");
return 0;

static (int Games, int Nodes, int Threads, int Seed, string Output, string? Model, int Resign, int MaxPlies, int DrawScore, int DrawCount, string? Book) ParseArgs(string[] args)
{
    int games = 500, nodes = 5000, threads = Math.Max(1, Environment.ProcessorCount - 2), seed = 1;
    string output = "data/selfplay.noadata";
    string? model = null;
    string? book = null;
    int resign = int.MinValue; // Sentinel: auto-pick from the evaluator scale below.
    int maxPlies = 400;
    int drawScore = 10;        // Draw adjudication threshold in centipawns.
    int drawCount = 12;        // Consecutive near-zero plies needed to adjudicate.

    for (int i = 0; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--games": games = int.Parse(args[i + 1]); break;
            case "--nodes": nodes = int.Parse(args[i + 1]); break;
            case "--threads": threads = int.Parse(args[i + 1]); break;
            case "--seed": seed = int.Parse(args[i + 1]); break;
            case "--out": output = args[i + 1]; break;
            case "--model": model = args[i + 1]; break;
            case "--book": book = args[i + 1]; break;
            case "--resign": resign = int.Parse(args[i + 1]); break;
            case "--maxplies": maxPlies = int.Parse(args[i + 1]); break;
            case "--drawscore": drawScore = int.Parse(args[i + 1]); break;
            case "--drawcount": drawCount = int.Parse(args[i + 1]); break;
        }
    }

    // The resign threshold is a centipawn score, so it lives on the active
    // evaluator's scale. The classical evaluator reaches ±1500 readily; an
    // NNUE model's output is sigmoid-trained and compressed (~0.5-0.6x
    // classical, saturating further at the extremes), so the same 1500 almost
    // never triggers and games run to the ply cap — datagen with an NNUE
    // teacher then takes many times longer for no extra data quality. Default
    // to a scale-appropriate value per evaluator; --resign overrides.
    if (resign == int.MinValue)
        resign = model is null ? 1500 : 700;

    return (games, nodes, threads, seed, output, model, resign, maxPlies, drawScore, drawCount, book);
}
