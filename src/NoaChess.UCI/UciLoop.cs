using NoaChess.Core;
using NoaChess.Engine;

namespace NoaChess.UCI;

// Minimal UCI loop for the MVP. UCI (Universal Chess Interface) is the
// standard text protocol any chess GUI (Arena, CuteChess, Fritz...) uses to
// talk to an engine: the GUI writes commands to stdin and the engine replies
// on stdout.
//
// Commands supported in v0.1.3:
//   uci / isready / ucinewgame / position / go / quit
// The rest of the protocol (setoption, asynchronous stop, detailed info, real
// time management) arrives in later versions per the roadmap.
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
                    output.WriteLine("id name NoaChess 0.1.3");
                    output.WriteLine("id author NoaChess Team");
                    output.WriteLine("uciok");
                    break;

                case "isready":
                    // The GUI uses it to synchronize; we are always ready in the MVP.
                    output.WriteLine("readyok");
                    break;

                case "ucinewgame":
                    _board = new Board();
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

    // "go [depth N] ...". In the MVP only "depth" is honored; any other form
    // (wtime, movetime...) uses the engine's default depth.
    private void HandleGo(string[] tokens)
    {
        int? depth = null;
        int depthIndex = Array.IndexOf(tokens, "depth");
        if (depthIndex != -1 && depthIndex + 1 < tokens.Length && int.TryParse(tokens[depthIndex + 1], out int d))
            depth = d;

        // One "info" line per completed depth (standard UCI progress output).
        // SynchronousProgress guarantees the lines are written before "bestmove".
        var progress = new SynchronousProgress(p =>
            output.WriteLine($"info depth {p.Depth} score cp {p.Score} nodes {p.NodesSearched} pv {p.BestMove}"));

        var result = _engine.FindBestMove(_board, depth, progress: progress);

        output.WriteLine(result.BestMove == Move.None
            ? "bestmove 0000"   // UCI convention for "no move" (mate/stalemate).
            : $"bestmove {result.BestMove}");
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
