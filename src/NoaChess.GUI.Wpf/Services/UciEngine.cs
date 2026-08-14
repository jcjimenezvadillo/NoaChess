using System.Diagnostics;
using System.IO;

namespace NoaChess.GUI.Wpf.Services;

// What an external engine says while it thinks.
public readonly record struct UciInfo(int Depth, int ScoreCp, bool IsMate, int MateIn,
                                      long Nodes, long Nps, long TimeMs, string Pv);

// What it finally plays. 'Move' is UCI text ("e2e4", "e7e8q"), empty when the
// engine produced nothing.
public readonly record struct UciBestMove(string Move, string Ponder);

// How long it may think.
public readonly record struct UciLimits(long MoveTimeMs, int Depth, long Nodes,
                                        long WhiteTimeMs, long BlackTimeMs,
                                        long WhiteIncMs, long BlackIncMs)
{
    public static UciLimits Time(long ms) => new(ms, 0, 0, 0, 0, 0, 0);
    public static UciLimits ToDepth(int depth) => new(0, depth, 0, 0, 0, 0, 0);
    public static UciLimits ToNodes(long nodes) => new(0, 0, nodes, 0, 0, 0, 0);

    public static UciLimits Clock(long whiteMs, long blackMs, long whiteInc, long blackInc)
        => new(0, 0, 0, whiteMs, blackMs, whiteInc, blackInc);

    // The "go" line this describes.
    public string ToGoCommand()
    {
        if (MoveTimeMs > 0) return $"go movetime {MoveTimeMs}";
        if (Depth > 0) return $"go depth {Depth}";
        if (Nodes > 0) return $"go nodes {Nodes}";
        if (WhiteTimeMs > 0 || BlackTimeMs > 0)
        {
            string go = $"go wtime {Math.Max(1, WhiteTimeMs)} btime {Math.Max(1, BlackTimeMs)}";
            if (WhiteIncMs > 0 || BlackIncMs > 0)
                go += $" winc {WhiteIncMs} binc {BlackIncMs}";
            return go;
        }
        return "go movetime 1000";
    }
}

