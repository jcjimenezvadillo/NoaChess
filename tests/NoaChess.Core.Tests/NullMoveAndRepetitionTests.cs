using NoaChess.Core;

namespace NoaChess.Core.Tests;

// Tests of the v1.0 Core additions: null moves, repetition counting and the
// incremental pawn-only Zobrist key.
public class NullMoveAndRepetitionTests
{
    [Theory]
    [InlineData(Board.StartFen)]
    // Position WITH an en passant square (the null move must clear and restore it).
    [InlineData("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2")]
    public void NullMove_MakeUnmake_RestoresExactState(string fen)
    {
        var board = new Board(fen);
        string originalFen = Fen.Save(board);
        ulong originalHash = board.ZobristKey;

        board.MakeNullMove();
        Assert.NotEqual(originalHash, board.ZobristKey); // Side to move changed.
        Assert.Equal(Squares.None, board.EnPassantSquare); // EP is not capturable after a pass.
        board.UnmakeNullMove();

        Assert.Equal(originalFen, Fen.Save(board));
        Assert.Equal(originalHash, board.ZobristKey);
    }

    [Fact]
    public void CountRepetitions_DetectsKnightShuffle()
    {
        var board = new Board();
        void Play(string uci) =>
            board.MakeMove(MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == uci));

        // Knights out and back: the starting position repeats.
        Play("g1f3"); Play("g8f6"); Play("f3g1"); Play("f6g8");
        Assert.Equal(1, board.CountRepetitions());

