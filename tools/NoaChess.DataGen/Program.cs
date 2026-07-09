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

var options = ParseArgs(args);
Console.WriteLine($"datagen: games={options.Games} nodes={options.Nodes} threads={options.Threads} seed={options.Seed}");
Console.WriteLine($"output : {options.Output}");
if (options.Model is not null)
    Console.WriteLine($"model  : {options.Model} (self-play uses NNUE instead of the classical evaluator)");

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

            var board = new Board();

            // Random opening: 8-9 uniformly random legal plies give opening
            // variety without a book (standard practice for datagen).
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

            // Self-play with a fixed node budget per move.
            int whiteResult = 0; // +1 white wins, -1 black wins, 0 draw.
            int ply = openingPlies;
            int decisiveStreak = 0;

            while (ply < 400)
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
                // game early (saves time; matches how matches are run).
                int whiteScore = board.SideToMove == Color.White ? result.Score : -result.Score;
                decisiveStreak = Math.Abs(result.Score) >= 1500 ? decisiveStreak + 1 : 0;
                if (decisiveStreak >= 6)
                {
                    whiteResult = whiteScore > 0 ? 1 : -1;
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
    openingPlies = "8-9 random legal",
    filters = "no in-check, no tactical best move, |score| < 20000",
    resignAdjudication = "|score| >= 1500 for 6 plies",
    evaluator = options.Model ?? "classical",
    engineVersion = "NoaChess 2.3.0",
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

static (int Games, int Nodes, int Threads, int Seed, string Output, string? Model) ParseArgs(string[] args)
{
    int games = 500, nodes = 5000, threads = Math.Max(1, Environment.ProcessorCount - 2), seed = 1;
    string output = "data/selfplay.noadata";
    string? model = null;

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
        }
    }
    return (games, nodes, threads, seed, output, model);
}
