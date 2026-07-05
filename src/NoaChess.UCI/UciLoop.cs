using System.Diagnostics;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;

namespace NoaChess.UCI;

// UCI loop. UCI (Universal Chess Interface) is the standard text protocol any
// chess GUI (Arena, CuteChess, Fritz...) uses to talk to an engine: the GUI
// writes commands to stdin and the engine replies on stdout.
//
// Commands supported in v0.2:
//   uci / isready / ucinewgame / position / go (depth, movetime, clock) / quit
// The rest of the protocol (setoption, asynchronous stop, full info output)
// arrives in v1.0 per the roadmap.
public sealed class UciLoop(TextReader input, TextWriter output)
{
    private readonly ChessEngine _engine = new();
    private Board _board = new();

    public void Run()
    {
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            switch (tokens[0])
            {
                case "uci":
                    // Engine identification + end of handshake.
                    output.WriteLine("id name NoaChess 0.2.0");
                    output.WriteLine("id author NoaChess Team");
                    output.WriteLine("uciok");
                    break;

                case "isready":
                    // The GUI uses it to synchronize; we are always ready in the MVP.
                    output.WriteLine("readyok");
                    break;

                case "ucinewgame":
                    _board = new Board();
                    _engine.NewGame(); // Clear TT/heuristics from the previous game.
                    break;

                case "position":
                    HandlePosition(tokens);
                    break;

                case "go":
                    HandleGo(tokens);
                    break;

                case "quit":
                    return;

                // Unknown commands are silently ignored, as UCI mandates.
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

    // "go [depth N | movetime N | wtime N btime N [winc N] [binc N]]".
    // Priority: explicit depth > explicit movetime > clock-derived budget >
    // engine default depth.
    private void HandleGo(string[] tokens)
    {
        SearchLimits limits = ParseLimits(tokens);

        var stopwatch = Stopwatch.StartNew();

        // One "info" line per completed depth (standard UCI progress output).
        // SynchronousProgress guarantees the lines are written before "bestmove".
        var progress = new SynchronousProgress(p =>
        {
            long ms = Math.Max(1, stopwatch.ElapsedMilliseconds);
            long nps = p.NodesSearched * 1000 / ms;
            output.WriteLine(
                $"info depth {p.Depth} score cp {p.Score} nodes {p.NodesSearched} time {ms} nps {nps} pv {p.BestMove}");
        });

        var result = _engine.FindBestMove(_board, limits, progress: progress);

        output.WriteLine(result.BestMove == Move.None
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

        if (Value("depth") is long depth)
            return SearchLimits.Depth((int)depth);

        if (Value("movetime") is long movetime)
            return SearchLimits.Time(movetime);

        // Clock mode: derive a budget from our remaining time and increment.
        // Classic simple formula: spend 1/30 of the remaining clock plus half
        // the increment, never less than 50 ms. Conservative enough to not
        // flag even in long games (real time management arrives in v1.0).
        long? myTime = _board.SideToMove == Color.White ? Value("wtime") : Value("btime");
        if (myTime is long time)
        {
            long inc = (_board.SideToMove == Color.White ? Value("winc") : Value("binc")) ?? 0;
            long budget = Math.Max(50, time / 30 + inc / 2);
            return SearchLimits.Time(budget);
        }

        return SearchLimits.Depth(_engine.DefaultDepth);
    }

    // IProgress<T> implementation that invokes the callback on the calling
    // thread. The standard Progress<T> class posts to a SynchronizationContext
    // (or the thread pool), which could emit "info" lines AFTER "bestmove";
    // UCI GUIs expect them before.
    private sealed class SynchronousProgress(Action<Engine.Search.SearchProgress> callback)
        : IProgress<Engine.Search.SearchProgress>
    {
        public void Report(Engine.Search.SearchProgress value) => callback(value);
    }
}
