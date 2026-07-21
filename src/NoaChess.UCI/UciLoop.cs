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
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ChessEngine _engine = new();
    private readonly UciOptions _options = new();

    private Board _board = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

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
                    _output.WriteLine("id name NoaChess 1.0.0");
                    _output.WriteLine("id author NoaChess Team");
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
                    WaitForSearchToFinish();
                    _board = new Board();
                    _engine.NewGame(); // Clear TT/heuristics from the previous game.
                    break;

                case "position":
                    WaitForSearchToFinish();
                    HandlePosition(tokens);
                    break;

                case "go":
                    WaitForSearchToFinish();
                    HandleGo(tokens);
                    break;

                case "stop":
                    // Cancel the running search; its task still prints the
                    // "bestmove" (the best of the last completed iteration).
                    _searchCts?.Cancel();
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
    private void WaitForSearchToFinish()
    {
        _searchCts?.Cancel();
        _searchTask?.Wait();
        _searchTask = null;
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

    // "go [depth N | nodes N | movetime N | wtime N btime N [winc N] [binc N] | infinite]".
    // Launches the search asynchronously so the loop keeps serving stop/isready.
    private void HandleGo(string[] tokens)
    {
        SearchLimits limits = ParseLimits(tokens);

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _searchTask = Task.Run(() => RunSearch(limits, cts.Token));
    }

    private void RunSearch(SearchLimits limits, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();

        // One "info" line per completed depth (standard UCI progress output).
        // SynchronousProgress guarantees the lines are written before "bestmove".
        var progress = new SynchronousProgress(p =>
        {
            long ms = Math.Max(1, stopwatch.ElapsedMilliseconds);
            long nps = p.NodesSearched * 1000 / ms;
            _output.WriteLine(
                $"info depth {p.Depth} score cp {p.Score} nodes {p.NodesSearched} time {ms} nps {nps} pv {string.Join(' ', p.Pv)}");
        });

        var result = _engine.FindBestMove(_board, limits, token, progress);

        _output.WriteLine(result.BestMove == Move.None
            ? "bestmove 0000"   // UCI convention for "no move" (mate/stalemate).
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
            return TimeManager.FromClock(time, inc, _options.MoveOverhead, movesToGo);
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