        // Once more: second repetition -> threefold occurrence in total.
        Play("g1f3"); Play("g8f6"); Play("f3g1"); Play("f6g8");
        Assert.Equal(2, board.CountRepetitions());
        Assert.Equal(GameResult.ThreefoldRepetition, GameState.GetResult(board));
    }

    [Fact]
    public void PawnMove_ResetsRepetitionWindow()
    {
        var board = new Board();
        void Play(string uci) =>
            board.MakeMove(MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == uci));

        // A pawn move is irreversible: nothing before it can repeat.
        Play("e2e4"); Play("e7e5"); Play("g1f3"); Play("g8f6");
        Assert.Equal(0, board.CountRepetitions());
    }

    [Fact]
    public void NonCapturableEnPassant_DoesNotChangePositionIdentity()
    {
        const string withEp = "rnbqkbnr/pppppppp/8/8/P7/8/1PPPPPPP/RNBQKBNR b KQkq a3 0 1";
        const string withoutEp = "rnbqkbnr/pppppppp/8/8/P7/8/1PPPPPPP/RNBQKBNR b KQkq - 0 1";
        var board = new Board(withEp);

        Assert.Equal(new Board(withoutEp).ZobristKey, board.ZobristKey);

        // Once both knights return, the EP-less current position is genuinely
        // the same one seen immediately after a4, despite FEN publishing a3.
        Play(board, "g8f6 g1f3 f6g8 f3g1");
        Assert.Equal(1, board.CountRepetitions());
    }

    [Fact]
    public void CapturableEnPassant_RemainsPartOfPositionIdentity()
    {
        const string withEp = "4k3/8/8/8/Pp6/8/8/4K3 b - a3 0 1";
        const string withoutEp = "4k3/8/8/8/Pp6/8/8/4K3 b - - 0 1";

        Assert.NotEqual(new Board(withEp).ZobristKey, new Board(withoutEp).ZobristKey);
    }

    [Fact]
    public void PawnZobristKey_MatchesFreshlyLoadedBoard()
    {
        // The incrementally maintained pawn key after captures, en passant and
        // a promotion must match the key computed from scratch via FEN.
        var board = new Board();
        void Play(string uci) =>
            board.MakeMove(MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == uci));

        foreach (string uci in new[] { "e2e4", "d7d5", "e4d5", "c7c6", "d5c6", "g8f6", "c6b7", "f6g8", "b7a8q" })
            Play(uci);

        var fresh = new Board(Fen.Save(board));
        Assert.Equal(fresh.PawnZobristKey, board.PawnZobristKey);
    }

    [Fact]
    public void HasNonPawnMaterial_DetectsPawnEndgames()
    {
        // King + pawns only: no non-pawn material (null move would be unsafe).
        var kingAndPawns = new Board("4k3/pppp4/8/8/8/8/4PPPP/4K3 w - - 0 1");
        Assert.False(kingAndPawns.HasNonPawnMaterial(Color.White));
        Assert.False(kingAndPawns.HasNonPawnMaterial(Color.Black));

        // Add a knight: white has non-pawn material, black still does not.
        var withKnight = new Board("4k3/pppp4/8/8/8/8/4PPPP/3NK3 w - - 0 1");
        Assert.True(withKnight.HasNonPawnMaterial(Color.White));
        Assert.False(withKnight.HasNonPawnMaterial(Color.Black));
    }

    [Theory]
    [InlineData(Board.StartFen, "g1f3 g8f6 f3g1 f6g8")]
    [InlineData("2b4k/8/8/8/8/8/8/2B4K w - - 0 1", "c1g5 c8g4 g5c1 g4c8")]
    [InlineData("4k2r/8/8/8/8/8/8/R3K3 w - - 0 1", "a1a2 h8h7 a2a1 h7h8")]
    [InlineData("4k2q/8/8/8/8/8/8/Q3K3 w - - 0 1", "a1a2 h8h7 a2a1 h7h8")]
    [InlineData("7k/8/8/8/8/8/8/K7 w - - 0 1", "a1a2 h8h7 a2a1 h7h8")]
    public void HasUpcomingRepetition_DetectsEveryReversiblePieceType(
        string fen, string moves)
    {
        var board = new Board(fen);
        Play(board, moves);

        // At a sufficiently deep search ply, the method must exactly agree
        // with the simple oracle "does any legal move repeat a position?".
        Assert.True(AnyLegalMoveRepeats(board));
        Assert.True(board.HasUpcomingRepetition(ply: 4));
    }

    [Fact]
    public void HasUpcomingRepetition_DistinguishesBeforeAndAfterRoot()
    {
        var board = new Board();
        Play(board, "g1f3 g8f6 f3g1 f6g8");

        // Nf3 would repeat the position three plies back. At ply 3 that
        // position is the root itself and a second occurrence is not enough;
        // at ply 4 it lies strictly inside the search tree and closes a cycle.
        Assert.False(board.HasUpcomingRepetition(ply: 3));
        Assert.True(board.HasUpcomingRepetition(ply: 4));
    }

    [Fact]
    public void HasUpcomingRepetition_RequiresThreefoldBeforeRoot()
    {
        var board = new Board();
        Play(board,
            "g1f3 g8f6 f3g1 f6g8 g1f3 g8f6 f3g1 f6g8");

        // Nf3 has already occurred twice, so playing it once more is a real
        // threefold even when every earlier occurrence predates the root.
        Assert.True(board.HasUpcomingRepetition(ply: 0));
    }

    [Fact]
    public void HasUpcomingRepetition_RejectsBlockedSliderReturn()
    {
        // Black vacates a2, White slides Ra1-a3, then the knight returns to
        // a2. The history keys differ by Ra1/a3 only, but Ra3-a1 is NOT legal
        // now because the knight blocks the strict-between square.
        var board = new Board("7k/8/8/8/8/8/n7/R6K b - - 0 1");
        Play(board, "a2b4 a1a3 b4a2");

        Assert.False(AnyLegalMoveRepeats(board));
        Assert.False(board.HasUpcomingRepetition(ply: 128));
    }

    [Fact]
    public void HasUpcomingRepetition_StopsAtIrreversibleMove()
    {
        var board = new Board();
        Play(board, "g1f3 g8f6 f3g1 f6g8 e2e4");

        Assert.Equal(0, board.HalfmoveClock);
        Assert.False(board.HasUpcomingRepetition(ply: 128));
        Assert.False(board.HasRepeated());
    }

    [Fact]
    public void HasRepeated_RemembersAnIntermediateCycleFromANewPosition()
    {
        var board = new Board();
        Play(board, "g1f3 g8f6 f3g1 f6g8 b1c3");

        // The current position after Nc3 is new, but the starting position was
        // repeated one ply earlier and remains inside the reversible window.
        Assert.Equal(0, board.CountRepetitions());
        Assert.True(board.HasRepeated());
    }

    [Fact]
    public void RepetitionScans_StopAtNullAndNullDoesNotAdvanceRule50()
    {
        var board = new Board();
        Play(board, "g1f3 g8f6 f3g1 f6g8");
        Assert.Equal(4, board.HalfmoveClock);
        Assert.Equal(1, board.CountRepetitions());
        Assert.True(board.HasRepeated());
        Assert.True(board.HasUpcomingRepetition(ply: 4));

        board.MakeNullMove();
        Assert.Equal(4, board.HalfmoveClock);
        Assert.Equal(0, board.CountRepetitions());
        Assert.False(board.HasRepeated());
        Assert.False(board.HasUpcomingRepetition(ply: 128));

        // Bury the null two plies deep. A third prospective move would appear
        // to return to a pre-null position if either scan crossed the boundary.
        Play(board, "g8f6 g1f3");
        Assert.Equal(0, board.CountRepetitions());
        Assert.False(board.HasRepeated());
        Assert.False(board.HasUpcomingRepetition(ply: 128));

        board.UnmakeMove();
        board.UnmakeMove();
        board.UnmakeNullMove();
        Assert.Equal(1, board.CountRepetitions());
        Assert.True(board.HasRepeated());
        Assert.True(board.HasUpcomingRepetition(ply: 4));
    }

    [Fact]
    public void HasUpcomingRepetition_RejectsNegativePly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Board().HasUpcomingRepetition(-1));
    }

    private static void Play(Board board, string moves)
    {
        foreach (string uci in moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Move move = MoveGenerator.GenerateLegalMoves(board)
                .Single(m => m.ToString() == uci);
            board.MakeMove(move);
        }
    }

    private static bool AnyLegalMoveRepeats(Board board)
    {
        foreach (Move move in MoveGenerator.GenerateLegalMoves(board))
        {
            board.MakeMove(move);
            bool repeats = board.CountRepetitions() >= 1;
            board.UnmakeMove();
            if (repeats)
                return true;
        }

        return false;
    }
}
