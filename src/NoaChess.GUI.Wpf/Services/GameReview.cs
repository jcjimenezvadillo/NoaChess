using NoaChess.Core;
using NoaChess.Engine.Search;
using NoaChess.GUI.Wpf.Models;
using Color = NoaChess.Core.Color;

namespace NoaChess.GUI.Wpf.Services;

// How badly a move missed. The thresholds are the ones the big analysis sites
// settled on, in centipawns lost against the engine's own choice.
public enum MoveQuality
{
    Normal,
    Inaccuracy, // 50 or more
    Mistake,    // 120 or more
    Blunder,    // 300 or more
    Best,       // the engine's own first choice
}

// One reviewed move.
public sealed record ReviewedMove(int Ply, MoveQuality Quality, int CentipawnLoss,
                                  int ScoreBefore, int ScoreAfter, string BestSan);

// A position where the choice actually mattered.
//
// This is NOT a mistake report. Every chess program marks the moves you got
// wrong; none of them says where the decisions WERE. A position with twenty
// moves inside a tenth of a pawn is not a decision whatever you play; a
// position where the best move is worth a pawn and a half more than the second
// best is the fork in the road, whether or not you happened to take it.
//
// 'Spread' is the gap in centipawns between the best move and the second best:
// how much the choice was worth. 'Seconds' is how long the player actually
// spent on it, which is the other half of the story - the point of knowing
// where the decisions were is to find out whether you were looking.
public sealed record DecisionPoint(int Ply, int MoveNumber, bool WhiteToMove, string Played,
                                   string Best, int Spread, int CentipawnLoss, double Seconds)
{
    public bool TookIt => Played == Best;
}

// What the review found, per side.
public sealed record ReviewSummary(int Moves, int Inaccuracies, int Mistakes, int Blunders,
                                   double AverageLoss, double Accuracy);

// Plays the whole game through the engine and says where it went wrong: the
// "full analysis" every serious chess program offers.
//
// Every position is searched to the SAME depth, which is what makes the losses
// comparable across the game. A time-based budget would make the verdict depend
// on how busy the machine was, and the same move would be a blunder in one run
// and fine in the next.
public sealed class GameReview
{
    // Loss thresholds in centipawns, measured from the mover's point of view.
    private const int InaccuracyLoss = 50;
    private const int MistakeLoss = 120;
    private const int BlunderLoss = 300;

    // Beyond this the game is decided and a further loss means nothing: going
    // from +900 to +600 is not a mistake worth a mark.
    private const int DecidedScore = 800;

    private readonly EngineService _engine;

    public GameReview(EngineService engine) => _engine = engine;

    // Finds the positions where the choice mattered, by scoring every legal
    // move of every position and measuring the gap between the best two.
    //
    // Deliberately SHALLOW. This is a different question from "was that move a
    // mistake": it asks how much the alternatives differed, and that shape shows
    // up long before the exact numbers settle. A deep pass would multiply a
    // thirty-move scan by every position of the game for an answer of the same
    // shape.
    public async Task<List<DecisionPoint>> FindDecisionPointsAsync(
        string startFen, IReadOnlyList<PlayedMove> moves, int depth,
        IProgress<int>? progress, CancellationToken cancellation)
    {
        var points = new List<DecisionPoint>();
        var board = new Board(startFen);

        for (int i = 0; i < moves.Count; i++)
        {
            if (cancellation.IsCancellationRequested)
                break;

            Color mover = board.SideToMove;
            List<Move> legal = MoveGenerator.GenerateLegalMoves(board);

            // With one legal move there was no decision to make, and the
            // position should not compete with the ones where there was.
            if (legal.Count < 2)
            {
                board.MakeMove(moves[i].Move);
                progress?.Report(i + 1);
                continue;
            }

            var scores = new List<(Move Move, int Score)>(legal.Count);
            foreach (Move move in legal)
            {
                if (cancellation.IsCancellationRequested)
                    return points;

                board.MakeMove(move);
                SearchResult result = await _engine.SearchAsync(
                    board, SearchLimits.Depth(Math.Max(1, depth - 1)), null, cancellation);
                board.UnmakeMove();

                // The child's score belongs to the opponent; negate it to get
                // the mover's view, which is the one that ranks the choice.
                scores.Add((move, -result.Score));
            }

            scores.Sort((a, b) => b.Score.CompareTo(a.Score));
            int spread = scores[0].Score - scores[1].Score;

            string bestSan = San.Format(board, scores[0].Move);
            string playedSan = moves[i].San;
            int loss = Math.Max(0, scores[0].Score
                                   - scores.First(x => x.Move == moves[i].Move).Score);

            points.Add(new DecisionPoint(i + 1, board.FullmoveNumber, mover == Color.White,
                                         playedSan, bestSan, spread, loss, moves[i].Seconds));

            board.MakeMove(moves[i].Move);
            progress?.Report(i + 1);
        }

        return points;
    }

