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

// `corpus` subcommand: audit a directory of shards before training on them.
if (args.Length >= 1 && args[0] == "corpus")
    return Corpus.Run(args[1..]);

// A mistyped subcommand used to fall straight through into the datagen and
// start a 500-game run against the DEFAULT output path - which is exactly what
// happened when `nnueprobe` was typed instead of `--nnueprobe`. Options always
// begin with "--", so a bare first word that is not a known subcommand is a
// typo, and a typo must not launch hours of work.
if (args.Length >= 1 && !args[0].StartsWith("--"))
{
    Console.Error.WriteLine($"datagen: unknown subcommand '{args[0]}'.");
    Console.Error.WriteLine("         Known subcommands: pgnbook, corpus.");
    Console.Error.WriteLine("         Options start with '--' (e.g. --games, --nodes, --out).");
    Console.Error.WriteLine("         Refusing to start a default datagen run from a typo.");
    return 2;
}

var options = ParseArgs(args);
Console.WriteLine($"datagen: games={options.Games} nodes={options.Nodes} threads={options.Threads} seed={options.Seed}");
Console.WriteLine($"output : {options.Output}");
Console.WriteLine($"limits : resign>=|{options.Resign}|cp/6plies, draw<=|{options.DrawScore}|cp/{options.DrawCount}plies(after ply 60), maxPlies={options.MaxPlies}");
if (options.Model is not null)
    Console.WriteLine($"model  : {options.Model} (self-play uses NNUE instead of the classical evaluator)");

// ---- PROVENANCE GATE (v4.0.0) ----
//
// Blocks 7-8 spent five generations and a shipped version believing the datagen
// was seeded from a human opening book. Every manifest on disk says
// "8-9 random legal": the pipeline was correct and the book existed, but the
// -Book argument was never passed, and NOTHING complained. The resulting
// conclusion ("pure self-play is exhausted") reached the ROADMAP, the README
// and the release notes as established fact.
//
// --require-book turns the operator's INTENT into a checked precondition: a
// pipeline that means to seed from a book says so, and a run that would have
// silently produced random openings dies here instead of 13 hours later.
if (options.RequireBook && options.Book is null && options.LabelBook is null)
{
    Console.Error.WriteLine(
        "datagen: --require-book was given but no --book/--label-book was supplied.\n"
      + "         This run would have produced RANDOM-OPENING data while the\n"
      + "         pipeline reported book seeding. Refusing to start.");
    return 2;
}

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

// Stated once, unmissably, at the top of every run and every log. The failure
// this guards against was invisible precisely because provenance was never
// printed where an operator would see it.
Console.WriteLine(options.LabelBook is not null
    ? $"PROVENANCE: label-book (elite WDL anchoring) from '{options.LabelBook}'"
    : options.Book is not null
        ? $"PROVENANCE: self-play seeded from book '{options.Book}'"
        : "PROVENANCE: self-play seeded from 8-9 RANDOM LEGAL PLIES (no book). "
          + "If you meant to seed from a book, stop now and pass --book.");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);

var stopwatch = Stopwatch.StartNew();
long totalRecords = 0;
int gamesDone = 0;
var writeLock = new object();

// Sharding (v4.1.0): a multi-day run must not be able to lose everything to one
// crash. See ShardWriter. --shard-size 0 keeps the classic single-file layout.
int startShard = options.Resume ? ShardWriter.CountCompletedShards(options.Output) : 0;
if (options.Resume)
    Console.WriteLine($"resume : {startShard} completed shard(s) already on disk; continuing from {startShard:D4}");
if (options.ShardSize > 0)
    Console.WriteLine($"shards : {options.ShardSize:N0} records each"
                    + (options.TargetPositions > 0 ? $", target {options.TargetPositions:N0} positions" : ""));

