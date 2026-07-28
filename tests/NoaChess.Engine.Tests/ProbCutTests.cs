using System.Reflection;
using NoaChess.Core;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

public sealed class ProbCutTests
{
    [Theory]
    [InlineData("7k/P7/8/8/8/8/8/7K w - - 0 1", "a7", "a8", MoveFlag.PromoQueen)]
    [InlineData("r6k/1P6/8/8/8/8/8/7K w - - 0 1", "b7", "a8", MoveFlag.PromoQueenCapture)]
    public void QueenPromotionsBypassTheSimplifiedSeeGate(
        string fen, string from, string to, MoveFlag flag)
    {
        var board = new Board(fen);
        var move = new Move(Sq(from), Sq(to), flag);
        MethodInfo? method = typeof(AlphaBetaSearch).GetMethod(
            "PassesProbCutSeeGate", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.True((bool)method.Invoke(null, [board, move, 1_000])!);
    }

    private static int Sq(string algebraic)
        => (algebraic[1] - '1') * 8 + algebraic[0] - 'a';
}
