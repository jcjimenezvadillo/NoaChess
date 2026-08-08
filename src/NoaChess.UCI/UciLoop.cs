using System.Diagnostics;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Profiles;
using NoaChess.Engine.Search;
using NoaChess.Engine.TimeManagement;
using NoaChess.UCI.Options;

namespace NoaChess.UCI;

// UCI loop. UCI (Universal Chess Interface) is the standard text protocol any
// chess GUI (Arena, CuteChess, Fritz...) uses to talk to an engine: the GUI
// writes commands to stdin and the engine replies on stdout.
//
// v1.0 implements the full basic protocol:
//   uci / isready / ucinewgame / setoption / position / go / stop / quit
//
// Threading model: "go" launches the search on a background task and the loop
// keeps reading stdin, so "stop" and "isready" are answered WHILE searching
// (a GUI that gets no "readyok" mid-search declares the engine dead). Output
// is synchronized because both the loop thread and the search task write.
public sealed class UciLoop
{
    // Single source of truth for the engine identity (banner + "id" reply).
    public const string EngineName = "NoaChess";
    public const string EngineVersion = ChessEngine.Version;
    public const string EngineAuthor = "Juan Carlos Jimenez Vadillo";

    private readonly TextReader _input;
    private TextWriter _output;
    private readonly ChessEngine _engine = new();
    private readonly UciOptions _options = new();

    // Optional UCI traffic log ("Debug Log File"): every stdin line ("<<"),
    // every stdout line (">>") and the stdin EOF, timestamped. It is never
    // enabled by an environment variable, so an inherited machine setting
    // cannot silently create an unbounded log in Arena or lichess-bot.
    private StreamWriter? _log;
    private readonly Lock _logLock = new();

    private Board _board = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;
    private bool _embeddedNnueChecked;

    // Pondering state: while thinking on the opponent's time, the original
    // "go ponder ..." tokens are kept so a "ponderhit" can relaunch the same
    // search with the real clock limits. _suppressBestmove silences the
    // aborted ponder search (UCI forbids a bestmove between ponderhit and
    // the timed search's own answer). _ponderTimer measures how long the
    // ponder ran: the relaunched search charges that time against its budget
    // (the reference anchors its clock at "go ponder"), so a long successful
    // ponder answers almost instantly instead of re-spending the whole
    // optimum over the warm TT.
    private string[]? _pendingPonderTokens;
    private readonly Stopwatch _ponderTimer = new();
    private volatile bool _suppressBestmove;

    private readonly QueuedWriter _queuedOutput;

    public UciLoop(TextReader input, TextWriter output)
    {
        _input = input;
        // All output goes through ONE background writer thread via a queue.
        // Both producers (the command loop AND the search task) only ENQUEUE,
        // which never blocks. This is what prevents the classic UCI pipe
        // deadlock: with a shared blocking writer, a full stdout pipe (GUI slow
        // to read under fast TC / high concurrency) stalls whichever thread
        // holds the write lock; if that is the command loop it stops reading
        // stdin, the GUI then blocks writing its next command, and neither side
        // ever drains the other. Queuing decouples the loop from the actual
        // write so it keeps reading stdin no matter what.
        _queuedOutput = new QueuedWriter(output, this);
        _output = _queuedOutput;
    }

    // Loads the .noannue model compiled into the exe as an embedded resource
    // (LogicalName "noa-embedded.noannue"). Called on the first "isready" only
    // when EvalFile has not been set by the GUI. If the resource is absent the
    // method is a no-op (pre-training builds ship without a model).
    private void TryLoadEmbeddedNnue()
    {
        using Stream? stream = typeof(UciLoop).Assembly
                                              .GetManifestResourceStream("noa-embedded.noannue");
        if (stream is null) return;

        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        if (_engine.TryLoadNnueModel(bytes.AsSpan(), out string error))
        {
            _output.WriteLine($"info string NNUE embedded model loaded ({_engine.NnueModelSha256})");
            // Which SIMD path the evaluator will actually take. The engine
            // branches on Avx2.IsSupported in seven places and never said so,
            // which makes a whole class of problem invisible: the lichess bot
            // runs the osx-x64 build under Rosetta, where AVX2 does not exist,
            // so it silently takes the scalar path - a path that is known NOT
            // to be bit-identical to the AVX2 one. Two machines can therefore
            // play different moves from the same position with no way to tell
            // from any log. One line at startup makes it obvious forever.
            _output.WriteLine("info string NNUE SIMD path: "
                            + (System.Runtime.Intrinsics.X86.Avx2.IsSupported
                               ? "AVX2" : "scalar (no AVX2 on this machine)"));
            // Auto-enable unless the GUI sent "setoption name UseNNUE value false" first.
            if (!_options.UseNnueExplicitlySet || _options.UseNnue)
                _engine.SetUseNnue(true);
        }
        else
        {
            _output.WriteLine($"info string embedded NNUE model rejected: {error}");
        }
    }