using (var shards = new ShardWriter(options.Output, options.ShardSize, startShard,
           (index, records, sha) => BuildManifest(options, index, records, sha, gamesDone)))
{

    // ---- Elite-game WDL anchoring (--label-book) ----
    // A fundamentally different data source from self-play: instead of playing
    // games ourselves and labelling with our own outcome, we take positions that
    // REAL strong players reached and label them with (our deep search score,
    // THEIR game result). The result is the only signal in the whole pipeline
    // that the engine cannot manufacture for itself - self-play WDL is just the
    // engine's own opinion played out, which is why the gen3-era lambda sweep
    // found it worthless (lambda 0.750 -> score 0.338). This is not imitation
    // learning: the human never supplies an evaluation or a move, only the
    // position and who eventually won.
    if (options.LabelBook is not null)
    {
        string[] lines = File.ReadAllLines(options.LabelBook)
                             .Where(l => l.Trim().Length > 0).ToArray();
        Console.WriteLine($"label-book: {options.LabelBook} ({lines.Length:N0} positions, {options.Nodes} nodes each)");

        var lineQueue = new ConcurrentQueue<string>(lines);
        int labelled = 0, skipped = 0;

        Parallel.For(0, options.Threads, _ =>
        {
            var engine = new ChessEngine();
            if (options.Model is not null)
            {
                if (!engine.TryLoadNnueModel(options.Model, out string error))
                    throw new InvalidOperationException($"Failed to load NNUE model '{options.Model}': {error}");
                engine.SetUseNnue(true);
            }
            var record = new byte[DatasetFormat.RecordSize];
            var batch = new List<byte[]>(256);

            while (lineQueue.TryDequeue(out string? line))
            {
                // "FEN;R" as written by `pgnbook --with-result`; R is from White.
                int sep = line.LastIndexOf(';');
                if (sep <= 0) { Interlocked.Increment(ref skipped); continue; }
                if (!int.TryParse(line[(sep + 1)..].Trim(), out int whiteResult)
                    || whiteResult is < -1 or > 1)
                {
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                Board board;
                try { board = new Board(line[..sep].Trim()); }
                catch { Interlocked.Increment(ref skipped); continue; }
                if (GameState.GetResult(board) != GameResult.Ongoing)
                {
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                engine.NewGame();
                SearchResult search = engine.FindBestMove(board, SearchLimits.Nodes(options.Nodes));

                // Same quiet-position filter as the self-play path: a static
                // evaluator must not be taught positions whose value comes from
                // a tactic the SEARCH resolves.
                bool tactical = search.BestMove.IsCapture || search.BestMove.IsPromotion;
                if (board.IsInCheck() || tactical || Math.Abs(search.Score) >= 20_000)
                {
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                int ply = 2 * (board.FullmoveNumber - 1) + (board.SideToMove == Color.Black ? 1 : 0);
                int resultStm = board.SideToMove == Color.White ? whiteResult : -whiteResult;
                DatasetFormat.WriteRecord(record, board, ply, search.Score, resultStm);
                batch.Add((byte[])record.Clone());

                if (batch.Count >= 256)
                    FlushBatch(batch);
            }
            if (batch.Count > 0)
                FlushBatch(batch);

            void FlushBatch(List<byte[]> pending)
            {
                lock (writeLock)
                {
                    foreach (byte[] rec in pending)
                        shards.Write(rec);
                    // Label-book positions are independent, so a shard boundary
                    // can fall anywhere; self-play rolls between games instead.
                    shards.RollIfNeeded();
                    totalRecords = shards.TotalRecords;
                    int done = labelled += pending.Count;
                    if (done % 5000 < pending.Count)
                    {
                        double perPos = stopwatch.Elapsed.TotalSeconds / Math.Max(1, done);
                        Console.WriteLine($"  {done:N0} labelled, {skipped:N0} skipped, "
                                        + $"ETA {(lines.Length - done) * perPos / 60:F0} min");
                    }
                }
                pending.Clear();
            }
        });

        Console.WriteLine($"label-book done: labelled={labelled:N0} skipped={skipped:N0}");
    }
    // A resumed run already at its target has nothing to do; say so rather than
    // play one more game to discover it.
    else if (options.TargetPositions > 0 && shards.TotalRecords >= options.TargetPositions)
    {
        Console.WriteLine($"target already met: {shards.TotalRecords:N0} >= "
                        + $"{options.TargetPositions:N0} positions; nothing to generate");
    }
    else
    {

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
            // 8-9 uniformly random legal plies - variety without a book, but they
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
                    shards.Write(rec);
                }
                // Rolled here, between games, so a game's positions never span
                // two shards: records are ordered by game and the train/val tail
                // cut relies on whole games staying on one side.
                shards.RollIfNeeded();
                totalRecords = shards.TotalRecords;
                int done = ++gamesDone;
                if (done % 50 == 0)
                {
                    double perGame = stopwatch.Elapsed.TotalSeconds / done;

                    // The rate is THIS run's records over THIS run's clock.
                    // Dividing the elapsed time by the whole corpus credited the
                    // resumed millions to a stopwatch that never timed them, so a
                    // resumed run opened with an absurdly fast rate and the
                    // estimate CLIMBED all day as the lie wore off.
                    long producedHere = totalRecords - shards.ResumedRecords;
                    double perRecord = stopwatch.Elapsed.TotalSeconds / Math.Max(1, producedHere);

                    string progress = options.TargetPositions > 0
                        ? $"{totalRecords:N0}/{options.TargetPositions:N0} positions, "
                          + $"ETA {(options.TargetPositions - totalRecords) * perRecord / 3600:F1} h"
                        : $"{done}/{options.Games} games, {totalRecords:N0} positions, "
                          + $"ETA {(options.Games - done) * perGame / 60:F0} min";
                    Console.WriteLine($"  {progress}, {perGame:F1}s/game");
                }
            }

            // --positions target: stop as soon as enough data exists. Wanting N
            // positions is the natural way to size a corpus; --games only
            // approximates it, and badly, because game length varies with the
            // node budget and the opening source.
            if (options.TargetPositions > 0 && shards.TotalRecords >= options.TargetPositions)
            {
                while (gameQueue.TryDequeue(out _)) { }
                break;
            }
        }
    });

    } // end of the self-play branch (see --label-book above)

    totalRecords = shards.TotalRecords;
} // ShardWriter.Dispose finalizes the shard still open

