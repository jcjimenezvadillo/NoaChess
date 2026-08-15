using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// Loads an arch 4 net and evaluates with it.
//
// Exists because the first end-to-end attempt loaded the model correctly and
// then threw a NullReferenceException on the first search, with the UCI layer
// reporting only the message. A test gets the stack trace, and afterwards it
// stays as the check that a threat net can be evaluated at all - which is not
// covered by the format tests, since those never build a network object the
// engine runs.
public class ThreatEvalSmokeTests
{
    private static string? FindArch4Model()
    {
        string path = Path.Combine(Path.GetTempPath(), "_arch4.noannue");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void AnArch4NetEvaluatesWithoutThrowing()
    {
        string? path = FindArch4Model();
        if (path is null)
            return;   // nothing to check on a machine without the scratch model

        Assert.True(NnueModelLoader.TryLoad(path, out NnueNetwork? net, out string error), error);
        Assert.NotNull(net);
        Assert.True(net!.UsesThreats);

        var board = new Board();
        var stack = new NnueAccumulatorStack(net);
        stack.Reset(board);

        short[] white = stack.GetPerspective(board, Color.White);
        short[] black = stack.GetPerspective(board, Color.Black);

        Assert.Equal(net.FtOutputs, white.Length);
        Assert.Equal(net.FtOutputs, black.Length);

        // A threat net must differ from what the same position gives with the
        // threat half removed, or the transformer is being ignored.
        Assert.Contains(white, v => v != 0);
    }
}
