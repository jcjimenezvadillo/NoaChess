using NoaChess.Core;

namespace NoaChess.Core.Tests;

// The board keeps its undo frames in an array and the repetition scans read
// their own packed key array, bounded by the fifty-move clock and by the
// newest null move. Those bounds used to be a break inside an enumerator walk,
// so this file re-derives the same answers the slow, obvious way and demands
// they agree over random games that make, unmake, null-move and clone.
public class HistoryScanTests
{
    // The obvious model: every key ever pushed, in order, plus the indices of
    // the null-move frames. Kept by the test so the board's answers have
    // something independent to be wrong against.
    private sealed class HistoryModel
    {
        public readonly List<ulong> Keys = [];
        public readonly List<bool> IsNull = [];

        public HistoryModel Copy()
        {
            var copy = new HistoryModel();
            copy.Keys.AddRange(Keys);
            copy.IsNull.AddRange(IsNull);
            return copy;
        }

        // Repetitions of 'current', walking back from the newest frame, one
        // step per ply, stopping at the fifty-move window or a null move.
        public int CountRepetitions(ulong current, int halfmoveClock)
        {
            int count = 0;
            int distance = 0;
            for (int i = Keys.Count - 1; i >= 0; i--)
            {
                if (IsNull[i])
                    break;
                if (++distance > halfmoveClock)
                    break;
                if (Keys[i] == current)
                    count++;
            }
            return count;
        }

        public bool HasRepeated(ulong current, int halfmoveClock)
        {
            var seen = new List<ulong> { current };
            int distance = 0;
            for (int i = Keys.Count - 1; i >= 0; i--)
            {
                if (IsNull[i] || ++distance > halfmoveClock)
                    break;
                seen.Add(Keys[i]);
            }
            return seen.Count != seen.Distinct().Count();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ScansAgreeWithTheObviousWalk(int seed)
    {
        var rng = new Random(seed);
        var board = new Board(Board.StartFen);
        var model = new HistoryModel();

        // Deep enough for the fifty-move window to fill and for shuffles to
        // repeat positions several times over.
        for (int ply = 0; ply < 300; ply++)
        {
            Assert.Equal(model.CountRepetitions(board.ZobristKey, board.HalfmoveClock),
                         board.CountRepetitions());
            Assert.Equal(model.HasRepeated(board.ZobristKey, board.HalfmoveClock),
                         board.HasRepeated());

            // A null move now and then: it is the boundary no scan may cross,
            // and it nests inside the ordinary move history.
            if (rng.Next(8) == 0)
            {
                ulong key = board.ZobristKey;
                board.MakeNullMove();
                model.Keys.Add(key);
                model.IsNull.Add(true);

                Assert.Equal(model.CountRepetitions(board.ZobristKey, board.HalfmoveClock),
                             board.CountRepetitions());

                board.UnmakeNullMove();
                model.Keys.RemoveAt(model.Keys.Count - 1);
                model.IsNull.RemoveAt(model.IsNull.Count - 1);
                Assert.Equal(key, board.ZobristKey);
            }

            List<Move> legal = MoveGenerator.GenerateLegalMoves(board).ToList();
            if (legal.Count == 0)
                break;

            // Bias towards reversible moves so positions actually repeat:
            // a purely random game pushes pawns and captures its way out of
            // every repetition window before one closes.
            List<Move> quiet = legal.Where(m => !m.IsCapture && !m.IsPromotion
                                                && board.PieceTypeAt(m.From) != PieceType.Pawn).ToList();
            List<Move> pool = quiet.Count > 0 && rng.Next(10) < 9 ? quiet : legal;

            Move move = pool[rng.Next(pool.Count)];
            ulong before = board.ZobristKey;
            board.MakeMove(move);
            model.Keys.Add(before);
            model.IsNull.Add(false);
        }

        // And the whole history unwinds back to the start.
        while (model.Keys.Count > 0)
        {
            board.UnmakeMove();
            model.Keys.RemoveAt(model.Keys.Count - 1);
            model.IsNull.RemoveAt(model.IsNull.Count - 1);
            Assert.Equal(model.CountRepetitions(board.ZobristKey, board.HalfmoveClock),
                         board.CountRepetitions());
        }

        Assert.Equal(new Board(Board.StartFen).ZobristKey, board.ZobristKey);
    }

    [Fact]
    public void CloneCarriesTheHistoryAndItsNullBoundary()
    {
        var board = new Board(Board.StartFen);
        var model = new HistoryModel();
        var rng = new Random(99);

        for (int ply = 0; ply < 60; ply++)
        {
            List<Move> legal = MoveGenerator.GenerateLegalMoves(board).ToList();
            List<Move> quiet = legal.Where(m => !m.IsCapture && !m.IsPromotion
                                                && board.PieceTypeAt(m.From) != PieceType.Pawn).ToList();
            List<Move> pool = quiet.Count > 0 ? quiet : legal;
            Move move = pool[rng.Next(pool.Count)];
            model.Keys.Add(board.ZobristKey);
            model.IsNull.Add(false);
            board.MakeMove(move);
        }

        // Clone from underneath a null move: the copy must see the same
        // boundary, not the frames hidden behind it.
        board.MakeNullMove();
        model.Keys.Add(board.ZobristKey ^ Zobrist.SideToMoveKey);
        model.IsNull.Add(true);

        Board clone = board.Clone();
        Assert.Equal(board.ZobristKey, clone.ZobristKey);
        Assert.Equal(board.CountRepetitions(), clone.CountRepetitions());
        Assert.Equal(0, clone.CountRepetitions());

        // Both boards keep answering identically as play continues on the copy.
        foreach (Move move in MoveGenerator.GenerateLegalMoves(clone).Take(3))
        {
            clone.MakeMove(move);
            board.MakeMove(move);
            Assert.Equal(board.CountRepetitions(), clone.CountRepetitions());
            Assert.Equal(board.HasRepeated(), clone.HasRepeated());
            Assert.Equal(board.HasUpcomingRepetition(1), clone.HasUpcomingRepetition(1));
            clone.UnmakeMove();
            board.UnmakeMove();
        }
    }

    // A shuffle that repeats a position three times, with a null move dropped
    // in the middle of it: the null frame hides everything before it, so the
    // count restarts and comes back once the null move is undone.
    [Fact]
    public void NullMoveHidesTheOlderHistoryAndGivesItBack()
    {
        // No castling rights: with them, moving the rook changes the identity
        // of the starting position and the first occurrence would not count.
        var board = new Board("4k3/8/8/8/8/8/8/4K2R w - - 0 1");
        Play(board, "h1h2 e8e7 h2h1 e7e8 h1h2 e8e7 h2h1 e7e8");
        Assert.Equal(2, board.CountRepetitions());

        board.MakeNullMove();
        Assert.Equal(0, board.CountRepetitions());
        Assert.False(board.HasRepeated());
        board.UnmakeNullMove();

        Assert.Equal(2, board.CountRepetitions());
        Assert.True(board.HasRepeated());
    }

    private static void Play(Board board, string moves)
    {
        foreach (string uci in moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Move move = MoveGenerator.GenerateLegalMoves(board).Single(m => m.ToString() == uci);
            board.MakeMove(move);
        }
    }
}
