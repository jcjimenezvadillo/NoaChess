using NoaChess.Core;
using NoaChess.Engine.Heuristics;

namespace NoaChess.Engine.Tests;

public sealed class ContinuationHistoryTests
{
    [Fact]
    public void GravityUpdateStaysBoundedAndCanRecoverFromSaturation()
    {
        var history = new ContinuationHistory();
        int previous = ContinuationHistory.PieceIndex(Color.Black, PieceType.Knight);
        int current = ContinuationHistory.PieceIndex(Color.White, PieceType.Bishop);

        for (int i = 0; i < 10_000; i++)
            history.AddBonus(previous, 18, current, 27, depth: 64);

        int saturated = history.Get(previous, 18, current, 27);
        Assert.InRange(saturated, 1, 1 << 20);

        history.AddMalus(previous, 18, current, 27, depth: 64);
        Assert.True(history.Get(previous, 18, current, 27) < saturated);
    }
}