Console.WriteLine($"done: {gamesDone} games, {totalRecords:N0} positions in {stopwatch.Elapsed.TotalMinutes:F1} min");
return 0;

// Per-shard manifest. Every shard carries the FULL provenance of the run, so a
// corpus assembled from many shards (possibly across several sessions and
// several sources) can always be audited file by file. This is the machine-
// checkable half of the provenance rule: the manifest is what proves what went
// in, and it is written per shard precisely so a partial corpus cannot lie.
static object BuildManifest(
    (int Games, int Nodes, int Threads, int Seed, string Output, string? Model, int Resign,
     int MaxPlies, int DrawScore, int DrawCount, string? Book, string? LabelBook,
     bool RequireBook, long ShardSize, long TargetPositions, bool Resume) options,
    int shardIndex, long records, string datasetSha, int gamesDone) => new
{
    generator = "NoaChess.DataGen",
    formatVersion = DatasetFormat.FormatVersion,
    featureSchemaId = DatasetFormat.FeatureSchemaId,
    shardIndex,
    // The data SOURCE is the first thing to check when a net misbehaves, so it
    // is recorded explicitly: self-play games, or elite positions labelled with
    // their real game result (--label-book), which is a different distribution
    // AND a different WDL signal.
    mode = options.LabelBook is null ? "selfplay" : "label-book (elite WDL anchoring)",
    games = gamesDone,
    records,
    nodesPerMove = options.Nodes,
    seed = options.Seed,
    openingPlies = options.LabelBook is not null ? $"label-book:{options.LabelBook}"
                 : options.Book is null ? "8-9 random legal" : $"book:{options.Book}",
    maxPlies = options.MaxPlies,
    filters = "no in-check, no tactical best move, |score| < 20000",
    wdlSource = options.LabelBook is null ? "self-play game outcome"
                                          : "real elite game outcome (external signal)",
    resignAdjudication = options.LabelBook is null
        ? $"|score| >= {options.Resign} for 6 plies" : "n/a (no self-play games)",
    drawAdjudication = options.LabelBook is null
        ? $"|score| <= {options.DrawScore} for {options.DrawCount} plies after ply 60" : "n/a",
    evaluator = options.Model ?? "classical",
    engineVersion = $"NoaChess {ChessEngine.Version}",
    generatedUtc = DateTime.UtcNow.ToString("o"),
    datasetSha256BeforeHeaderPatch = datasetSha
};

