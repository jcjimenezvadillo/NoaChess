using NoaChess.Core;
using NoaChess.Engine.Heuristics;

namespace NoaChess.Engine.Tests;

// Capture-history gravity and its integration in the main capture picker.
// The table must remain bounded under arbitrary UCI depths, and learned
// outcomes must be able to refine (but not erase) the victim-value prior.
public class CaptureHistoryTests
{
    private static int Sq(string algebraic) => Squares.Parse(algebraic);

    [Fact]
    public void Gravity_OversizedUpdatesStayInsideRails()
    {
        var history = new CaptureHistory();
        int piece = ContinuationHistory.PieceIndex(Color.White, PieceType.Queen);
        int to = Sq("d5");
        int victim = (int)PieceType.Pawn;

        history.AddBonus(piece, to, victim, int.MaxValue);
        Assert.Equal(4096, history.Get(piece, to, victim));

        // Repeated oversized evidence stays on the rail instead of wrapping.
        history.AddBonus(piece, to, victim, int.MaxValue);
        Assert.Equal(4096, history.Get(piece, to, victim));

        history.AddMalus(piece, to, victim, int.MinValue);
        Assert.Equal(-4096, history.Get(piece, to, victim));
        history.AddMalus(piece, to, victim, int.MaxValue);
        Assert.Equal(-4096, history.Get(piece, to, victim));
    }

    [Fact]
    public void MainCaptureOrdering_UsesHistoryPlusSevenTimesVictim()
    {
        var board = new Board("k7/3n4/8/8/p2Q4/8/8/7K w - - 0 1");
        Move pawnCapture = new(Sq("d4"), Sq("a4"), MoveFlag.Capture);
        Move knightCapture = new(Sq("d4"), Sq("d7"), MoveFlag.Capture);
        var moves = new MoveList();
        moves.Add(pawnCapture);
        moves.Add(knightCapture);
        var history = new CaptureHistory();

        // With no evidence, 7 x victim keeps the knight capture first.
        MovePicker.ScoreAndSortCaptures(moves, 0, board, history);
        Assert.Equal(knightCapture, moves[0]);

        // Strong positive evidence for Qxa4 outweighs the victim difference.
        int queen = ContinuationHistory.PieceIndex(Color.White, PieceType.Queen);
        history.AddBonus(queen, Sq("a4"), (int)PieceType.Pawn, int.MaxValue);
        MovePicker.ScoreAndSortCaptures(moves, 0, board, history);
        Assert.Equal(pawnCapture, moves[0]);
    }

    [Fact]
    public void CapturePromotion_OrdersQueenBeforeUnderpromotion()
    {
        var board = new Board("r6k/1P6/8/8/8/8/8/7K w - - 0 1");
        Move knightPromotion = new(Sq("b7"), Sq("a8"), MoveFlag.PromoKnightCapture);
        Move queenPromotion = new(Sq("b7"), Sq("a8"), MoveFlag.PromoQueenCapture);
        var moves = new MoveList();
        moves.Add(knightPromotion);
        moves.Add(queenPromotion);

        MovePicker.ScoreAndSortCaptures(moves, 0, board, new CaptureHistory());

        Assert.Equal(queenPromotion, moves[0]);
    }

    [Fact]
    public void QuietOrdering_RewardsSafeDirectCheck()
    {
        var board = new Board("7k/8/8/8/8/8/8/R3K3 w - - 0 1");
        Move quiet = new(Sq("a1"), Sq("a7"), MoveFlag.Quiet);
        Move check = new(Sq("a1"), Sq("a8"), MoveFlag.Quiet);
        var moves = new MoveList();
        moves.Add(quiet);
        moves.Add(check);

        MovePicker.ScoreAndSortQuiets(
            moves, quietsFrom: 0, sortFrom: 0, board,
            new KillerTable(1), new HistoryTable(), ply: 0,
            contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None);

        Assert.Equal(check, moves[0]);
        Assert.True(moves.Scores[0] >= 16_384);
    }

