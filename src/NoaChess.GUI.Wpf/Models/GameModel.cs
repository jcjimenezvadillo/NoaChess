using NoaChess.Core;

namespace NoaChess.GUI.Wpf.Models;

// One played move plus everything the GUI needs to show it without recomputing:
// its notation, the move number it belongs to and the side that played it.
// The SAN is captured at the moment the move is played, when the position that
// gives it meaning is still on the board.
// 'Seconds' is how long the player took over it, or 0 when that is not known
// (a game read from a PGN, or one played with no clock). It is recorded because
// where the time went is half of what a review has to say: a mistake made in
// two seconds and one made in two minutes are different mistakes.
public sealed record PlayedMove(Move Move, string San, int MoveNumber, Color Side)
{
    public double Seconds { get; init; }
}

// The game: a start position, the sequence of moves played from it, and a
// cursor saying how many of them are currently on the board.
//
// Navigation is make/unmake on a SINGLE board rather than replaying from the
// start. That is not just faster: the board keeps the position history that
// answers threefold repetition, and rebuilding from a FEN would throw it away,
// so a rewound-and-replayed game would stop seeing its own repetitions.
//
// The model owns no view state and raises no events: the ViewModel drives it
// and refreshes itself afterwards.
public sealed class GameModel
{
    private readonly List<PlayedMove> _moves = [];
    private Board _board = new();

    // Position the start of the game was set up from. Kept for PGN (a game that
    // did not start from the initial array needs a FEN tag) and for resets.
    public string StartFen { get; private set; } = Board.StartFen;

    // The board as it stands at the current cursor. Callers must treat it as
    // read-only: anything that changes the position goes through this class.
    public Board Board => _board;

    // Every move of the game, including the ones ahead of the cursor.
    public IReadOnlyList<PlayedMove> Moves => _moves;

    // How many moves are applied. 0 = start position, Moves.Count = live position.
    public int Ply { get; private set; }

    public bool CanGoBack => Ply > 0;
    public bool CanGoForward => Ply < _moves.Count;

    // True when the cursor sits on the last move, which is the only place where
    // the game can be continued without discarding anything.
    public bool IsAtLivePosition => Ply == _moves.Count;

    public GameResult Result => GameState.GetResult(_board);

    public string CurrentFen => Core.Fen.Save(_board);

    // Restarts from a position ("" or null = the standard start).
    //
    // The tag pairs go with it. They describe THIS game - who played it, where,
    // when - so carrying them into the next one would label a fresh game with
    // the players of the one that was just loaded.
    public void Reset(string? fen = null)
    {
        StartFen = string.IsNullOrWhiteSpace(fen) ? Board.StartFen : fen.Trim();
        _board = new Board(StartFen);
        _moves.Clear();
        Tags.Clear();
        Ply = 0;
    }

    // Plays a move from the current cursor. Anything after the cursor is
    // discarded first: rewinding and playing a different move replaces the
    // continuation, which is how a take-back works here.
    public void Play(Move move, double seconds = 0)
    {
        TruncateHere();

        // The SAN has to be written BEFORE the move is made: it names the piece
        // that is still on the origin square and may need to know which other
        // pieces could have gone to the same destination.
        string san = San.Format(_board, move);
        int number = _board.FullmoveNumber;
        Color side = _board.SideToMove;

        _board.MakeMove(move);
        _moves.Add(new PlayedMove(move, san, number, side) { Seconds = seconds });
        Ply++;
    }

    // Drops every move after the cursor.
    public void TruncateHere()
    {
        if (Ply < _moves.Count)
            _moves.RemoveRange(Ply, _moves.Count - Ply);
    }

    // Moves the cursor to 'ply', undoing or replaying the difference. The
    // target is clamped, so callers can pass int.MaxValue for "the end".
    public void GoTo(int ply)
    {
        ply = Math.Clamp(ply, 0, _moves.Count);
        while (Ply > ply)
        {
            _board.UnmakeMove();
            Ply--;
        }
        while (Ply < ply)
        {
            _board.MakeMove(_moves[Ply].Move);
            Ply++;
        }
    }