    // Opens (or switches) the traffic log and tees stdout through it. The new
    // writer is opened before replacing the old one, so a bad path cannot leave
    // _log pointing at a disposed writer.
    private void OpenLog(string path)
    {
        StreamWriter? next = null;
        try
        {
            next = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write,
                                              FileShare.ReadWrite))
            { AutoFlush = true };
            next.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] === log opened (pid {Environment.ProcessId}, {EngineName} {EngineVersion}) ===");

            lock (_logLock)
            {
                StreamWriter? previous = _log;
                _log = next;
                next = null;
                try { previous?.Dispose(); }
                catch { /* Logging must never break the UCI loop. */ }
            }
            // Output already flows through QueuedWriter, which logs ">>" lines
            // as it enqueues them, so no extra tee is needed once _log is set.
        }
        catch (Exception ex)
        {
            try { next?.Dispose(); }
            catch { /* Best effort only. */ }
            _output.WriteLine($"info string debug log rejected: {ex.Message}");
        }
    }

    private void CloseLog()
    {
        StreamWriter? current;
        lock (_logLock)
        {
            current = _log;
            _log = null;
        }

        if (current is null)
            return;

        try { current.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] === log closed ==="); }
        catch { /* Best effort only. */ }
        try { current.Dispose(); }
        catch { /* Best effort only. */ }
    }
    private void LogLine(string direction, string text)
    {
        lock (_logLock)
        {
            try
            {
                _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {direction} {text}");
            }
            catch
            {
                // A full disk, disconnected drive or revoked permission must
                // disable diagnostics, never terminate an engine game.
                StreamWriter? failed = _log;
                _log = null;
                try { failed?.Dispose(); }
                catch { /* Best effort only. */ }
            }
        }
    }
    // Single-writer output queue. Every engine->GUI line is enqueued (a
    // non-blocking operation) and logged; a dedicated background thread is the
    // ONLY thing that ever writes to the real stdout, so no producer thread can
    // ever block on a full pipe. Line order is preserved (FIFO, one consumer).
    private sealed class QueuedWriter : TextWriter
    {
        private readonly TextWriter _sink;
        private readonly UciLoop _owner;
        private readonly System.Collections.Concurrent.BlockingCollection<string> _queue = new();
        private readonly Thread _pump;

        public QueuedWriter(TextWriter sink, UciLoop owner)
        {
            _sink = sink;
            _owner = owner;
            _pump = new Thread(Pump) { IsBackground = true, Name = "NoaUciOutput" };
            _pump.Start();
        }

        public override System.Text.Encoding Encoding => _sink.Encoding;

        public override void WriteLine(string? value)
        {
            string line = value ?? "";
            _owner.LogLine(">>", line); // log at enqueue time (logical order, non-blocking)
            try { _queue.Add(line); }
            catch (InvalidOperationException) { /* queue completed at shutdown */ }
        }

        private void Pump()
        {
            try
            {
                foreach (string line in _queue.GetConsumingEnumerable())
                {
                    // The ONLY place a full stdout can block - and it blocks only
                    // this pump thread, never a producer.
                    try { _sink.WriteLine(line); _sink.Flush(); }
                    catch { /* GUI gone: keep draining so producers never block */ }
                }
            }
            catch { /* CompleteAdding raced with GetConsumingEnumerable */ }
        }

        // Flush remaining lines (e.g. the final bestmove) at shutdown.
        public void CompleteAndDrain(int millis)
        {
            try { _queue.CompleteAdding(); } catch { }
            try { _pump.Join(millis); } catch { }
        }
    }

    public void Run()
    {
        try
        {
            string? line;
            bool quitReceived = false;
            while ((line = _input.ReadLine()) != null)
            {
                LogLine("<<", line);
                string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;

                // One bad command (e.g. a malformed FEN) must not kill the read
                // loop: report it and keep serving the GUI.
                try
                {
                    if (!Dispatch(tokens))
                    {
                        quitReceived = true;
                        LogLine("--", "quit received - read loop ends");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"info string command error: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (!quitReceived)
                LogLine("--", "stdin EOF - read loop ends");
        }
        finally
        {
            _queuedOutput.CompleteAndDrain(2000); // flush the final bestmove
            CloseLog();
        }
    }
    // Executes one UCI command. Returns false when the loop must exit (quit).
    private bool Dispatch(string[] tokens)
    {
        switch (tokens[0])
        {
                case "uci":
                    // Identification + option declarations + end of handshake.
                    _output.WriteLine($"id name {EngineName} {EngineVersion}");
                    _output.WriteLine($"id author {EngineAuthor}");
                    _options.Print(_output);
                    _output.WriteLine("uciok");
                    break;

                case "isready":
                    // Must answer even while a search runs - the GUI uses it
                    // as a heartbeat. The loop thread is free, so just reply.
                    if (!_embeddedNnueChecked)
                    {
                        _embeddedNnueChecked = true;
                        if (_options.EvalFile.Length == 0)
                            TryLoadEmbeddedNnue();
                    }
                    _output.WriteLine("readyok");
                    break;

                case "setoption":
                    HandleSetOption(tokens);
                    break;

                case "tbprobe":
                {
                    // Not UCI: prints the tablebase verdict for the current
                    // position. Drives the differential test harness.
                    bool okW = NoaChess.Engine.Tablebases.Syzygy.ProbeWdl(
                        _board, out var wdlScore);
                    bool okD = NoaChess.Engine.Tablebases.Syzygy.ProbeDtz(
                        _board, out int dtzScore);
                    _output.WriteLine($"tbresult wdl {(okW ? ((int)wdlScore).ToString() : "none")}"
                                    + $" dtz {(okD ? dtzScore.ToString() : "none")}");
                    break;
                }

                case "nnueprofile":
                {
                    // Not UCI: measures where NNUE evaluation time actually
                    // goes (v4.0.0 foundation gate). The v4.2.0 width decision
                    // must rest on this instead of on intuition - the previous
                    // decision not to widen rested on a cost model that does
                    // not survive arithmetic. Forces single-threaded search so
                    // the unsynchronised counters stay meaningful.
                    WaitForSearchToFinish(suppressBestmove: true);
                    HandleNnueProfile(tokens);
                    break;
                }

                case "nnuewidth":
                {
                    // Not UCI: measures what each feature-transformer width
                    // COSTS, using shape-accurate synthetic nets so no training
                    // is needed. This is the v4.2.0 gate input - the width
                    // decision must rest on measurement, and previously the
                    // measurement did not exist. Run on an idle machine.
                    WaitForSearchToFinish(suppressBestmove: true);
                    HandleNnueWidth(tokens);
                    break;
                }

                case "ucinewgame":
                    WaitForSearchToFinish(suppressBestmove: true);
                    _board = new Board();
                    _engine.NewGame(); // Clear TT/heuristics from the previous game.
                    break;

                case "position":
                    WaitForSearchToFinish(suppressBestmove: true);
                    HandlePosition(tokens);
                    break;

                case "go":
                    WaitForSearchToFinish();
                    HandleGo(tokens);
                    break;

                case "stop":
                    // Cancel the running search; its task still prints the
                    // "bestmove" (the best of the last completed iteration).
                    _pendingPonderTokens = null;
                    _searchCts?.Cancel();
                    break;

                case "ponderhit":
                    // The opponent played the predicted move: everything the
                    // ponder search stored in the TT is valid. Silently stop
                    // it and relaunch as a normal timed search - the warm TT
                    // makes the early iterations nearly free, and the time
                    // already pondered is charged against the new budget so
                    // a long ponder answers almost instantly.
                    if (_pendingPonderTokens is string[] goTokens)
                    {
                        long ponderedMs = _ponderTimer.ElapsedMilliseconds;
                        _pendingPonderTokens = null;
                        WaitForSearchToFinish(suppressBestmove: true);
                        HandleGo(goTokens.Where(t => t != "ponder").ToArray(), ponderedMs);
                    }
                    break;

                case "quit":
                    _searchCts?.Cancel();
                    try { _searchTask?.Wait(TimeSpan.FromSeconds(2)); }
                    catch (AggregateException) { }
                    return false;

                // Unknown commands are silently ignored, as UCI mandates.
            }

        return true;
    }

    // Cancels and joins any running search. Called before commands that touch
    // the board or the engine: a search still running would race with them.
    // 'suppressBestmove' silences the aborted search's answer - used when the
    // GUI moved on (new position / new game / ponderhit) and a late bestmove
    // would be misattributed to the new context.
    private void WaitForSearchToFinish(bool suppressBestmove = false)
    {
        if (suppressBestmove)
            _suppressBestmove = true;
        if (_searchTask is { IsCompleted: false })
            LogLine("--", $"waiting for search task (suppress={suppressBestmove})");
        _searchCts?.Cancel();
        // A faulted search task re-throws its exception here, on the UCI loop
        // thread - which would kill the read loop and leave a zombie process
        // (alive but deaf; Arena's Ctrl+N new game shows exactly this). The
        // search already reported the failure; the loop must survive it.
        //
        // The wait is UNBOUNDED by design (proceeding while a search still runs
        // would break ChessEngine's "one search at a time" contract), but a
        // search that ignored cancellation would hang the command loop with no
        // trace at all - under lichess-bot, with concurrency 1, that silently
        // ends the bot's night. Report every stalled second so the failure is
        // diagnosable from the GUI/bot log instead of looking like a freeze.
        try
        {
            if (_searchTask is { } task)
            {
                for (int waitedSeconds = 0; !task.Wait(TimeSpan.FromSeconds(1)); waitedSeconds++)
                {
                    _output.WriteLine("info string search still stopping after "
                                    + $"{waitedSeconds + 1}s - cancellation not honoured");
                    LogLine("--", $"STALL: search task not finishing after {waitedSeconds + 1}s");
                }
            }
        }
        catch (AggregateException) { }
        _searchTask = null;
        _suppressBestmove = false;
    }

    // "nnueprofile [depth]" - not UCI. Prints the NNUE cost breakdown that the
    // v4.0.0 gate is defined against. Runs single-threaded: the profiling
    // counters are deliberately unsynchronised, and a parallel search would
    // both corrupt them and blur the per-primitive attribution.
    private void HandleNnueProfile(string[] tokens)
    {
        var network = _engine.NnueNetwork;
        if (network is null)
        {
            _output.WriteLine("info string nnueprofile: no NNUE model loaded");
            return;
        }

        int depth = 8;
        if (tokens.Length > 1 && int.TryParse(tokens[1], out int requested) && requested > 0)
            depth = Math.Min(requested, 20);

        int savedThreads = _engine.Threads;
        bool savedNnue = _engine.NnueActive;
        try
        {
            _engine.Threads = 1;
            if (!savedNnue)
                _engine.SetUseNnue(true);

            string report = NoaChess.Engine.Evaluation.Nnue.NnueProfiler.Run(
                network,
                (board, d) =>
                {
                    _engine.NewGame(); // cold tables, so counts are comparable
                    return _engine.FindBestMove(board, depth: d).NodesSearched;
                },
                depth);

            foreach (string line in report.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                    _output.WriteLine("info string " + trimmed);
            }
        }
        finally
        {
            _engine.Threads = savedThreads;
            if (!savedNnue)
                _engine.SetUseNnue(false);
            NoaChess.Engine.Evaluation.Nnue.NnueProfiling.Enabled = false;
            _engine.NewGame();
        }
    }

    // "nnuewidth [w1,w2,...] [l1] [buckets]" - not UCI. Defaults sweep the
    // widths BLOCK 12 is choosing between.
    private void HandleNnueWidth(string[] tokens)
    {
        int[] widths = [128, 256, 512, 1024];
        int l1 = 32;
        int buckets = 8;

        if (tokens.Length > 1)
        {
            var parsed = tokens[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(t => int.TryParse(t, out int w) ? w : 0)
                                  .Where(w => w > 0 && w % 32 == 0 && w <= 4096)
                                  .ToArray();
            if (parsed.Length > 0)
                widths = parsed;
        }
        if (tokens.Length > 2 && int.TryParse(tokens[2], out int parsedL1) && parsedL1 > 0)
            l1 = parsedL1;
        if (tokens.Length > 3 && int.TryParse(tokens[3], out int parsedBuckets) && parsedBuckets > 0)
            buckets = parsedBuckets;

        string report = NoaChess.Engine.Evaluation.Nnue.NnueProfiler.RunWidthSweep(widths, l1, buckets);
        foreach (string line in report.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
                _output.WriteLine("info string " + trimmed);
        }
    }

    // "setoption name <name...> value <value...>". The name may contain
    // spaces, so everything between "name" and "value" is the name.
    private void HandleSetOption(string[] tokens)
    {
        int nameIndex = Array.IndexOf(tokens, "name");
        int valueIndex = Array.IndexOf(tokens, "value");
        if (nameIndex == -1)
            return;

        int nameEnd = valueIndex == -1 ? tokens.Length : valueIndex;
        string name = string.Join(' ', tokens[(nameIndex + 1)..nameEnd]);
        string value = valueIndex == -1 ? "" : string.Join(' ', tokens[(valueIndex + 1)..]);

        string? changed = _options.Set(name, value);

        // Options that require engine-side action.
        if (changed == "Hash")
            _engine.ResizeHash(_options.Hash);
        if (changed == "Threads")
            _engine.Threads = _options.Threads;
        if (changed == "Profile")
            _engine.Profile = EngineProfile.ByName(_options.Profile);
        if (changed is "SyzygyProbeLimit" or "SyzygyProbeDepth" or "Syzygy50MoveRule")
        {
            _engine.SyzygyProbeLimit = _options.SyzygyProbeLimit;
            _engine.SyzygyProbeDepth = _options.SyzygyProbeDepth;
            _engine.Syzygy50MoveRule = _options.Syzygy50MoveRule;
        }
        if (changed == "SyzygyPath")
        {
            NoaChess.Engine.Tablebases.Syzygy.Init(_options.SyzygyPath);
            _engine.RefreshTablebaseLimit();
            _output.WriteLine(NoaChess.Engine.Tablebases.Syzygy.Available
                ? $"info string Syzygy: {NoaChess.Engine.Tablebases.Syzygy.Cardinality}-man tablebases loaded"
                : "info string Syzygy: no tablebases found");
        }
        if (changed == "Debug Log File")
        {
            if (_options.DebugLogFile.Length > 0)
                OpenLog(_options.DebugLogFile);
            else
                CloseLog();
        }

        // NNUE wiring: EvalFile loads/validates the model; UseNNUE switches
        // the evaluator. Failures are reported as "info string" (per UCI, a
        // bad option must not kill the engine) and the classical evaluator
        // stays in charge.
        if (changed == "EvalFile" && _options.EvalFile.Length > 0)
        {
            if (_engine.TryLoadNnueModel(_options.EvalFile, out string loadError))
            {
                _output.WriteLine($"info string NNUE model loaded ({_engine.NnueModelSha256})");
                if (_options.UseNnue)
                    _engine.SetUseNnue(true);
            }
            else
            {
                _output.WriteLine($"info string NNUE model rejected: {loadError}");
            }
        }
        if (changed == "UseNNUE")
        {
            if (!_engine.SetUseNnue(_options.UseNnue) && _options.UseNnue)
            {
                // Suppress the error when isready hasn't arrived yet: the embedded model
                // loads on the first isready and will pick up _options.UseNnue automatically.
                if (_embeddedNnueChecked)
                    _output.WriteLine("info string UseNNUE ignored: no valid model loaded (set EvalFile first)");
            }
        }
    }

    // "position startpos [moves e2e4 e7e5 ...]" or "position fen <fen> [moves ...]".
    // The GUI always resends the whole game from the start, so the board is
    // rebuilt from scratch on every command.
    private void HandlePosition(string[] tokens)
    {
        int movesIndex = Array.IndexOf(tokens, "moves");

        if (tokens.Length > 1 && tokens[1] == "fen")
        {
            // The FEN spans 6 tokens (or up to "moves" if it appears earlier).
            int fenEnd = movesIndex == -1 ? tokens.Length : movesIndex;
            string fen = string.Join(' ', tokens[2..fenEnd]);
            _board = new Board(fen);
        }
        else
        {
            _board = new Board(); // startpos
        }

        if (movesIndex == -1)
            return;

        // The moves come in UCI notation ("e2e4"). Each text is translated by
        // looking it up among the legal moves: this way the correct flag
        // (capture, castle, en passant...) is always decided by the Core, never
        // by the parser.
        for (int i = movesIndex + 1; i < tokens.Length; i++)
        {
            string uciMove = tokens[i];
            Move move = MoveGenerator.GenerateLegalMoves(_board)
                .FirstOrDefault(m => m.ToString() == uciMove);
            if (move == Move.None)
                break; // Illegal or malformed move: stop applying.
            _board.MakeMove(move);
        }
    }

    // "go [ponder] [depth N] [nodes N] [movetime N] [wtime N btime N
    //      [winc N] [binc N] [movestogo N] | infinite]".
    // Limits are cumulative: a GUI may send clock + depth + nodes and the
    // first one reached must stop the search. "searchmoves" and "mate" are
    // not implemented yet because SearchLimits cannot express a root subset
    // or a mate-search horizon; they are deliberately ignored, never
    // approximated with incorrect semantics.
    // Launches the search asynchronously so the loop keeps serving stop/isready.
    // 'ponderedMs' (ponderhit relaunch only) is the time the ponder search
    // already ran; it is charged against this search's clock budget, floored
    // so at least 100 ms of hard budget always remain (a warm-TT iteration
    // needs almost nothing to reproduce the pondered move).
    private void HandleGo(string[] tokens, long ponderedMs = 0)
    {
        // "go ponder": think on the opponent's time. The search runs without
        // limits (the opponent's clock is ticking, not ours) until the GUI
        // resolves it with "ponderhit" (prediction right -> timed re-search
        // over a warm TT, pondered time deducted) or "stop" (prediction
        // wrong -> discarded).
        bool ponder = Array.IndexOf(tokens, "ponder") != -1;
        bool infinite = Array.IndexOf(tokens, "infinite") != -1;
        _pendingPonderTokens = ponder ? tokens : null;
        if (ponder)
            _ponderTimer.Restart();

        SearchLimits limits = ponder
            ? SearchLimits.Unlimited()
            : ParseLimits(tokens);

        // Clock-managed searches only (soft < hard): movetime/depth/nodes
        // budgets are explicit GUI requests and stay untouched.
        if (ponderedMs > 0 && limits.SoftTimeMs < limits.HardTimeMs)
        {
            // Charge at most HALF the soft budget, never more.
            //
            // The credit used to be clamped against the HARD budget only,
            // leaving 100 ms of it. But iterative deepening is driven by the
            // SOFT budget, so a ponder longer than that budget made the search
            // start already past its deadline and break straight after depth 1.
            // Measured 2026-08-04 at 60+1 (soft budget about 2.5 s), pondering
            // for the stated time and then sending ponderhit:
            //
            //   pondered   500 ms -> depth 16, 4646 ms searching
            //   pondered  2000 ms -> depth 15, 3550 ms
            //   pondered  5000 ms -> depth 11,    5 ms
            //   pondered 10000 ms -> depth  1,    5 ms
            //
            // The longer the opponent thought, the shallower the reply, which
            // against slow bots is most moves of the game. It produced real
            // blunders on Lichess: RZwdbv4z move 23 Qh3 at depth 1 with 41 s
            // still on the clock, and several more the same night.
            //
            // Charging the full pondered time is right for a scheduler that
            // CONTINUES the pondered search, as the reference does. This engine
            // relaunches instead, inheriting only the transposition table, so
            // the pondered depth has to be re-established and that needs time.
            // Half the soft budget is enough to reach the previous depth over a
            // warm table while still answering visibly faster than a fresh move.
            long maxCredit = Math.Min(limits.SoftTimeMs / 2,
                                      Math.Max(0, limits.HardTimeMs - 100));
            limits = limits with { ElapsedOffsetMs = Math.Min(ponderedMs, maxCredit) };
        }

        // UCI: during "go ponder" / "go infinite" the engine must NOT send
        // "bestmove" until the GUI resolves the search with "stop" or
        // "ponderhit" - even if the search finishes on its own (a forced mate
        // breaks iterative deepening in milliseconds, which happens all the
        // time in pondered positions near the end of a game). A bestmove
        // leaked here desyncs the GUI: Arena consumes it as the answer to the
        // NEXT "go" and the engine looks frozen from the next game on.
        bool waitForStop = ponder || infinite;

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _searchTask = Task.Run(() => RunSearch(limits, cts.Token, waitForStop));
    }

    // Mate scores carry distance-to-mate in plies from the root; UCI wants
    // "mate N" in MOVES (negative when the engine is being mated).
    private static string FormatUciScore(int score)
    {
        const int mateBound = AlphaBetaSearch.MateScore - 1_000;
        if (score > mateBound)
            return $"mate {(AlphaBetaSearch.MateScore - score + 1) / 2}";
        if (score < -mateBound)
            return $"mate {-(AlphaBetaSearch.MateScore + score + 1) / 2}";

        // Tablebase verdicts live in their own band just below the mate range
        // (AlphaBetaSearch.TbWin), deliberately so they are never announced as
        // a mate the engine has not proven. Left raw they came out as
        // "cp 98872" - about 988 pawns - on the eval bar and in everything that
        // reads the score, including the bot's resign and draw-offer rules.
        // Report the conventional saturated value, keeping the ply ordering so
        // a win found sooner still scores higher.
        const int tbBand = AlphaBetaSearch.TbWin - 256;
        if (score > tbBand)
            return $"cp {20_000 - (AlphaBetaSearch.TbWin - score)}";
        if (score < -tbBand)
            return $"cp {-20_000 + (AlphaBetaSearch.TbWin + score)}";

        return $"cp {score}";
    }

    private void RunSearch(SearchLimits limits, CancellationToken token, bool waitForStop)
    {
        // Never let an exception escape: a faulted task would poison the next
        // WaitForSearchToFinish, and a GUI that never receives "bestmove"
        // considers the engine hung. Report the error and answer with a legal
        // move so the game (and the process) survives.
        try
        {
            RunSearchCore(limits, token, waitForStop);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"info string search error: {ex.GetType().Name}: {ex.Message}");
            if (_suppressBestmove)
                return;
            Move fallback = MoveGenerator.GenerateLegalMoves(_board).FirstOrDefault();
            _output.WriteLine(fallback == Move.None ? "bestmove 0000" : $"bestmove {fallback}");
        }
    }

    private void RunSearchCore(SearchLimits limits, CancellationToken token, bool waitForStop)
    {
        var stopwatch = Stopwatch.StartNew();

        // Kept for the "ponder" hint: the second move of the last full PV is
        // the opponent reply we expect - the GUI needs it to ponder at all.
        Move[] lastPv = [];

        // One "info" line per completed depth (standard UCI progress output).
        // SynchronousProgress guarantees the lines are written before "bestmove".
        var progress = new SynchronousProgress(p =>
        {
            lastPv = p.Pv;
            long ms = Math.Max(1, stopwatch.ElapsedMilliseconds);
            long nps = p.NodesSearched * 1000 / ms;
            // Mate scores go out as "score mate N" (moves, signed) per UCI;
            // reporting them as huge cp values confuses GUI eval displays
            // and adjudication.
            string score = FormatUciScore(p.Score);
            _output.WriteLine(
                $"info depth {p.Depth} score {score} nodes {p.NodesSearched} time {ms} nps {nps} tbhits {_engine.TbHits} pv {string.Join(' ', p.Pv)}");
        });

        var result = _engine.FindBestMove(_board, limits, token, progress);

        // Ponder/infinite search that finished on its own (e.g. a forced
        // mate): park here until the GUI sends "stop" (-> answer below) or
        // "ponderhit"/new position (-> cancelled with bestmove suppressed).
        // Answering early violates UCI and desyncs the GUI.
        if (waitForStop && !token.IsCancellationRequested)
        {
            LogLine("--", "ponder/infinite search self-finished, parked until stop/ponderhit");
            token.WaitHandle.WaitOne();
        }

        // A ponder search converted by "ponderhit" must stay silent: its only
        // job was warming the TT; the relaunched timed search answers.
        if (_suppressBestmove)
        {
            LogLine("--", "search finished with bestmove suppressed");
            return;
        }

        if (result.BestMove == Move.None)
        {
            _output.WriteLine("bestmove 0000"); // UCI: "no move" (mate/stalemate).
            return;
        }

        // "bestmove X ponder Y": Y is the predicted opponent reply. The PV
        // provides it when it starts with the returned best move (a
        // soft-stopped partial iteration may have improved past the last
        // completed PV). When it does not, predict ANY legal reply instead of
        // omitting the hint: Arena's Permanent Brain stalls its whole game
        // controller on a bare bestmove - it waits forever for the ponder
        // position, the engine's clock runs out, and not even a new game
        // recovers until the engine process is restarted (seen in the
        // 2026-07-14 traffic log). A wrong prediction is harmless: a ponder
        // miss is just stop -> discard -> fresh go.
        Move ponderHint = lastPv.Length >= 2 && lastPv[0] == result.BestMove
            ? lastPv[1]
            : Move.None;
        if (ponderHint == Move.None)
        {
            _board.MakeMove(result.BestMove);
            var replies = MoveGenerator.GenerateLegalMoves(_board);
            if (replies.Count > 0)
                ponderHint = replies[0];
            _board.UnmakeMove();
        }
        _output.WriteLine(ponderHint == Move.None
            ? $"bestmove {result.BestMove}"
            : $"bestmove {result.BestMove} ponder {ponderHint}");
    }

    internal SearchLimits ParseLimits(string[] tokens)
    {
        // Reads the numeric value following a keyword ("wtime 60000" -> 60000).
        long? Value(string keyword)
        {
            int i = Array.IndexOf(tokens, keyword);
            return i != -1 && i + 1 < tokens.Length && long.TryParse(tokens[i + 1], out long v) ? v : null;
        }

        if (Array.IndexOf(tokens, "infinite") != -1)
            return SearchLimits.Unlimited(); // Runs until "stop".

        long? requestedDepth = Value("depth");
        long? requestedNodes = Value("nodes");
        long? moveTime = Value("movetime");

        // Clock mode: the TimeManager turns remaining time + increment into a
        // soft/hard budget, discounting MoveOverhead for GUI latency.
        // "movestogo N" (classical time controls) tightens the budget to the
        // moves left until the next time control.
        long? myTime = _board.SideToMove == Color.White ? Value("wtime") : Value("btime");
        SearchLimits limits = SearchLimits.Unlimited();
        bool hasLimit = false;
        if (myTime is long time)
        {
            long inc = (_board.SideToMove == Color.White ? Value("winc") : Value("binc")) ?? 0;
            int? movesToGo = Value("movestogo") is long mtg
                ? (int)Math.Clamp(mtg, 1, int.MaxValue)
                : null;
            // Game ply (halfmoves elapsed) drives the optimum-time curve: the
            // engine spends a growing share of its clock as the game advances.
            int gamePly = 2 * (_board.FullmoveNumber - 1) + (_board.SideToMove == Color.Black ? 1 : 0);
            limits = TimeManager.FromClock(time, inc, _options.MoveOverhead, movesToGo, gamePly);
            hasLimit = true;
        }

        // Every supplied constraint narrows the same limit object. This is
        // important for tournament GUIs, which routinely add a safety depth
        // or node cap to normal clock parameters.
        if (moveTime is long milliseconds)
        {
            long budget = Math.Max(1, milliseconds);
            limits = limits with
            {
                HardTimeMs = Math.Min(limits.HardTimeMs, budget),
                SoftTimeMs = Math.Min(limits.SoftTimeMs, budget),
            };
            hasLimit = true;
        }

        if (requestedDepth is long depth)
        {
            limits = limits with { MaxDepth = (int)Math.Clamp(depth, 1, int.MaxValue) };
            hasLimit = true;
        }

        if (requestedNodes is long nodes)
        {
            limits = limits with { MaxNodes = Math.Max(1, nodes) };
            hasLimit = true;
        }

        return hasLimit ? limits : SearchLimits.Depth(_engine.DefaultDepth);
    }

    // IProgress<T> implementation that invokes the callback on the calling
    // thread. The standard Progress<T> class posts to a SynchronizationContext
    // (or the thread pool), which could emit "info" lines AFTER "bestmove";
    // UCI GUIs expect them before.
    private sealed class SynchronousProgress(Action<SearchProgress> callback) : IProgress<SearchProgress>
    {
        public void Report(SearchProgress value) => callback(value);
    }
}