    // Reviews every move of 'moves' starting from 'startFen'.
    //
    // 'progress' is called on the caller's thread after each move so a long
    // review can be watched and, through the token, abandoned.
    public async Task<(List<ReviewedMove> Moves, ReviewSummary White, ReviewSummary Black)>
        RunAsync(string startFen, IReadOnlyList<PlayedMove> moves, int depth,
                 IProgress<int>? progress, CancellationToken cancellation)
    {
        var reviewed = new List<ReviewedMove>();
        var board = new Board(startFen);

        // ONE search per position, not two. A search returns the score AND the
        // move it would play, so the position before a move and the position
        // after it are each searched exactly once, and the result of one
        // iteration is the "before" of the next.
        SearchResult before = await _engine.SearchAsync(board, SearchLimits.Depth(depth),
                                                        null, cancellation);
        int scoreBefore = Formatting.ToWhiteScore(before.Score, board.SideToMove);

        for (int i = 0; i < moves.Count; i++)
        {
            if (cancellation.IsCancellationRequested)
                break;

            Color mover = board.SideToMove;
            Move engineChoice = before.BestMove;
            string bestSan = engineChoice == Move.None ? "" : San.Format(board, engineChoice);

            board.MakeMove(moves[i].Move);

            SearchResult after = await _engine.SearchAsync(board, SearchLimits.Depth(depth),
                                                          null, cancellation);
            int scoreAfter = Formatting.ToWhiteScore(after.Score, board.SideToMove);

            // Both scores are white-relative, so the loss for the side that
            // moved is the drop in ITS direction.
            int loss = mover == Color.White ? scoreBefore - scoreAfter : scoreAfter - scoreBefore;
            loss = Math.Max(0, loss);

            reviewed.Add(new ReviewedMove(
                i + 1,
                Classify(loss, scoreBefore, mover, moves[i].Move, engineChoice),
                loss, scoreBefore, scoreAfter, bestSan));

            before = after;
            scoreBefore = scoreAfter;
            progress?.Report(i + 1);
        }

        return (reviewed, Summarise(reviewed, Color.White), Summarise(reviewed, Color.Black));
    }

    private static MoveQuality Classify(int loss, int scoreBefore, Color mover,
                                        Move played, Move best)
    {
        if (played == best)
            return MoveQuality.Best;

        // In a position that is already decided, a further slide is not a
        // mistake anyone needs pointing out.
        int fromMover = mover == Color.White ? scoreBefore : -scoreBefore;
        if (Math.Abs(fromMover) > DecidedScore)
            return MoveQuality.Normal;

        if (loss >= BlunderLoss) return MoveQuality.Blunder;
        if (loss >= MistakeLoss) return MoveQuality.Mistake;
        if (loss >= InaccuracyLoss) return MoveQuality.Inaccuracy;
        return MoveQuality.Normal;
    }

    private static ReviewSummary Summarise(List<ReviewedMove> reviewed, Color side)
    {
        // Ply 1 is white's, so odd plies are white's moves.
        bool white = side == Color.White;
        List<ReviewedMove> mine = reviewed.Where(r => r.Ply % 2 == (white ? 1 : 0)).ToList();
        if (mine.Count == 0)
            return new ReviewSummary(0, 0, 0, 0, 0, 100);

        double averageLoss = mine.Average(r => r.CentipawnLoss);

        // Accuracy from the average loss through the same curve the evaluation
        // bar uses, so "how accurate" and "who is winning" speak one language.
        // A perfect game reads 100 and a hopeless one approaches 0.
        double accuracy = 100.0 * (2.0 / (1.0 + Math.Exp(0.006 * averageLoss)));

        return new ReviewSummary(
            mine.Count,
            mine.Count(r => r.Quality == MoveQuality.Inaccuracy),
            mine.Count(r => r.Quality == MoveQuality.Mistake),
            mine.Count(r => r.Quality == MoveQuality.Blunder),
            averageLoss,
            Math.Clamp(accuracy, 0, 100));
    }
}
