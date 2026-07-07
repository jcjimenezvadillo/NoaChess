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
}