// One external UCI engine, running as a child process.
//
// The protocol is line based and asynchronous: commands go down stdin and
// answers come back up stdout whenever the engine feels like it. That is
// handled with a single reader loop that classifies every line and completes
// whatever the caller is waiting for, rather than by reading in the middle of
// each request - an engine emits dozens of "info" lines between the question
// and the answer, and code that reads a line expecting "bestmove" gets one of
// those instead.
//
// ONE SEARCH AT A TIME, like the built-in engine. An engine that is asked a
// second question before answering the first is entitled to do anything at all.
public sealed class UciEngine : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TaskCompletionSource<bool>? _uciOk;
    private TaskCompletionSource<bool>? _readyOk;
    private TaskCompletionSource<UciBestMove>? _bestMove;
    private IProgress<UciInfo>? _progress;
    private volatile bool _quitting;

    public string Name { get; private set; } = "";
    public string Author { get; private set; } = "";
    public string Path { get; }

    // The options the engine declared during the handshake. Settings are only
    // sent for options an engine actually has: "setoption name Threads" to an
    // engine without threads is at best ignored and at worst an error it
    // reports for the rest of the game.
    private readonly HashSet<string> _declared = new(StringComparer.OrdinalIgnoreCase);

    public bool Supports(string option) => _declared.Contains(option);

    // Everything the engine said, kept for the log window. Bounded: a long
    // analysis produces thousands of lines and none of the old ones matter.
    public List<string> Transcript { get; } = [];

    private UciEngine(Process process, string path)
    {
        _process = process;
        _input = process.StandardInput;
        Path = path;
    }

    // Starts an engine and completes the UCI handshake. Returns null with a
    // reason when the program is not one: a chess engine is recognised by
    // answering "uciok", and nothing else is a valid test.
    public static async Task<(UciEngine? Engine, string Error)> StartAsync(string path)
    {
        if (!File.Exists(path))
            return (null, "There is no file at that path.");

        var info = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex)
        {
            return (null, $"It could not be started: {ex.Message}");
        }

        if (process is null)
            return (null, "It could not be started.");

        var engine = new UciEngine(process, path);

        if (!await engine.HandshakeAsync())
        {
            // What it DID say is the only useful thing to report here: "not an
            // engine" is a verdict, and the first lines of its output are the
            // evidence for it.
            string said;
            lock (engine.Transcript)
            {
                said = engine.Transcript.Count > 0
                    ? "  It said: " + string.Join(" / ", engine.Transcript.Take(4))
                    : "  It said nothing at all.";
            }

            engine.Dispose();
            return (null, $"It started but never answered \"uciok\" in {HandshakeSeconds} seconds." + said);
        }

        return (engine, "");
    }

    private void StartReading()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                    Handle(line);
            }
            catch
            {
                // The process died. Whoever is waiting is released below.
            }
            finally
            {
                // Never leave a caller waiting on an engine that is gone.
                _uciOk?.TrySetResult(false);
                _readyOk?.TrySetResult(false);
                _bestMove?.TrySetResult(new UciBestMove("", ""));
            }
        });

        // stderr is drained so a chatty engine cannot fill its pipe and block.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync() is { } line)
                    Record($"stderr: {line}");
            }
            catch { }
        });
    }

    private void Handle(string line)
    {
        Record(line);

        if (line.StartsWith("bestmove", StringComparison.Ordinal))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string move = parts.Length > 1 ? parts[1] : "";
            string ponder = "";
            int p = Array.IndexOf(parts, "ponder");
            if (p >= 0 && p + 1 < parts.Length)
                ponder = parts[p + 1];

            _bestMove?.TrySetResult(new UciBestMove(move == "(none)" ? "" : move, ponder));
            return;
        }

        if (line.StartsWith("info", StringComparison.Ordinal))
        {
            if (_progress is { } sink && TryParseInfo(line, out UciInfo info))
                sink.Report(info);
            return;
        }

        if (line.StartsWith("uciok", StringComparison.Ordinal))
        {
            _uciOk?.TrySetResult(true);
            return;
        }

        if (line.StartsWith("readyok", StringComparison.Ordinal))
        {
            _readyOk?.TrySetResult(true);
            return;
        }

        if (line.StartsWith("id name ", StringComparison.Ordinal))
            Name = line[8..].Trim();
        else if (line.StartsWith("id author ", StringComparison.Ordinal))
            Author = line[10..].Trim();
        else if (line.StartsWith("option name ", StringComparison.Ordinal))
            RememberOption(line);
    }

    // "option name Move Overhead type spin default 10 min 0 max 5000". The name
    // can contain spaces, so it runs from after "name" to before "type".
    private void RememberOption(string line)
    {
        int type = line.IndexOf(" type ", StringComparison.Ordinal);
        if (type < 12)
            return;
        _declared.Add(line[12..type].Trim());
    }

    private void Record(string line)
    {
        lock (Transcript)
        {
            Transcript.Add(line);
            if (Transcript.Count > 400)
                Transcript.RemoveRange(0, 200);
        }
    }

    // "info depth 12 score cp 34 nodes 1234 nps 5678 time 90 pv e2e4 e7e5"
    private static bool TryParseInfo(string line, out UciInfo info)
    {
        info = default;
        string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int depth = 0, cp = 0, mateIn = 0;
        long nodes = 0, nps = 0, time = 0;
        bool isMate = false, sawSomething = false;
        string pv = "";

        for (int i = 1; i < t.Length; i++)
        {
            switch (t[i])
            {
                case "depth" when i + 1 < t.Length:
                    depth = ParseInt(t[++i]);
                    sawSomething = true;
                    break;
                case "nodes" when i + 1 < t.Length:
                    nodes = ParseLong(t[++i]);
                    break;
                case "nps" when i + 1 < t.Length:
                    nps = ParseLong(t[++i]);
                    break;
                case "time" when i + 1 < t.Length:
                    time = ParseLong(t[++i]);
                    break;
                case "score" when i + 2 < t.Length:
                    if (t[i + 1] == "cp")
                        cp = ParseInt(t[i + 2]);
                    else if (t[i + 1] == "mate")
                    {
                        isMate = true;
                        mateIn = ParseInt(t[i + 2]);
                    }
                    i += 2;
                    sawSomething = true;
                    break;
                case "pv":
                    pv = string.Join(' ', t[(i + 1)..]);
                    i = t.Length;
                    break;
            }
        }

        // Lines that only report which move is being searched carry no reading
        // and would blank the panel if they were let through.
        if (!sawSomething)
            return false;

        info = new UciInfo(depth, cp, isMate, mateIn, nodes, nps, time, pv);
        return true;
    }

    private static int ParseInt(string s) => int.TryParse(s, out int v) ? v : 0;
    private static long ParseLong(string s) => long.TryParse(s, out long v) ? v : 0;

    // How long an engine may take to identify itself. Generous on purpose: an
    // engine loads its evaluation network before it answers, and a big network
    // off a slow disk takes seconds. This engine's own debug build needs ten.
    // The limit exists only so that pointing the GUI at a program that is not
    // an engine fails instead of hanging for ever.
    private const int HandshakeSeconds = 30;

    private async Task<bool> HandshakeAsync()
    {
        // Created BEFORE the reader is started: a fast engine can answer
        // between the two, and an answer that arrives with nobody waiting for
        // it is simply dropped.
        _uciOk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartReading();
        Send("uci");

        Task finished = await Task.WhenAny(_uciOk.Task, Task.Delay(HandshakeSeconds * 1000));
        return finished == _uciOk.Task && _uciOk.Task.Result;
    }

    // Long by the same argument: "isready" after "ucinewgame" is where an
    // engine clears a large hash table, and that is not instant either.
    public async Task<bool> IsReadyAsync(int timeoutMs = 30_000)
    {
        _readyOk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send("isready");
        Task finished = await Task.WhenAny(_readyOk.Task, Task.Delay(timeoutMs));
        return finished == _readyOk.Task && _readyOk.Task.Result;
    }

    public void SetOption(string name, string value) => Send($"setoption name {name} value {value}");

    public async Task NewGameAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Send("ucinewgame");
            await IsReadyAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Asks for a move. 'startFen' is null or the standard start for a normal
    // game; 'moves' are the UCI moves played from it.
    public async Task<UciBestMove> SearchAsync(string? startFen, IReadOnlyList<string> moves,
                                               UciLimits limits, IProgress<UciInfo>? progress,
                                               CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_quitting || _process.HasExited)
                return new UciBestMove("", "");

            _progress = progress;
            _bestMove = new TaskCompletionSource<UciBestMove>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            string position = startFen is null || startFen == NoaChess.Core.Board.StartFen
                ? "position startpos"
                : $"position fen {startFen}";
            if (moves.Count > 0)
                position += " moves " + string.Join(' ', moves);

            Send(position);
            Send(limits.ToGoCommand());

            // Cancelling means "stop": the protocol's answer to a stop is still
            // a bestmove, so the wait below ends either way and the engine is
            // left in a state where the next question is legal.
            using CancellationTokenRegistration registration =
                cancellation.Register(() => Send("stop"));

            return await _bestMove.Task;
        }
        catch (Exception)
        {
            // The engine went away underneath us, which during shutdown is the
            // normal case. No move is the honest answer.
            return new UciBestMove("", "");
        }
        finally
        {
            _progress = null;
            _gate.Release();
        }
    }

    public void RequestStop() => Send("stop");

    private void Send(string command)
    {
        if (_quitting)
            return;
        try
        {
            Record($"> {command}");
            _input.WriteLine(command);
            _input.Flush();
        }
        catch
        {
            // The engine died mid-command. The reader loop releases the waiters.
        }
    }

    public void Dispose()
    {
        _quitting = true;
        try
        {
            _input.WriteLine("quit");
            _input.Flush();
        }
        catch { }

        try
        {
            if (!_process.WaitForExit(1500))
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        _process.Dispose();

        // The gate is deliberately NOT disposed. Closing the window while an
        // engine is thinking leaves a search waiting on it, and that search
        // still has to run its finally and release: disposing here turns a
        // normal shutdown into an ObjectDisposedException on a thread nobody
        // is catching. A SemaphoreSlim that was never waited on as a handle
        // holds nothing worth reclaiming.
    }
}
