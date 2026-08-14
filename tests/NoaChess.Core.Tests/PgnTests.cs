using NoaChess.Core;

namespace NoaChess.Core.Tests;

// PGN reading. The format is forgiving and the files in the wild use every bit
// of that latitude, so the cases here are the shapes that actually turn up:
// comments, variations, glyphs, and move numbers glued to their move.
public class PgnTests
{
    private const string Sample = """
        [Event "Test match"]
        [Site "Somewhere"]
        [Date "2026.08.12"]
        [Round "1"]
        [White "Player A"]
        [Black "Player B"]
        [Result "1-0"]

        1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 1-0
        """;

    [Fact]
    public void ReadsTagsAndMoves()
    {
        Assert.True(Pgn.TryParseFirst(Sample, out PgnGame game));
        Assert.Equal("Player A", game.Get("White"));
        Assert.Equal("Player B", game.Get("Black"));
        Assert.Equal("1-0", game.Result);
        Assert.Equal(["e4", "e5", "Nf3", "Nc6", "Bb5", "a6"], game.Moves);
    }

    [Fact]
    public void SkipsCommentsGlyphsAndVariations()
    {
        const string annotated = """
            [Event "Annotated"]
            [Result "*"]

            1. e4 {The king's pawn.} e5 $1 2. Nf3 (2. f4 exf4 3. Nf3 {gambit}) 2... Nc6
            3. Bb5 ; the Ruy Lopez
            """;

        Assert.True(Pgn.TryParseFirst(annotated, out PgnGame game));
        Assert.Equal(["e4", "e5", "Nf3", "Nc6", "Bb5"], game.Moves);
    }

    [Fact]
    public void HandlesNestedVariations()
    {
        const string nested = "1. d4 (1. e4 e5 (1... c5 2. Nf3) 2. Nf3) 1... d5 2. c4";
        Assert.True(Pgn.TryParseFirst(nested, out PgnGame game));
        Assert.Equal(["d4", "d5", "c4"], game.Moves);
    }

    [Fact]
    public void HandlesMoveNumbersGluedToTheMove()
    {
        // "1.e4" with no space is legal and a naive whitespace split loses it.
        Assert.True(Pgn.TryParseFirst("1.e4 e5 2.Nf3 Nc6", out PgnGame game));
        Assert.Equal(["e4", "e5", "Nf3", "Nc6"], game.Moves);
    }

    [Fact]
    public void ReadsAGameThatStartsFromAPosition()
    {
        const string fromFen = """
            [SetUp "1"]
            [FEN "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1"]

            1. e4 Kd7 2. e5 *
            """;

        Assert.True(Pgn.TryParseFirst(fromFen, out PgnGame game));
        Assert.Equal("4k3/8/8/8/8/8/4P3/4K3 w - - 0 1", game.StartFen);
        Assert.Equal(["e4", "Kd7", "e5"], game.Moves);
    }

    [Fact]
    public void SplitsAFileHoldingSeveralGames()
    {
        string two = Sample + "\n\n" + Sample.Replace("Player A", "Player C");
        List<PgnGame> games = Pgn.ParseAll(two);
        Assert.Equal(2, games.Count);
        Assert.Equal("Player A", games[0].Get("White"));
        Assert.Equal("Player C", games[1].Get("White"));
    }

    [Fact]
    public void EveryMoveResolvesAgainstTheBoard()
    {
        // The point of the reader: what comes out has to be playable. Each
        // token is resolved in the position it belongs to, which is the only
        // thing that can tell a real move from a plausible-looking string.
        Assert.True(Pgn.TryParseFirst(Sample, out PgnGame game));

        var board = new Board(game.StartFen);
        foreach (string san in game.Moves)
        {
            Assert.True(San.TryParse(board, san, out Move move), $"'{san}' did not resolve");
            board.MakeMove(move);
        }
        Assert.Equal(6, game.Moves.Count);
    }

    [Fact]
    public void WrittenGamesReadBackIdentically()
    {
        Assert.True(Pgn.TryParseFirst(Sample, out PgnGame original));

        string written = Pgn.Write(original.Tags, original.Moves, 1, blackMovesFirst: false);

        Assert.True(Pgn.TryParseFirst(written, out PgnGame reread));
        Assert.Equal(original.Moves, reread.Moves);
        Assert.Equal(original.Get("White"), reread.Get("White"));
        Assert.Equal(original.Get("Black"), reread.Get("Black"));
        Assert.Equal(original.Result, reread.Result);
    }

    [Fact]
    public void WritesTheMoveNumbersOfAGameThatStartsOnABlackMove()
    {
        var tags = new Dictionary<string, string> { ["Result"] = "*" };
        string pgn = Pgn.Write(tags, ["Nf6", "c4", "e6"], firstMoveNumber: 12, blackMovesFirst: true);

        // Black's first move carries the number with an ellipsis, and the count
        // only advances after black has moved.
        Assert.Contains("12... Nf6 13. c4 e6 *", pgn);
        Assert.True(Pgn.TryParseFirst(pgn, out PgnGame reread));
        Assert.Equal(["Nf6", "c4", "e6"], reread.Moves);
    }

    [Fact]
    public void BareMovetextWithNoTagsIsStillAGame()
    {
        Assert.True(Pgn.TryParseFirst("1. e4 c5 2. Nf3", out PgnGame game));
        Assert.Equal(["e4", "c5", "Nf3"], game.Moves);
    }
}