    public void GoToStart() => GoTo(0);
    public void GoBack() => GoTo(Ply - 1);
    public void GoForward() => GoTo(Ply + 1);
    public void GoToEnd() => GoTo(_moves.Count);

    // Puts a line back exactly as it was, keeping the tag pairs.
    //
    // Trying a different move REPLACES the continuation, which is what makes
    // exploring one gesture - and it is also what makes practising a game
    // destroy it. Practice snapshots the line first and restores it here
    // between exercises.
    public void RestoreLine(string startFen, IReadOnlyList<Move> moves)
    {
        var kept = new Dictionary<string, string>(Tags, StringComparer.OrdinalIgnoreCase);

        Reset(startFen);
        foreach (Move move in moves)
            Play(move);

        foreach (KeyValuePair<string, string> tag in kept)
            Tags[tag.Key] = tag.Value;
    }

    // The move that led to the current position, or null at the start.
    public PlayedMove? LastMove => Ply > 0 ? _moves[Ply - 1] : null;

    // ---- PGN ----

    // Tag pairs of the game. Owned here because they belong to the game, not to
    // the window: loading a PGN brings them in and saving one writes them back.
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Fills in the tags a game gets when it is played here rather than loaded.
    public void SetDefaultTags(string white, string black)
    {
        Tags["Event"] = "Casual game";
        Tags["Site"] = "NoaChess";
        Tags["Date"] = DateTime.Now.ToString("yyyy.MM.dd");
        Tags["Round"] = "-";
        Tags["White"] = white;
        Tags["Black"] = black;
        Tags["Result"] = "*";
    }

    // Exports the game as PGN. 'result' is the tag value ("1-0", "0-1",
    // "1/2-1/2" or "*"); 'eco' and 'opening' are written as the tags every
    // database indexes games by, and dropped when the game has no name.
    public string ToPgn(string result, string eco = "", string opening = "")
    {
        Tags["Result"] = result;

        if (opening.Length > 0)
        {
            Tags["ECO"] = eco;
            Tags["Opening"] = opening;
        }
        else
        {
            Tags.Remove("ECO");
            Tags.Remove("Opening");
        }

        // A game that did not start from the initial array has to say so, or it
        // reads back as a different game entirely.
        if (StartFen != Core.Board.StartFen)
        {
            Tags["SetUp"] = "1";
            Tags["FEN"] = StartFen;
        }
        else
        {
            Tags.Remove("SetUp");
            Tags.Remove("FEN");
        }

        var start = new Core.Board(StartFen);
        return Pgn.Write(Tags, _moves.Select(m => m.San).ToList(),
                         start.FullmoveNumber, start.SideToMove == Color.Black);
    }

    // Replaces the game with one read from a PGN. Every move is resolved
    // against the position it belongs to, so a token that is not a legal move
    // there stops the load rather than silently skewing the rest of the game.
    //
    // Returns the number of moves loaded; 'problem' is empty when the whole
    // game came through.
    public int LoadPgn(PgnGame pgn, out string problem)
    {
        problem = "";

        try
        {
            Reset(pgn.StartFen);
        }
        catch (Exception ex)
        {
            problem = $"The FEN tag is not a position NoaChess can read: {ex.Message}";
            return 0;
        }

        Tags.Clear();
        foreach (KeyValuePair<string, string> tag in pgn.Tags)
            Tags[tag.Key] = tag.Value;

        for (int i = 0; i < pgn.Moves.Count; i++)
        {
            if (!San.TryParse(_board, pgn.Moves[i], out Move move))
            {
                problem = $"Move {i + 1} of the game, '{pgn.Moves[i]}', is not legal in the "
                        + "position it appears in. The game was loaded up to that point.";
                break;
            }
            Play(move);
        }

        return _moves.Count;
    }
}