    [Fact]
    public void QuietOrdering_RewardsEscapingLesserPieceThreat()
    {
        var board = new Board("3r3k/8/8/8/3Q4/8/8/7K w - - 0 1");
        Move staysOnRookFile = new(Sq("d4"), Sq("d5"), MoveFlag.Quiet);
        Move escapesRook = new(Sq("d4"), Sq("a4"), MoveFlag.Quiet);
        var moves = new MoveList();
        moves.Add(staysOnRookFile);
        moves.Add(escapesRook);

        MovePicker.ScoreAndSortQuiets(
            moves, quietsFrom: 0, sortFrom: 0, board,
            new KillerTable(1), new HistoryTable(), ply: 0,
            contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None);

        Assert.Equal(escapesRook, moves[0]);
        Assert.True(moves.Scores[0] >= 18_000); // Queen value 900 x weight 20.
    }

    [Fact]
    public void QuietOrdering_PenalizesEnteringLesserPieceThreat()
    {
        var board = new Board("7k/8/8/2p5/8/8/N7/7K w - - 0 1");
        Move entersPawnThreat = new(Sq("a2"), Sq("b4"), MoveFlag.Quiet);
        Move staysSafe = new(Sq("a2"), Sq("c3"), MoveFlag.Quiet);
        var moves = new MoveList();
        moves.Add(entersPawnThreat);
        moves.Add(staysSafe);

        MovePicker.ScoreAndSortQuiets(
            moves, quietsFrom: 0, sortFrom: 0, board,
            new KillerTable(1), new HistoryTable(), ply: 0,
            contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None);

        Assert.Equal(staysSafe, moves[0]);
        Assert.Equal(-6_400, moves.Scores[1]); // Knight value 320 x weight 20.
    }

    [Fact]
    public void PartialQuietSort_OrdersOnlyMovesAboveDepthCutoff()
    {
        var board = new Board();
        Move high = new(Sq("a2"), Sq("a3"), MoveFlag.Quiet);
        Move lowWorst = new(Sq("b2"), Sq("b3"), MoveFlag.Quiet);
        Move highest = new(Sq("c2"), Sq("c3"), MoveFlag.Quiet);
        Move lowBetter = new(Sq("d2"), Sq("d3"), MoveFlag.Quiet);
        var history = new HistoryTable();
        history.AddBonus(Color.White, high, depth: 100);       // +10,000
        history.AddMalus(Color.White, lowWorst, depth: 200);   // -40,000
        history.AddBonus(Color.White, highest, depth: 200);    // +40,000
        history.AddMalus(Color.White, lowBetter, depth: 100);  // -10,000

        var moves = new MoveList();
        moves.Add(high);
        moves.Add(lowWorst);
        moves.Add(highest);
        moves.Add(lowBetter);

        // At depth 3 the cutoff is -9,000. The two positive moves form a
        // sorted prefix; the tail stays deliberately unsorted (-40k, -10k).
        MovePicker.ScoreAndSortQuiets(
            moves, quietsFrom: 0, sortFrom: 0, board,
            new KillerTable(1), history, ply: 0,
            contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None,
            depth: 3);

        Assert.Equal(highest, moves[0]);
        Assert.Equal(high, moves[1]);
        Assert.Equal(lowWorst, moves[2]);
        Assert.Equal(lowBetter, moves[3]);
    }

    [Fact]
    public void PartialQuietSort_KeepsLowQuietsAheadOfLosingCaptures()
    {
        var board = new Board();
        Move losingCapture = new(Sq("a1"), Sq("a8"), MoveFlag.Capture);
        Move lowQuiet = new(Sq("a2"), Sq("a3"), MoveFlag.Quiet);
        var history = new HistoryTable();
        history.AddMalus(Color.White, lowQuiet, depth: 100); // -10,000.

        var moves = new MoveList();
        moves.Add(losingCapture);
        moves.Add(lowQuiet);
        moves.Scores[0] = -5_000_000; // Scored by the preceding capture stage.

        // The quiet is below the depth-3 cutoff (-9,000), but it still belongs
        // to the QUIET stage and must be served before every bad capture.
        MovePicker.ScoreAndSortQuiets(
            moves, quietsFrom: 1, sortFrom: 0, board,
            new KillerTable(1), history, ply: 0,
            contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None,
            depth: 3);

        Assert.Equal(lowQuiet, moves[0]);
        Assert.Equal(losingCapture, moves[1]);
    }
}
