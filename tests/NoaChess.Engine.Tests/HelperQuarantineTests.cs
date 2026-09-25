using System.Diagnostics;
using System.Reflection;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

// 2026-09-25: the Windows bot's tablebases sat on a mechanical disk, and a
// helper deep in tablebase territory could not honour a stop within the
// 3000 ms watchdog. It was quarantined FOREVER: eleven of 24 helpers in one
// game, and the engine played the rest of it on 13 threads. A late helper
// must rejoin the pool, and its late result - computed on an OLD position -
// must never reach the vote of the search running when it comes back.
public class HelperQuarantineTests
{
    private static FieldInfo Hook =>
        typeof(ChessEngine).GetField("s_helperReturnHook", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void LateHelperRejoinsAndItsStaleResultNeverVotes()
    {
        var engine = new ChessEngine { Threads = 4 };
        var messages = new List<string>();
        engine.Diagnostic += m => { lock (messages) messages.Add(m); };

        // Helper 1 comes back 4.5 s late from the FIRST search only.
        int calls = 0;
        Hook.SetValue(null, new Action<int>(index =>
        {
            if (index == 0 && Interlocked.Increment(ref calls) == 1)
                Thread.Sleep(4500);
        }));
        try
        {
            var start = new Board();
            SearchResult first = engine.FindBestMove(start, SearchLimits.Time(300));
            Assert.NotEqual(Move.None, first.BestMove);
            lock (messages)
                Assert.Contains(messages, m => m.StartsWith("helper thread 1") && m.Contains("quarantined"));

            // Black to move, so no start-position move is legal: the late
            // helper returns during THIS search holding a start-position move,
            // and the old code wrote it into the shared result array.
            var other = new Board("r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 5 4");
            SearchResult second = engine.FindBestMove(other, SearchLimits.Time(2500));
            Assert.Contains(second.BestMove, MoveGenerator.GenerateLegalMoves(other));
            lock (messages)
                Assert.Contains(messages, m => m.StartsWith("helper thread 1") && m.Contains("rejoined"));

            // Rejoined for real: counted and woken again, so the next search
            // pays no watchdog wait and quarantines nobody.
            int before;
            lock (messages) before = messages.Count;
            var sw = Stopwatch.StartNew();
            SearchResult third = engine.FindBestMove(start, SearchLimits.Time(300));
            sw.Stop();
            Assert.Contains(third.BestMove, MoveGenerator.GenerateLegalMoves(start));
            Assert.True(sw.ElapsedMilliseconds < 2500, $"third search took {sw.ElapsedMilliseconds} ms");
            lock (messages)
                Assert.Equal(before, messages.Count);
        }
        finally
        {
            Hook.SetValue(null, null);
        }
    }
}
