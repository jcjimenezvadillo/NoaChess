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
    public const string EngineVersion = "2.7.2";
    public const string EngineAuthor = "Juan Carlos Jimenez Vadillo";

    private readonly TextReader _input;
    private TextWriter _output;
    private readonly ChessEngine _engine = new();
    private readonly UciOptions _options = new();

    // UCI traffic log ("Debug Log File" option or NOACHESS_DEBUG_LOG env
    // var): every stdin line ("<<"), every stdout line (">>") and the stdin
    // EOF, timestamped. The only way to see what a GUI actually sends when it
    // misbehaves (Arena's engine-frozen-after-new-game reports).
    private StreamWriter? _log;
    private readonly Lock _logLock = new();

    private Board _board = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

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

    public UciLoop(TextReader input, TextWriter output)
    {
        _input = input;
        // The search task and the command loop both write here.
        _output = TextWriter.Synchronized(output);
        if (Environment.GetEnvironmentVariable("NOACHESS_DEBUG_LOG") is string path
            && path.Length > 0)
            OpenLog(path);
    }

    // Opens (or switches) the traffic log and tees stdout through it. Never
    // throws: a bad path just reports and leaves logging off.
    private void OpenLog(string path)
    {
        try
        {
            lock (_logLock)
            {
                _log?.Dispose();
                _log = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write,
                                                  FileShare.ReadWrite))
                { AutoFlush = true };
                _log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] === log opened (pid {Environment.ProcessId}, {EngineName} {EngineVersion}) ===");
            }
            if (_output is not TeeWriter)
                _output = new TeeWriter(_output, this);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"info string debug log rejected: {ex.Message}");
        }
    }

    private void LogLine(string direction, string text)
    {
        lock (_logLock)
            _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {direction} {text}");
    }

    // Tees every engine->GUI line into the traffic log.
    private sealed class TeeWriter(TextWriter main, UciLoop owner) : TextWriter
    {
        public override System.Text.Encoding Encoding => main.Encoding;
        public override void WriteLine(string? value)
        {
            main.WriteLine(value);
            owner.LogLine(">>", value ?? "");
        }
    }

    public void Run()
    {
        string? line;
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
                    return;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"info string command error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        LogLine("--", "stdin EOF — read loop ends");
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
                    // Must answer even while a search runs — the GUI uses it
                    // as a heartbeat. The loop thread is free, so just reply.
                    _output.WriteLine("readyok");
                    break;

                case "setoption":
                    HandleSetOption(tokens);
                    break;

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
                    // it and relaunch as a normal timed search — the warm TT
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
    // 'suppressBestmove' silences the aborted search's answer — used when the
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
        // thread — which would kill the read loop and leave a zombie process
        // (alive but deaf; Arena's Ctrl+N new game shows exactly this). The
        // search already reported the failure; the loop must survive it.
        try { _searchTask?.Wait(); }
        catch (AggregateException) { }
        _searchTask = null;
        _suppressBestmove = false;
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
        if (changed == "Profile")
            _engine.Profile = EngineProfile.ByName(_options.Profile);
        if (changed == "Debug Log File" && _options.DebugLogFile.Length > 0)
            OpenLog(_options.DebugLogFile);

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
                _output.WriteLine("info string UseNNUE ignored: no valid model loaded (set EvalFile first)");
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

    // "go [ponder] [depth N | nodes N | movetime N | wtime N btime N [winc N] [binc N] | infinite]".
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
            ? SearchLimits.Depth(SearchLimits.DepthUnlimited)
            : ParseLimits(tokens);

        // Clock-managed searches only (soft < hard): movetime/depth/nodes
        // budgets are explicit GUI requests and stay untouched.
        if (ponderedMs > 0 && limits.SoftTimeMs < limits.HardTimeMs)
            limits = limits with
            {
                ElapsedOffsetMs = Math.Min(ponderedMs, Math.Max(0, limits.HardTimeMs - 100)),
            };

        // UCI: during "go ponder" / "go infinite" the engine must NOT send
        // "bestmove" until the GUI resolves the search with "stop" or
        // "ponderhit" — even if the search finishes on its own (a forced mate
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
        // the opponent reply we expect — the GUI needs it to ponder at all.
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
                $"info depth {p.Depth} score {score} nodes {p.NodesSearched} time {ms} nps {nps} pv {string.Join(' ', p.Pv)}");
        });

        var result = _engine.FindBestMove(_board, limits, token, progress);

        // Ponder/infinite search that finished on its own (forced mate, depth
        // cap): park here until the GUI sends "stop" (-> answer below) or
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
        // controller on a bare bestmove — it waits forever for the ponder
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

    private SearchLimits ParseLimits(string[] tokens)
    {
        // Reads the numeric value following a keyword ("wtime 60000" -> 60000).
        long? Value(string keyword)
        {
            int i = Array.IndexOf(tokens, keyword);
            return i != -1 && i + 1 < tokens.Length && long.TryParse(tokens[i + 1], out long v) ? v : null;
        }

        if (Array.IndexOf(tokens, "infinite") != -1)
            return SearchLimits.Depth(SearchLimits.DepthUnlimited); // Runs until "stop".

        if (Value("depth") is long depth)
            return SearchLimits.Depth((int)depth);

        if (Value("nodes") is long nodes)
            return SearchLimits.Nodes(nodes);

        if (Value("movetime") is long movetime)
            return SearchLimits.Time(movetime);

        // Clock mode: the TimeManager turns remaining time + increment into a
        // soft/hard budget, discounting MoveOverhead for GUI latency.
        // "movestogo N" (classical time controls) tightens the budget to the
        // moves left until the next time control.
        long? myTime = _board.SideToMove == Color.White ? Value("wtime") : Value("btime");
        if (myTime is long time)
        {
            long inc = (_board.SideToMove == Color.White ? Value("winc") : Value("binc")) ?? 0;
            int? movesToGo = Value("movestogo") is long mtg ? (int)mtg : null;
            // Game ply (halfmoves elapsed) drives the optimum-time curve: the
            // engine spends a growing share of its clock as the game advances.
            int gamePly = 2 * (_board.FullmoveNumber - 1) + (_board.SideToMove == Color.Black ? 1 : 0);
            return TimeManager.FromClock(time, inc, _options.MoveOverhead, movesToGo, gamePly);
        }

        return SearchLimits.Depth(_engine.DefaultDepth);
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
