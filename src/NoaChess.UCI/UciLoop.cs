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
    public const string EngineVersion = "2.6.4";
    public const string EngineAuthor = "Juan Carlos Jimenez Vadillo";

    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ChessEngine _engine = new();
    private readonly UciOptions _options = new();

    private Board _board = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

    // Pondering state: while thinking on the opponent's time, the original
    // "go ponder ..." tokens are kept so a "ponderhit" can relaunch the same
    // search with the real clock limits. _suppressBestmove silences the
    // aborted ponder search (UCI forbids a bestmove between ponderhit and
    // the timed search's own answer).
    private string[]? _pendingPonderTokens;
    private volatile bool _suppressBestmove;

    public UciLoop(TextReader input, TextWriter output)
    {
        _input = input;
        // The search task and the command loop both write here.
        _output = TextWriter.Synchronized(output);
    }

    public void Run()
    {
        string? line;
        while ((line = _input.ReadLine()) != null)
        {
            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

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
                    // makes the early iterations nearly free.
                    if (_pendingPonderTokens is string[] goTokens)
                    {
                        _pendingPonderTokens = null;
                        WaitForSearchToFinish(suppressBestmove: true);
                        HandleGo(goTokens.Where(t => t != "ponder").ToArray());
                    }
                    break;

                case "quit":
                    _searchCts?.Cancel();
                    _searchTask?.Wait(TimeSpan.FromSeconds(2));
                    return;

                // Unknown commands are silently ignored, as UCI mandates.
            }
        }
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
        _searchCts?.Cancel();
        _searchTask?.Wait();
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
    private void HandleGo(string[] tokens)
    {
        // "go ponder": think on the opponent's time. The search runs without
        // limits (the opponent's clock is ticking, not ours) until the GUI
        // resolves it with "ponderhit" (prediction right -> timed re-search
        // over a warm TT) or "stop" (prediction wrong -> discarded).
        bool ponder = Array.IndexOf(tokens, "ponder") != -1;
        _pendingPonderTokens = ponder ? tokens : null;

        SearchLimits limits = ponder
            ? SearchLimits.Depth(SearchLimits.DepthUnlimited)
            : ParseLimits(tokens);

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _searchTask = Task.Run(() => RunSearch(limits, cts.Token));
    }

    private void RunSearch(SearchLimits limits, CancellationToken token)
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
            _output.WriteLine(
                $"info depth {p.Depth} score cp {p.Score} nodes {p.NodesSearched} time {ms} nps {nps} pv {string.Join(' ', p.Pv)}");
        });

        var result = _engine.FindBestMove(_board, limits, token, progress);

        // A ponder search converted by "ponderhit" must stay silent: its only
        // job was warming the TT; the relaunched timed search answers.
        if (_suppressBestmove)
            return;

        if (result.BestMove == Move.None)
        {
            _output.WriteLine("bestmove 0000"); // UCI: "no move" (mate/stalemate).
            return;
        }

        // "bestmove X ponder Y": Y is the predicted opponent reply. Without
        // it the GUI has nothing to ponder on and never sends "go ponder".
        // Only offered when the last reported PV actually starts with the
        // returned best move (a soft-stopped partial iteration may differ).
        bool hasPonderHint = lastPv.Length >= 2 && lastPv[0] == result.BestMove;
        _output.WriteLine(hasPonderHint
            ? $"bestmove {result.BestMove} ponder {lastPv[1]}"
            : $"bestmove {result.BestMove}");
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
            // Game ply (halfmoves elapsed) drives the adaptive horizon: the
            // engine spends a growing share of its clock as the game advances.
            int gamePly = 2 * (_board.FullmoveNumber - 1) + (_board.SideToMove == Color.Black ? 1 : 0);
            return TimeManager.FromClock(time, inc, _options.MoveOverhead, movesToGo,
                                         _engine.Profile.AssumedMovesToGo, gamePly);
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
