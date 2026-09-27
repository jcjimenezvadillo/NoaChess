using NoaChess.Core;

namespace NoaChess.Core.Tests;

// Board.IsRepetition(ply) implements the reference's rule: a single earlier
// occurrence counts only when it lies strictly inside the search (closer than
// 'ply' plies back), while an occurrence at or before the root needs to have
// been a repetition itself. This file re-derives that answer the slow way from
// a plain list of keys and demands agreement over random games with null moves
// interleaved, for every ply value the search could pass.
public class RepetitionRuleTests
{
    private static bool NaiveIsRepetition(List<ulong> keys, List<bool> isNull, ulong current,
                                          int halfmoveClock, int ply)
    {
        if (halfmoveClock < 4)
            return false;

        // Frames inside the reversible window and above the newest null move.
        int oldest = keys.Count - halfmoveClock;
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            if (isNull[i])
            {
                oldest = Math.Max(oldest, i + 1);
                break;
            }
        }
        oldest = Math.Max(oldest, 0);

        for (int i = keys.Count - 1; i >= oldest; i--)
        {
            if (keys[i] != current)
                continue;
            int distance = keys.Count - i;
            if (distance < ply)
                return true;
            for (int j = i - 1; j >= oldest; j--)
                if (keys[j] == current)
                    return true;
            return false;
        }
        return false;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MatchesTheNaiveRuleOverRandomGames(int seed)
    {
        var rng = new Random(seed);
        var board = new Board();
        var keys = new List<ulong>();
        var isNull = new List<bool>();
        var scratch = new MoveList();
        int checks = 0;

        for (int step = 0; step < 400; step++)
        {
            // Mostly reversible shuffles so repetitions actually happen.
            MoveGenerator.GenerateLegalMoves(board, scratch);
            if (scratch.Count == 0)
                break;

            bool nullMove = rng.Next(12) == 0 && !board.IsInCheck();
            if (nullMove)
            {
                keys.Add(board.ZobristKey);
                isNull.Add(true);
                board.MakeNullMove();
            }
            else
            {
                var quiet = new List<Move>();
                for (int i = 0; i < scratch.Count; i++)
                    if (!scratch[i].IsCapture && board.PieceTypeAt(scratch[i].From) != PieceType.Pawn)
                        quiet.Add(scratch[i]);
                Move move = quiet.Count > 0 && rng.Next(10) != 0
                    ? quiet[rng.Next(quiet.Count)]
                    : scratch[rng.Next(scratch.Count)];
                keys.Add(board.ZobristKey);
                isNull.Add(false);
                board.MakeMove(move);
            }

            for (int ply = 0; ply <= 12; ply++)
            {
                bool expected = NaiveIsRepetition(keys, isNull, board.ZobristKey,
                                                  board.HalfmoveClock, ply);
                Assert.Equal(expected, board.IsRepetition(ply));
                checks++;
            }

            // Occasionally unmake a couple of frames so the array path is
            // exercised in both directions.
            if (rng.Next(6) == 0 && keys.Count >= 2)
            {
                for (int k = 0; k < 2; k++)
                {
                    int last = keys.Count - 1;
                    if (isNull[last]) board.UnmakeNullMove();
                    else board.UnmakeMove();
                    keys.RemoveAt(last);
                    isNull.RemoveAt(last);
                }
            }
        }

        Assert.True(checks > 1000);
    }

    [Fact]
    public void AGameHistoryRepetitionIsNotADrawInsideTheSearchUntilThreefold()
    {
        // Knights shuffle: the start position recurs in the game history.
        var board = new Board();
        string[] shuffle = ["g1f3", "g8f6", "f3g1", "f6g8"];
        foreach (string uci in shuffle)
            Play(board, uci);

        // The root is the start position, seen twice in the game. Reaching it
        // again inside the search at ply 4 is a threefold: a draw.
        foreach (string uci in shuffle)
            Play(board, uci);
        Assert.True(board.IsRepetition(4));
        Assert.True(board.IsRepetition(100));

        // Undo the search plies: the root itself, seen twice, with ply 0.
        for (int i = 0; i < 4; i++)
            board.UnmakeMove();

        // A fresh game with ONE earlier occurrence: not a draw when that
        // occurrence lies at or before the root (ply <= distance), a draw when
        // the search itself produced it.
        board = new Board();
        foreach (string uci in shuffle)
            Play(board, uci);
        Assert.False(board.IsRepetition(4));   // earlier occurrence 4 plies back, root 4 back
        Assert.False(board.IsRepetition(2));
        Assert.True(board.IsRepetition(5));    // root further back: the cycle is the search's
        Assert.Equal(1, board.CountRepetitions());
    }

    private static void Play(Board board, string uci)
    {
        Move move = MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == uci);
        board.MakeMove(move);
    }
}
