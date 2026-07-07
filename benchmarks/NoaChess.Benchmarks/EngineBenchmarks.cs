using BenchmarkDotNet.Attributes;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;

namespace NoaChess.Benchmarks;

// Micro-benchmarks for the engine's hot paths. Run with:
//   dotnet run -c Release --project benchmarks/NoaChess.Benchmarks
// BenchmarkDotNet handles warmup, iteration counts and statistical analysis;
// numbers from Debug builds or dotnet run without -c Release are meaningless.

[MemoryDiagnoser] // Also reports allocations: hot paths should show 0 B.
public class MoveGenerationBenchmarks
{
    private readonly Board _startpos = new();
    private readonly Board _kiwipete = new("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");
    private readonly MoveList _list = new();

    [Benchmark]
    public int LegalMoves_Startpos()
    {
        MoveGenerator.GenerateLegalMoves(_startpos, _list);
        return _list.Count;
    }

    [Benchmark]
    public int LegalMoves_Kiwipete()
    {
        MoveGenerator.GenerateLegalMoves(_kiwipete, _list);
        return _list.Count;
    }

    [Benchmark]
    public long Perft4_Startpos() => Perft.Count(_startpos, 4);
}

[MemoryDiagnoser]
public class MakeMoveBenchmarks
{
    private readonly Board _board = new();
    private readonly Move _e2e4 = new(12, 28, MoveFlag.DoublePawnPush);

    [Benchmark]
    public void MakeUnmake()
    {
        _board.MakeMove(_e2e4);
        _board.UnmakeMove();
    }
}

[MemoryDiagnoser]
public class EvaluationBenchmarks
{
    private readonly ClassicalEvaluator _evaluator = new();
    private readonly Board _middlegame = new("r2q1rk1/pp1bbppp/2n1pn2/3p2B1/3P4/2NBPN2/PP3PPP/R2Q1RK1 w - - 0 1");

    [Benchmark]
    public int Evaluate_Middlegame() => _evaluator.Evaluate(_middlegame);
}

public class SearchBenchmarks
{
    private readonly Board _kiwipete = new("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

    // A fresh engine per invocation: a warm TT would make iterations
    // incomparable (each run would search a different effective tree).
    [Benchmark]
    public long Search_Depth6_Kiwipete() =>
        new ChessEngine().FindBestMove(_kiwipete, SearchLimits.Depth(6)).NodesSearched;
}