static (int Games, int Nodes, int Threads, int Seed, string Output, string? Model, int Resign, int MaxPlies, int DrawScore, int DrawCount, string? Book, string? LabelBook, bool RequireBook, long ShardSize, long TargetPositions, bool Resume) ParseArgs(string[] args)
{
    int games = 500, nodes = 5000, threads = Math.Max(1, Environment.ProcessorCount - 2), seed = 1;
    string output = "data/selfplay.noadata";
    string? model = null;
    string? book = null;
    string? labelBook = null;
    // Provenance gate: assert that this run is book-seeded. See the check at
    // the top of the file for why an unchecked intent is not good enough.
    bool requireBook = false;
    // Sharding: 0 keeps the classic single output file. A multi-day run should
    // always set this - see ShardWriter for why.
    long shardSize = 0;
    // Stop on a POSITION count rather than a game count. Corpus size is what is
    // actually being specified; --games only approximates it and game length
    // varies with node budget and opening source.
    long targetPositions = 0;
    bool resume = false;
    int resign = int.MinValue; // Sentinel: auto-pick from the evaluator scale below.
    int maxPlies = 400;
    int drawScore = 10;        // Draw adjudication threshold in centipawns.
    int drawCount = 12;        // Consecutive near-zero plies needed to adjudicate.

    // Valueless flags are scanned over the whole array; the value-pair loop
    // below stops one short and would miss a flag in last position.
    bool requireBookFlag = args.Contains("--require-book");
    bool resumeFlag = args.Contains("--resume");

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
            case "--label-book": labelBook = args[i + 1]; break;
            case "--resign": resign = int.Parse(args[i + 1]); break;
            case "--maxplies": maxPlies = int.Parse(args[i + 1]); break;
            case "--drawscore": drawScore = int.Parse(args[i + 1]); break;
            case "--drawcount": drawCount = int.Parse(args[i + 1]); break;
            case "--shard-size": shardSize = long.Parse(args[i + 1]); break;
            case "--positions": targetPositions = long.Parse(args[i + 1]); break;
        }
    }

    // The resign threshold is a centipawn score, so it lives on the active
    // evaluator's scale. The classical evaluator reaches ±1500 readily; an
    // NNUE model's output is sigmoid-trained and compressed (~0.5-0.6x
    // classical, saturating further at the extremes), so the same 1500 almost
    // never triggers and games run to the ply cap - datagen with an NNUE
    // teacher then takes many times longer for no extra data quality. Default
    // to a scale-appropriate value per evaluator; --resign overrides.
    if (resign == int.MinValue)
        resign = model is null ? 1500 : 700;

    // A position target needs enough games queued to reach it; the loop stops on
    // the target, so overshooting the queue costs nothing but undershooting it
    // silently caps the corpus. ~60 positions per game at these node counts.
    if (targetPositions > 0)
        games = Math.Max(games, (int)Math.Min(int.MaxValue, targetPositions / 20));

    return (games, nodes, threads, seed, output, model, resign, maxPlies, drawScore, drawCount,
            book, labelBook, requireBook || requireBookFlag, shardSize, targetPositions,
            resume || resumeFlag);
}
